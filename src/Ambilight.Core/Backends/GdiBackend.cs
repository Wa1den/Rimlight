using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ambilight.Capture.Backends;

/// <summary>
/// Plain GDI StretchBlt - the slow, old path. Included as the control arm: Asus Armory
/// Crate keeps working in the problem game but with visible lag, which is exactly how a
/// GDI grabber behaves, so this arm tests whether that is what it falls back on.
///
/// Deliberately capped at 60 fps. It is a polling loop, and this probe runs while a game
/// is running - pegging a core to discover a number we do not need would be rude. If it
/// holds 60 here, it can do 60.
/// </summary>
public sealed class GdiBackend : CaptureBackendBase
{
    public override string Name => "GDI";

    // HALFTONE box-filters properly but costs ~60 ms at 3440x1440. COLORONCOLOR just
    // picks nearest pixels, which is far cheaper; blitting into a larger intermediate
    // and averaging that on the CPU keeps the zone colours honest anyway.
    const int TargetFps = 60;

    public const string PacingNote = "ограничен 60 к/с намеренно";

    protected override void RunLoop()
    {
        int SmallWidth = ReduceWidth;
        int smallH = Math.Max(1, SmallWidth * Monitor.Height / Math.Max(1, Monitor.Width));

        IntPtr screenDC = Native.CreateDCW("DISPLAY", Monitor.DeviceName, null, IntPtr.Zero);
        if (screenDC == IntPtr.Zero)
        {
            Metrics.NoteError("CreateDC не удался");
            ProbeLog.LogStatusChange(Name, BackendStatus.Error, "CreateDC не удался");
            return;
        }

        IntPtr memDC = Native.CreateCompatibleDC(screenDC);
        IntPtr bmp = Native.CreateCompatibleBitmap(screenDC, SmallWidth, smallH);
        IntPtr oldBmp = Native.SelectObject(memDC, bmp);
        Native.SetStretchBltMode(memDC, Native.COLORONCOLOR);

        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = SmallWidth,
                biHeight = -smallH,      // negative = top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            }
        };

        var buffer = new byte[SmallWidth * smallH * 4];
        var sw = new Stopwatch();
        var pace = new Stopwatch();
        double periodMs = 1000.0 / TargetFps;

        ProbeLog.Log(Name, $"старт, {SmallWidth}x{smallH}, {PacingNote}");
        Metrics.NoteStatus(BackendStatus.Starting, "запущен");

        try
        {
            while (ShouldRun)
            {
                pace.Restart();
                sw.Restart();

                bool ok = Native.StretchBlt(memDC, 0, 0, SmallWidth, smallH,
                                            screenDC, 0, 0, Monitor.Width, Monitor.Height,
                                            Native.SRCCOPY | Native.CAPTUREBLT);
                double acquireMs = sw.Elapsed.TotalMilliseconds;

                if (!ok)
                {
                    Metrics.NoteError($"StretchBlt ошибка {Marshal.GetLastWin32Error()}");
                    ProbeLog.LogStatusChange(Name, BackendStatus.Error, "StretchBlt не удался");
                    Thread.Sleep(200);
                    continue;
                }

                sw.Restart();
                // the bitmap must not be selected into a DC while GetDIBits reads it
                Native.SelectObject(memDC, oldBmp);
                int lines = Native.GetDIBits(memDC, bmp, 0, (uint)smallH, buffer, ref bmi, Native.DIB_RGB_COLORS);
                Native.SelectObject(memDC, bmp);

                if (lines == 0)
                {
                    Metrics.NoteError("GetDIBits вернул 0");
                    Thread.Sleep(200);
                    continue;
                }

                var (r, g, b, black) = ColorMath.AverageBgra(buffer, SmallWidth, smallH, SmallWidth * 4);
                double reduceMs = sw.Elapsed.TotalMilliseconds;

                if (TakeSnapshotRequest())
                    SaveBigSnapshot(screenDC);

                PublishImage(buffer, SmallWidth, smallH, SmallWidth * 4);
                Metrics.NoteFrame(r, g, b, black, acquireMs, reduceMs);
                if (black) ProbeLog.LogStatusChange(Name, BackendStatus.Black, "ЧЁРНЫЙ КАДР");
                else ProbeLog.LogStatusChange(Name, BackendStatus.Ok, "OK");

                double restMs = periodMs - pace.Elapsed.TotalMilliseconds;
                if (restMs > 1) Thread.Sleep((int)restMs);
            }
        }
        finally
        {
            Native.SelectObject(memDC, oldBmp);
            Native.DeleteObject(bmp);
            Native.DeleteDC(memDC);
            Native.DeleteDC(screenDC);
        }
    }

    /// <summary>One-off larger grab, big enough to recognise by eye.</summary>
    void SaveBigSnapshot(IntPtr screenDC)
    {
        const int W = 256;
        int h = Math.Max(1, W * Monitor.Height / Math.Max(1, Monitor.Width));

        IntPtr dc = Native.CreateCompatibleDC(screenDC);
        IntPtr bmp = Native.CreateCompatibleBitmap(screenDC, W, h);
        IntPtr old = Native.SelectObject(dc, bmp);
        Native.SetStretchBltMode(dc, Native.HALFTONE);
        Native.SetBrushOrgEx(dc, 0, 0, IntPtr.Zero);

        try
        {
            if (!Native.StretchBlt(dc, 0, 0, W, h, screenDC, 0, 0, Monitor.Width, Monitor.Height,
                                   Native.SRCCOPY | Native.CAPTUREBLT))
                return;

            var bmi = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = W,
                    biHeight = -h,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            var px = new byte[W * h * 4];
            Native.SelectObject(dc, old);
            if (Native.GetDIBits(dc, bmp, 0, (uint)h, px, ref bmi, Native.DIB_RGB_COLORS) > 0)
                Snapshot.Save(Name, px, W, h, W * 4);
            Native.SelectObject(dc, bmp);
        }
        finally
        {
            Native.SelectObject(dc, old);
            Native.DeleteObject(bmp);
            Native.DeleteDC(dc);
        }
    }
}
