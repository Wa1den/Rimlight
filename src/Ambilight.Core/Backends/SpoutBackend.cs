using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace Ambilight.Capture.Backends;

/// <summary>
/// Receives OBS's canvas as a shared D3D11 texture via Spout2.
///
/// The point is not that OBS captures better than we can - it is that OBS's game capture
/// hook is whitelisted by anti-cheat vendors, so the injection is done by software that is
/// allowed to do it while we only read the result. Spout shares the texture in video memory
/// with no encoding, so this stays on the same GPU-resident path as the other backends.
///
/// Requires OBS running with the Spout2 plugin and a "Spout Output" enabled.
/// </summary>
public sealed class SpoutBackend : CaptureBackendBase
{
    public override string Name => "Spout";

    public const string SetupNote = "нужен OBS + плагин Spout2";

    const string SenderNamesMap = "SpoutSenderNames";
    const string ActiveSenderMap = "ActiveSenderName";
    const int SenderNameLength = 256;
    const int MaxSenders = 10;
    const int PollHz = 120;

    /// <summary>
    /// Layout of Spout's per-sender shared memory block. The share handle comes FIRST -
    /// reading it as width/height/handle/format yields a nonsense 2147486082x3440.
    /// </summary>
    readonly record struct SenderInfo(uint ShareHandle, uint Width, uint Height, uint Format);

    protected override void RunLoop()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            null!, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();

        using (device)
        using (context)
        using (var reducer = new GpuReducer(device, context, ReduceWidth))
        {
            while (ShouldRun)
            {
                try
                {
                    ReceiveLoop(device, context, reducer);
                }
                catch (Exception ex)
                {
                    Metrics.NoteError(Short(ex));
                    ProbeLog.LogStatusChange(Name, BackendStatus.Error, Short(ex));
                    Sleep(1000);
                }
            }
        }
    }

    void Sleep(int ms)
    {
        int slept = 0;
        while (slept < ms && ShouldRun) { Thread.Sleep(25); slept += 25; }
    }

    void ReceiveLoop(ID3D11Device device, ID3D11DeviceContext context, GpuReducer reducer)
    {
        // OBS republishes under a new name (OBS_Spout -> OBS_Spout_1) and leaves the old
        // entry in Spout's registry with a dead handle, so the first name is not
        // necessarily a live one. Take the first candidate that actually opens.
        string? sender = null;
        SenderInfo? info = null;
        ID3D11Texture2D? shared = null;

        foreach (var candidate in EnumerateSenders())
        {
            var ci = ReadSenderInfo(candidate);
            if (ci == null || ci.Value.ShareHandle == 0) continue;

            try
            {
                shared = device.OpenSharedResource<ID3D11Texture2D>(new IntPtr(ci.Value.ShareHandle));
                sender = candidate;
                info = ci;
                ProbeLog.Log(Name, $"отправитель \"{candidate}\" {ci.Value.Width}x{ci.Value.Height} " +
                                   $"формат={ci.Value.Format} handle=0x{ci.Value.ShareHandle:X}");
                break;
            }
            catch (Exception ex)
            {
                ProbeLog.Log(Name, $"отправитель \"{candidate}\" не открылся (устаревшая запись): {ex.Message}");
            }
        }

        if (shared == null || sender == null || info == null)
        {
            Metrics.NoteStatus(BackendStatus.Starting, "жду живого отправителя Spout (OBS)");
            Sleep(1000);
            return;
        }

        Mutex? access = null;
        try { access = Mutex.OpenExisting(sender + "_SpoutAccessMutex"); }
        catch { /* older senders may not publish one; copying without it is still fine */ }

        // private copy, so the cross-process lock is held only for the copy itself
        using var temp = device.CreateTexture2D(new Texture2DDescription
        {
            Width = info.Value.Width,
            Height = info.Value.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });

        Metrics.NoteStatus(BackendStatus.Starting, $"подключён к \"{sender}\"");

        var sw = new Stopwatch();
        var pace = new Stopwatch();
        double periodMs = 1000.0 / PollHz;
        ulong lastHash = 0;
        bool first = true;
        int sinceRecheck = 0;
        long lastChangeTicks = Environment.TickCount64;
        long lastStarveLog = 0;

        try
        {
            while (ShouldRun)
            {
                pace.Restart();

                // The sender can change size, republish under the same name, or - the case
                // that actually bit us - be superseded by a brand new sender while the old
                // entry lingers, frozen. Watch for all three.
                if (++sinceRecheck >= PollHz / 4)
                {
                    sinceRecheck = 0;

                    var now = ReadSenderInfo(sender);
                    if (now == null || now.Value.ShareHandle != info.Value.ShareHandle ||
                        now.Value.Width != info.Value.Width || now.Value.Height != info.Value.Height)
                    {
                        ProbeLog.Log(Name, "запись отправителя изменилась, пересоздание");
                        return;
                    }

                    string? active = ReadFixedString(ActiveSenderMap, SenderNameLength);
                    if (!string.IsNullOrWhiteSpace(active) && active != sender)
                    {
                        ProbeLog.Log(Name, $"активным стал другой отправитель \"{active}\" (был \"{sender}\"), пересоздание");
                        return;
                    }

                    // catch-all: a frozen texture means we are glued to something dead,
                    // so rescan rather than sit on it forever
                    if (Environment.TickCount64 - lastChangeTicks > 5000)
                    {
                        ProbeLog.Log(Name, "картинка заморожена 5 с, пересканирование отправителей");
                        return;
                    }
                }

                sw.Restart();
                bool locked = access?.WaitOne(50) ?? true;
                try
                {
                    context.CopyResource(temp, shared);
                    // without this the copy sits in our queue and can read stale content
                    context.Flush();
                }
                finally
                {
                    if (locked) access?.ReleaseMutex();
                }
                double acquireMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                var (r, g, b, black, valid) = reducer.Reduce(temp);
                double reduceMs = sw.Elapsed.TotalMilliseconds;

                if (valid)
                {
                    if (TakeSnapshotRequest())
                    {
                        var px = reducer.TryGrabSnapshot(out int sw2, out int sh2, out int stride2);
                        if (px != null) Snapshot.Save(Name, px, sw2, sh2, stride2);
                    }

                    // Spout has no frame signal we read here, so a poll whose content did not
                    // change counts as "no new frame" - the same meaning as a DDA timeout.
                    bool changed = first || reducer.LastHash != lastHash;
                    first = false;
                    lastHash = reducer.LastHash;

                    if (changed)
                    {
                        lastChangeTicks = Environment.TickCount64;
                        PublishImage(reducer.LastImage, reducer.ImageWidth, reducer.ImageHeight, reducer.ImageStride);
                        Metrics.NoteFrame(r, g, b, black, acquireMs, reduceMs);
                        Metrics.NoteSkipped(reducer.Skipped);
                        if (black) ProbeLog.LogStatusChange(Name, BackendStatus.Black, "ЧЁРНЫЙ КАДР");
                        else ProbeLog.LogStatusChange(Name, BackendStatus.Ok, "OK");
                    }
                    else
                    {
                        Metrics.NoteTimeout();

                        // Distinguish "OBS is not updating the texture" from "we read it wrong":
                        // if the content is frozen, report what Spout's registry still advertises.
                        long now = Environment.TickCount64;
                        if (now - lastChangeTicks > 3000 && now - lastStarveLog > 5000)
                        {
                            lastStarveLog = now;
                            var cur = ReadSenderInfo(sender);
                            string list = string.Join(", ", EnumerateSenders());
                            ProbeLog.Log(Name,
                                $"картинка не меняется {(now - lastChangeTicks) / 1000.0:F0} с; " +
                                $"текущий \"{sender}\" handle=0x{info.Value.ShareHandle:X} -> " +
                                (cur == null ? "запись пропала" :
                                 $"сейчас {cur.Value.Width}x{cur.Value.Height} handle=0x{cur.Value.ShareHandle:X}") +
                                $"; отправители: [{list}]");
                        }
                    }
                }

                double restMs = periodMs - pace.Elapsed.TotalMilliseconds;
                if (restMs > 0.5) Thread.Sleep((int)Math.Max(1, restMs));
            }
        }
        finally
        {
            shared.Dispose();
            access?.Dispose();
        }
    }

    /// <summary>Active sender first, then everything in Spout's registry.</summary>
    static System.Collections.Generic.List<string> EnumerateSenders()
    {
        var result = new System.Collections.Generic.List<string>();

        string? active = ReadFixedString(ActiveSenderMap, SenderNameLength);
        if (!string.IsNullOrWhiteSpace(active)) result.Add(active);

        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(SenderNamesMap, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewStream(0, MaxSenders * SenderNameLength, MemoryMappedFileAccess.Read);
            var buf = new byte[MaxSenders * SenderNameLength];
            view.ReadExactly(buf, 0, buf.Length);

            for (int i = 0; i < MaxSenders; i++)
            {
                string name = ReadCString(buf, i * SenderNameLength, SenderNameLength);
                if (!string.IsNullOrWhiteSpace(name) && !result.Contains(name)) result.Add(name);
            }
        }
        catch (FileNotFoundException) { /* nothing is sending */ }
        catch { /* ignore and report as no sender */ }

        return result;
    }

    static SenderInfo? ReadSenderInfo(string sender)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(sender, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewStream(0, 16, MemoryMappedFileAccess.Read);
            using var br = new BinaryReader(view);
            uint handle = br.ReadUInt32();
            uint w = br.ReadUInt32();
            uint h = br.ReadUInt32();
            uint format = br.ReadUInt32();
            if (w == 0 || h == 0 || w > 16384 || h > 16384) return null;
            return new SenderInfo(handle, w, h, format);
        }
        catch
        {
            return null;
        }
    }

    static string? ReadFixedString(string mapName, int length)
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
            using var view = mmf.CreateViewStream(0, length, MemoryMappedFileAccess.Read);
            var buf = new byte[length];
            view.ReadExactly(buf, 0, buf.Length);
            return ReadCString(buf, 0, length);
        }
        catch
        {
            return null;
        }
    }

    static string ReadCString(byte[] buf, int offset, int max)
    {
        int end = offset;
        while (end < offset + max && buf[end] != 0) end++;
        return Encoding.ASCII.GetString(buf, offset, end - offset).Trim();
    }
}
