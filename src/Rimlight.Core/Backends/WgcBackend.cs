using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Rimlight.Capture.Backends;

public enum WgcTarget
{
    /// <summary>Whole monitor. Goes through desktop composition.</summary>
    Monitor,

    /// <summary>
    /// The foreground window. Taps the application's own presentation rather than
    /// desktop composition, which is why this keeps working when a game is promoted
    /// to an independent flip / overlay plane and monitor capture goes silent.
    /// </summary>
    ForegroundWindow
}

/// <summary>
/// Windows.Graphics.Capture. Needs no OpenInputDesktop and no thread-to-desktop binding.
///
/// Two targets are offered because they fail differently: monitor capture starves when a
/// game bypasses DWM composition - measured here as "no new frames" the moment gameplay
/// starts - while window capture follows the application's own swapchain.
/// </summary>
public sealed class WgcBackend : CaptureBackendBase
{
    readonly WgcTarget _target;

    public WgcBackend(WgcTarget target) => _target = target;

    public override string Name => _target == WgcTarget.Monitor ? "WGC-экран" : "WGC-окно";

    static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid ID3D11Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    static readonly Guid IDirect3DDxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, [In] ref Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    long _lastFrameTicks;
    volatile bool _itemClosed;
    IntPtr _capturedWindow;

    protected override void RunLoop()
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            Metrics.NoteError("WGC не поддерживается системой");
            ProbeLog.Log(Name, "GraphicsCaptureSession.IsSupported() == false");
            return;
        }

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

    void Sleep(int ms)
    {
        int slept = 0;
        while (slept < ms && ShouldRun) { Thread.Sleep(25); slept += 25; }
    }

    /// <summary>
    /// Picks a sensible capture target window. Our own window and anything tiny is
    /// ignored, so alt-tabbing to the probe does not retarget it away from the game.
    /// </summary>
    static bool IsSuitableTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindowVisible(hwnd)) return false;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;   // that is us

        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        if (r.Right - r.Left < 400 || r.Bottom - r.Top < 300) return false;

        return Native.GetWindowTitle(hwnd).Length > 0;
    }

    /// <summary>
    /// Prefers the foreground window, but skips our own - otherwise alt-tabbing to the
    /// probe (to press a button) would either retarget it at itself or, on startup, leave
    /// it with no target at all. Falls back to the topmost suitable window in Z order.
    /// </summary>
    static IntPtr PickForegroundWindow(IntPtr current)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (IsSuitableTarget(fg)) return fg;

        // foreground is ours (or unusable) - keep what we had if it is still valid
        if (current != IntPtr.Zero && Native.IsWindow(current) && IsSuitableTarget(current))
            return current;

        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (!IsSuitableTarget(h)) return true;   // EnumWindows walks in Z order
            found = h;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    void SessionLoop()
    {
        _itemClosed = false;

        GraphicsCaptureItem item;
        if (_target == WgcTarget.Monitor)
        {
            item = CreateItemForMonitor(Monitor.Handle);
            ProbeLog.Log(Name, $"цель: монитор {Monitor.DeviceName}");
        }
        else
        {
            IntPtr hwnd = PickForegroundWindow(IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                Metrics.NoteStatus(BackendStatus.Starting, "жду подходящее окно");
                Sleep(500);
                return;
            }
            _capturedWindow = hwnd;
            item = CreateItemForWindow(hwnd);
            ProbeLog.Log(Name, $"цель: окно 0x{hwnd.ToInt64():X} \"{Native.GetWindowTitle(hwnd)}\"");
        }

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            null!, out ID3D11Device device, out ID3D11DeviceContext context).CheckError();

        using (device)
        using (context)
        using (var reducer = new GpuReducer(device, context, ReduceWidth))
        {
            IDirect3DDevice winrtDevice = CreateWinRtDevice(device);

            item.Closed += (_, _) =>
            {
                _itemClosed = true;
                ProbeLog.LogStatusChange(Name, BackendStatus.Error, "источник закрыт");
            };

            var size = item.Size;
            if (size.Width <= 0 || size.Height <= 0)
                size = new SizeInt32 { Width = Monitor.Width, Height = Monitor.Height };

            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);

            var session = pool.CreateCaptureSession(item);
            TrySetSessionOptions(session);

            var sw = new Stopwatch();
            pool.FrameArrived += (s, _) =>
            {
                using var frame = s.TryGetNextFrame();
                if (frame == null) return;

                try
                {
                    Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                    if (!ReduceDue()) return;   // faster than the strip needs

                    sw.Restart();
                    using var tex = GetTexture(frame.Surface);
                    double acquireMs = sw.Elapsed.TotalMilliseconds;

                    sw.Restart();
                    var (r, g, b, black, valid) = reducer.Reduce(tex);
                    double reduceMs = sw.Elapsed.TotalMilliseconds;

                    Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                    if (!valid) return;   // ring still warming up (inside the FrameArrived callback)

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
                catch (Exception ex)
                {
                    Metrics.NoteError(Short(ex));
                    ProbeLog.LogStatusChange(Name, BackendStatus.Error, Short(ex));
                }
            };

            session.StartCapture();
            ProbeLog.Log(Name, "сессия захвата запущена");
            Metrics.NoteStatus(BackendStatus.Starting, "сессия запущена");
            Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);

            // frames arrive on pool callbacks; this thread only supervises
            while (ShouldRun && !_itemClosed)
            {
                Thread.Sleep(100);

                long last = Interlocked.Read(ref _lastFrameTicks);
                double idleMs = (DateTime.UtcNow.Ticks - last) / (double)TimeSpan.TicksPerMillisecond;
                if (idleMs > 250)
                {
                    if (reducer.TryDrain(out byte dr, out byte dg, out byte db, out bool dblack))
                    {
                        PublishImage(reducer.LastImage, reducer.ImageWidth, reducer.ImageHeight, reducer.ImageStride);
                        Metrics.NoteFrame(dr, dg, db, dblack, 0, 0);
                        Interlocked.Exchange(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                        continue;
                    }

                    Metrics.NoteTimeout();
                    if (idleMs > 3000) LogStarvation(idleMs);
                }

                if (_target == WgcTarget.ForegroundWindow)
                {
                    IntPtr next = PickForegroundWindow(_capturedWindow);
                    if (next != _capturedWindow || !Native.IsWindow(_capturedWindow))
                    {
                        ProbeLog.Log(Name, $"смена окна -> 0x{next.ToInt64():X} \"{Native.GetWindowTitle(next)}\"");
                        break;   // rebuild the session against the new window
                    }
                }
            }

            session.Dispose();
            if (_itemClosed)
            {
                Metrics.NoteError("источник закрыт, пересоздание");
                Sleep(300);
            }
        }
    }

    long _lastStarvationLog;

    /// <summary>Records what is actually on screen while no frames arrive.</summary>
    void LogStarvation(double idleMs)
    {
        long now = Environment.TickCount64;
        if (now - _lastStarvationLog < 5000) return;
        _lastStarvationLog = now;

        IntPtr fg = Native.GetForegroundWindow();
        Native.GetWindowRect(fg, out var r);
        long style = Native.GetWindowLongPtr(fg, Native.GWL_STYLE).ToInt64();
        long ex = Native.GetWindowLongPtr(fg, Native.GWL_EXSTYLE).ToInt64();

        // WS_POPUP without WS_BORDER covering exactly the monitor is the signature of a
        // borderless-or-exclusive fullscreen presentation
        bool coversMonitor = (r.Right - r.Left) >= Monitor.Width && (r.Bottom - r.Top) >= Monitor.Height;

        ProbeLog.Log(Name, $"без кадров {idleMs:F0} мс; активное окно 0x{fg.ToInt64():X} " +
                           $"\"{Native.GetWindowTitle(fg)}\" класс={Native.GetClassName(fg)} " +
                           $"{r.Right - r.Left}x{r.Bottom - r.Top} @ {r.Left},{r.Top} " +
                           $"style=0x{style:X8} exstyle=0x{ex:X8} накрывает_монитор={coversMonitor}; " +
                           $"цель 0x{_capturedWindow.ToInt64():X}");
    }

    static void TrySetSessionOptions(GraphicsCaptureSession session)
    {
        try { session.IsCursorCaptureEnabled = false; }
        catch (Exception ex) { ProbeLog.Log("WGC", "IsCursorCaptureEnabled недоступно: " + ex.Message); }

        // Windows 11 only; without it the capture draws a yellow border
        try { session.IsBorderRequired = false; }
        catch (Exception ex) { ProbeLog.Log("WGC", "IsBorderRequired недоступно: " + ex.Message); }
    }

    static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        uint hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr abi);
        if (hr != 0) throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice: 0x{hr:X8}");
        try
        {
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    static IGraphicsCaptureItemInterop GetInterop()
    {
        var factory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        return factory.AsInterface<IGraphicsCaptureItemInterop>();
    }

    static GraphicsCaptureItem CreateItemForMonitor(IntPtr hMonitor)
    {
        Guid iid = GraphicsCaptureItemIid;
        IntPtr abi = GetInterop().CreateForMonitor(hMonitor, ref iid);
        if (abi == IntPtr.Zero) throw new InvalidOperationException("CreateForMonitor вернул null");
        try { return GraphicsCaptureItem.FromAbi(abi); }
        finally { Marshal.Release(abi); }
    }

    static GraphicsCaptureItem CreateItemForWindow(IntPtr hWnd)
    {
        Guid iid = GraphicsCaptureItemIid;
        IntPtr abi = GetInterop().CreateForWindow(hWnd, ref iid);
        if (abi == IntPtr.Zero) throw new InvalidOperationException("CreateForWindow вернул null");
        try { return GraphicsCaptureItem.FromAbi(abi); }
        finally { Marshal.Release(abi); }
    }

    /// <summary>
    /// Pulls the underlying ID3D11Texture2D out of a WinRT surface. A plain cast to a
    /// ComImport interface does not work here - under CsWinRT the projected object is a
    /// WinRT wrapper, not a COM RCW - so this queries and calls through the vtable directly.
    /// </summary>
    static unsafe ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr surfacePtr = WinRT.MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Guid accessIid = IDirect3DDxgiInterfaceAccessIid;
            int hr = Marshal.QueryInterface(surfacePtr, in accessIid, out IntPtr accessPtr);
            if (hr != 0)
                throw new InvalidOperationException($"QI IDirect3DDxgiInterfaceAccess: 0x{hr:X8}");

            try
            {
                // vtable slots: 0 QueryInterface, 1 AddRef, 2 Release, 3 GetInterface
                var vtbl = *(void***)accessPtr;
                var getInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[3];

                Guid texIid = ID3D11Texture2DIid;
                IntPtr texPtr;
                int hr2 = getInterface(accessPtr, &texIid, &texPtr);
                if (hr2 != 0)
                    throw new InvalidOperationException($"GetInterface(ID3D11Texture2D): 0x{hr2:X8}");

                return new ID3D11Texture2D(texPtr);
            }
            finally { Marshal.Release(accessPtr); }
        }
        finally { Marshal.Release(surfacePtr); }
    }
}
