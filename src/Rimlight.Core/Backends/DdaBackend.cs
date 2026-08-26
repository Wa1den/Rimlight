using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ResultCode = Vortice.DXGI.ResultCode;

namespace Rimlight.Capture.Backends;

/// <summary>
/// Desktop Duplication, written the way Prismatik's DDuplGrabber should have been:
///
///   1. asks for DESKTOP_READOBJECTS, not DESKTOP_SWITCHDESKTOP - it only needs to
///      attach a thread for reading, and the wider the right requested, the more
///      readily Windows refuses it;
///   2. never closes the desktop handle the thread is currently bound to, and uses
///      CloseDesktop rather than CloseHandle on a USER object.
///
/// If the outage disappears here but persists in Prismatik, the bug was those four lines.
/// </summary>
public sealed class DdaBackend : CaptureBackendBase
{
    public override string Name => "DDA";

    /// <summary>
    /// How long AcquireNextFrame is allowed to block.
    ///
    /// It used to be 100 ms, which is also how long a reduction could sit unread in the
    /// ring after the last change: the drain only runs on the timeout branch, and on a
    /// screen that moves once and then stops there is no next frame to push it out.
    /// Waking eight times a second more often costs nothing and caps that at one tick.
    /// </summary>
    const int AcquireTimeoutMs = 8;

    /// <summary>
    /// How long to leave a reduction sitting in the ring before looking at it again.
    ///
    /// Paced here rather than through the timeout of AcquireNextFrame, which is a kernel
    /// wait like any other: asking it for one millisecond is not a promise of one
    /// millisecond, and the rounding it does is the very thing being chased out.
    /// </summary>
    const double DrainPollMs = 0.5;

    readonly PrecisionTimer _pacer = new();

    IntPtr _originalDesk = IntPtr.Zero;
    IntPtr _currentDesk = IntPtr.Zero;

    protected override void RunLoop()
    {
        _originalDesk = Native.GetThreadDesktop(Native.GetCurrentThreadId());
        try
        {
            while (ShouldRun)
            {
                try
                {
                    SessionLoop();
                }
                catch (Exception ex)
                {
                    Metrics.NoteError(Short(ex));
                    ProbeLog.LogStatusChange(Name, BackendStatus.Error, Short(ex));
                    Sleep(500);
                }
            }
        }
        finally
        {
            // restore first, then close - the inverse of Prismatik's mistake
            if (_originalDesk != IntPtr.Zero) Native.SetThreadDesktop(_originalDesk);
            if (_currentDesk != IntPtr.Zero) { Native.CloseDesktop(_currentDesk); _currentDesk = IntPtr.Zero; }
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _pacer.Dispose();
    }

    void Sleep(int ms)
    {
        int slept = 0;
        while (slept < ms && ShouldRun) { Thread.Sleep(25); slept += 25; }
    }

    /// <summary>Binds this thread to the current input desktop. Returns false if denied.</summary>
    bool AttachToInputDesktop()
    {
        IntPtr desk = Native.OpenInputDesktop(0, true, Native.DESKTOP_READOBJECTS);
        if (desk == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            string msg = err == Native.ERROR_ACCESS_DENIED
                ? "отказ доступа к input desktop"
                : $"OpenInputDesktop ошибка {err}";
            Metrics.NoteError(msg);
            ProbeLog.LogStatusChange(Name, BackendStatus.Error, msg);
            return false;
        }

        if (!Native.SetThreadDesktop(desk))
        {
            int err = Marshal.GetLastWin32Error();
            Native.CloseDesktop(desk);
            Metrics.NoteError($"SetThreadDesktop ошибка {err}");
            return false;
        }

        // safe to release the previous one only now that the thread moved off it
        if (_currentDesk != IntPtr.Zero) Native.CloseDesktop(_currentDesk);
        _currentDesk = desk;
        return true;
    }

    void SessionLoop()
    {
        if (!AttachToInputDesktop()) { Sleep(1000); return; }

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        IDXGIAdapter1? chosenAdapter = null;
        IDXGIOutput? chosenOutput = null;

        for (uint ai = 0; factory.EnumAdapters1(ai, out IDXGIAdapter1 adapter).Success; ai++)
        {
            bool used = false;
            for (uint oi = 0; adapter.EnumOutputs(oi, out IDXGIOutput output).Success; oi++)
            {
                if (output.Description.DeviceName == Monitor.DeviceName)
                {
                    chosenAdapter = adapter;
                    chosenOutput = output;
                    used = true;
                    break;
                }
                output.Dispose();
            }
            if (used) break;
            adapter.Dispose();
        }

        if (chosenAdapter == null || chosenOutput == null)
        {
            Metrics.NoteError($"выход {Monitor.DeviceName} не найден");
            Sleep(1000);
            return;
        }

        // the device must live on the adapter that owns the output
        D3D11.D3D11CreateDevice(chosenAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            null!, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();

        using (chosenAdapter)
        using (chosenOutput)
        using (device)
        using (context)
        using (var output1 = chosenOutput.QueryInterface<IDXGIOutput1>())
        using (var reducer = new GpuReducer(device, context, ReduceWidth))
        {
            IDXGIOutputDuplication? dup = null;
            try
            {
                try
                {
                    dup = output1.DuplicateOutput(device);
                }
                catch (SharpGen.Runtime.SharpGenException ex)
                {
                    string msg = ex.ResultCode.Code == ResultCode.AccessDenied.Code
                        ? "DuplicateOutput: отказ доступа"
                        : $"DuplicateOutput: {ex.ResultCode.Description}";
                    Metrics.NoteError(msg);
                    ProbeLog.LogStatusChange(Name, BackendStatus.Error, msg);
                    Sleep(1000);
                    return;
                }

                LogOutputColorInfo(chosenOutput);
                ProbeLog.Log(Name, "дублирование создано");
                Metrics.NoteStatus(BackendStatus.Starting, "дублирование создано");

                var sw = new Stopwatch();
                while (ShouldRun)
                {
                    // With a reduction still in flight, do not settle in to wait for the
                    // next frame - the one already queued will be ready long before it.
                    // Ask whether anything is there, and if not, come back in half a
                    // millisecond to see whether the GPU has finished.
                    bool draining = reducer.HasPending;

                    sw.Restart();
                    var res = dup.AcquireNextFrame(draining ? 0u : AcquireTimeoutMs,
                                                   out OutduplFrameInfo info, out IDXGIResource? resource);

                    if (res.Code == ResultCode.WaitTimeout.Code)
                    {
                        // nothing new on screen, but the previous frame's reduction may
                        // still be sitting in the ring unread
                        if (reducer.TryDrain(out byte dr, out byte dg, out byte db, out bool dblack))
                        {
                            PublishImage(reducer.LastImage, reducer.ImageWidth, reducer.ImageHeight,
                                         reducer.ImageStride, reducer.LastImageStamps);
                            Metrics.NoteFrame(dr, dg, db, dblack, 0, 0);
                        }
                        else if (draining) _pacer.Wait(DrainPollMs);

                        // Only a real one counts. A poll we asked to return immediately is
                        // not the screen going quiet, and counting it as one would paint
                        // the whole status strip amber through every game.
                        else Metrics.NoteTimeout();
                        continue;
                    }

                    if (res.Failure)
                    {
                        resource?.Dispose();
                        HandleAcquireFailure(res);
                        return;   // rebuild the whole session
                    }

                    double acquireMs = sw.Elapsed.TotalMilliseconds;
                    long acquiredAt = Stopwatch.GetTimestamp();

                    try
                    {
                        // LastPresentTime == 0 means cursor-only movement, not new picture
                        if (info.LastPresentTime == 0)
                            continue;

                        // arriving faster than the strip is driven: release and move on
                        if (!ReduceDue())
                            continue;

                        using var tex = resource!.QueryInterface<ID3D11Texture2D>();
                        sw.Restart();
                        var (r, g, b, black, valid) = reducer.Reduce(tex, PresentStamp(info), acquiredAt);
                        double reduceMs = sw.Elapsed.TotalMilliseconds;

                        if (!valid) continue;   // ring still warming up
                        if (TakeSnapshotRequest())
                        {
                            var px = reducer.TryGrabSnapshot(out int sw2, out int sh2, out int stride2);
                            if (px != null) Snapshot.Save(Name, px, sw2, sh2, stride2);
                        }

                        // The card was busy and this is an older slot - but the frame that
                        // replaces it is already queued, and the poll above will have it
                        // within a millisecond or two. Sending both means the output thread
                        // wakes twice per frame and the port throws away the fresher of the
                        // two for arriving too soon after the staler one.
                        Metrics.NoteSkipped(reducer.Skipped);
                        if (!reducer.LastReadFresh) continue;

                        PublishImage(reducer.LastImage, reducer.ImageWidth, reducer.ImageHeight,
                                     reducer.ImageStride, reducer.LastImageStamps);
                        Metrics.NoteFrame(r, g, b, black, acquireMs, reduceMs);
                        if (black) ProbeLog.LogStatusChange(Name, BackendStatus.Black, "ЧЁРНЫЙ КАДР");
                        else ProbeLog.LogStatusChange(Name, BackendStatus.Ok, "OK");
                    }
                    finally
                    {
                        resource?.Dispose();
                        dup.ReleaseFrame();
                    }
                }
            }
            finally
            {
                dup?.Dispose();
            }
        }
    }

    /// <summary>
    /// When the frame was actually put on screen, on the Stopwatch clock.
    ///
    /// LastPresentTime is a QPC value, which is the clock Stopwatch reads - so this is a
    /// true "picture appeared" mark rather than "we got round to it". Trusted only while
    /// it reads as one: a value from another epoch would quietly turn the latency figure
    /// into noise, and no measurement is better than a wrong one.
    /// </summary>
    static long PresentStamp(in OutduplFrameInfo info)
    {
        long now = Stopwatch.GetTimestamp();
        long present = info.LastPresentTime;
        long age = now - present;
        return present > 0 && age >= 0 && age < Stopwatch.Frequency ? present : now;
    }

    /// <summary>
    /// If the desktop is in HDR the plain DuplicateOutput path behaves differently from
    /// SDR, so record what the output is actually running before drawing conclusions.
    /// </summary>
    void LogOutputColorInfo(IDXGIOutput output)
    {
        try
        {
            using var o6 = output.QueryInterface<IDXGIOutput6>();
            var d = o6.Description1;
            ProbeLog.Log(Name, $"выход: цвет.простр={d.ColorSpace} бит/канал={d.BitsPerColor} " +
                               $"макс.яркость={d.MaxLuminance} режим={d.AttachedToDesktop}");
        }
        catch (Exception ex)
        {
            ProbeLog.Log(Name, "не удалось прочитать IDXGIOutput6: " + ex.Message);
        }
    }

    void HandleAcquireFailure(SharpGen.Runtime.Result res)
    {
        if (res.Code == ResultCode.AccessLost.Code)
        {
            // expected on fullscreen entry / mode change - just rebuild
            ProbeLog.LogStatusChange(Name, BackendStatus.Error, "ACCESS_LOST, пересоздание");
            Metrics.NoteStatus(BackendStatus.Starting, "ACCESS_LOST, пересоздание");
        }
        else if (res.Code == ResultCode.AccessDenied.Code)
        {
            Metrics.NoteError("ACCESS_DENIED");
            ProbeLog.LogStatusChange(Name, BackendStatus.Error, "ACCESS_DENIED");
            Sleep(500);
        }
        else if (res.Code == ResultCode.DeviceRemoved.Code || res.Code == ResultCode.DeviceReset.Code)
        {
            Metrics.NoteError("устройство сброшено");
            ProbeLog.LogStatusChange(Name, BackendStatus.Error, "устройство сброшено");
            Sleep(500);
        }
        else
        {
            Metrics.NoteError($"AcquireNextFrame: {res.Description}");
            ProbeLog.LogStatusChange(Name, BackendStatus.Error, $"AcquireNextFrame: {res.Description}");
            Sleep(500);
        }
    }
}
