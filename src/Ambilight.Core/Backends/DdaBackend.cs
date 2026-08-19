using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ResultCode = Vortice.DXGI.ResultCode;

namespace Ambilight.Capture.Backends;

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
                    sw.Restart();
                    var res = dup.AcquireNextFrame(100, out OutduplFrameInfo info, out IDXGIResource? resource);

                    if (res.Code == ResultCode.WaitTimeout.Code)
                    {
                        // nothing new on screen, but the previous frame's reduction may
                        // still be sitting in the ring unread
                        if (reducer.TryDrain(out byte dr, out byte dg, out byte db, out bool dblack))
                        {
                            PublishImage(reducer.LastImage, reducer.ImageWidth, reducer.ImageHeight, reducer.ImageStride);
                            Metrics.NoteFrame(dr, dg, db, dblack, 0, 0);
                        }
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
                        var (r, g, b, black, valid) = reducer.Reduce(tex);
                        double reduceMs = sw.Elapsed.TotalMilliseconds;

                        if (!valid) continue;   // ring still warming up
                        if (TakeSnapshotRequest())
                        {
                            var px = reducer.TryGrabSnapshot(out int sw2, out int sh2, out int stride2);
                            if (px != null) Snapshot.Save(Name, px, sw2, sh2, stride2);
                        }

                        PublishImage(reducer.LastImage, reducer.ImageWidth, reducer.ImageHeight, reducer.ImageStride);
                        Metrics.NoteFrame(r, g, b, black, acquireMs, reduceMs);
                        Metrics.NoteSkipped(reducer.Skipped);
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
