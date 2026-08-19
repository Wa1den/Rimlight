using System;
using System.Runtime.InteropServices;

namespace Ambilight.Capture.Backends;

/// <summary>
/// A cheap "is anything on screen moving?" check that does not read the composition path,
/// so it can answer while DDA and WGC are both silent.
///
/// Deliberately not the full GDI backend: spinning that up for the answer meant ~40 blits
/// at 60 Hz plus a thread and a device context, started and stopped every time the desktop
/// went quiet. That churn was enough to visibly disturb the mouse cursor. Two small blits
/// a few hundred milliseconds apart answer the same question.
/// </summary>
public static class GdiProbe
{
    const int Width = 64;

    /// <summary>Hash of a coarse grab of the monitor, or 0 if it could not be read.</summary>
    public static ulong Sample(MonitorInfo monitor)
    {
        int height = Math.Max(1, Width * monitor.Height / Math.Max(1, monitor.Width));

        IntPtr screenDC = Native.CreateDCW("DISPLAY", monitor.DeviceName, null, IntPtr.Zero);
        if (screenDC == IntPtr.Zero) return 0;

        IntPtr memDC = IntPtr.Zero, bmp = IntPtr.Zero, oldBmp = IntPtr.Zero;
        try
        {
            memDC = Native.CreateCompatibleDC(screenDC);
            bmp = Native.CreateCompatibleBitmap(screenDC, Width, height);
            if (memDC == IntPtr.Zero || bmp == IntPtr.Zero) return 0;

            oldBmp = Native.SelectObject(memDC, bmp);
            Native.SetStretchBltMode(memDC, Native.COLORONCOLOR);

            if (!Native.StretchBlt(memDC, 0, 0, Width, height,
                                   screenDC, 0, 0, monitor.Width, monitor.Height,
                                   Native.SRCCOPY | Native.CAPTUREBLT))
                return 0;

            var bmi = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = Width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };

            var buf = new byte[Width * height * 4];
            Native.SelectObject(memDC, oldBmp);
            int lines = Native.GetDIBits(memDC, bmp, 0, (uint)height, buf, ref bmi, Native.DIB_RGB_COLORS);
            Native.SelectObject(memDC, bmp);
            if (lines == 0) return 0;

            ulong h = 14695981039346656037UL;
            for (int i = 0; i < buf.Length; i++)
            {
                h ^= buf[i];
                h *= 1099511628211UL;
            }
            return h == 0 ? 1UL : h;    // 0 is reserved for "could not read"
        }
        finally
        {
            if (oldBmp != IntPtr.Zero && memDC != IntPtr.Zero) Native.SelectObject(memDC, oldBmp);
            if (bmp != IntPtr.Zero) Native.DeleteObject(bmp);
            if (memDC != IntPtr.Zero) Native.DeleteDC(memDC);
            Native.DeleteDC(screenDC);
        }
    }
}
