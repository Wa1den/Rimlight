using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Rimlight.Text;

namespace Rimlight.Capture;

public sealed class MonitorInfo
{
    public required string DeviceName { get; init; }   // e.g. \\.\DISPLAY2 - matches DXGI output name
    public required IntPtr Handle { get; init; }       // HMONITOR - what WGC needs
    public required int Left { get; init; }
    public required int Top { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required bool IsPrimary { get; init; }

    /// <summary>Model from EDID, when it could be read. Often blank or cryptic.</summary>
    public string Model { get; init; } = "";

    /// <summary>1-based, in enumeration order - the part a human can actually match up.</summary>
    public int Index { get; init; }

    /// <summary>Readable identity without the size, for compact places.</summary>
    public string DisplayName
    {
        get
        {
            string name = Index > 0 ? Loc.P($"Экран {Index}", $"Screen {Index}") : DeviceName;
            if (!string.IsNullOrWhiteSpace(Model)) name += " — " + Model;
            return name;
        }
    }

    public override string ToString() =>
        $"{DisplayName}  {Width}x{Height}{(IsPrimary ? "  (основной)" : "")}";
}

public static class Native
{
    // ---- DPI ----------------------------------------------------------------
    // Without per-monitor-v2 awareness the monitor rectangles come back scaled,
    // which silently misplaces every capture region on a mixed-DPI setup.
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // ---- Monitor enumeration -----------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    // ---- monitor model ------------------------------------------------------
    // EnumDisplayDevices only ever says "Generic PnP Monitor", so the real name comes from
    // the EDID blob the driver stashes in the registry for that monitor instance.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplayDevicesW(string? device, uint num, ref DISPLAY_DEVICE info, uint flags);

    const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    static string GetMonitorModel(string deviceName)
    {
        try
        {
            var dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
            if (!EnumDisplayDevicesW(deviceName, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME)) return "";

            // \?\DISPLAY#SAM0E0F#5&15ec5605&0&UID4355#{guid}
            var parts = dd.DeviceID.Split('#');
            if (parts.Length < 3) return "";

            string key = $@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters";
            using var reg = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            if (reg?.GetValue("EDID") is not byte[] edid || edid.Length < 128) return "";

            return ParseEdidName(edid);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>EDID keeps four 18-byte descriptors; type 0xFC holds the model name.</summary>
    static string ParseEdidName(byte[] edid)
    {
        for (int off = 54; off + 18 <= edid.Length && off <= 108; off += 18)
        {
            if (edid[off] != 0 || edid[off + 1] != 0 || edid[off + 2] != 0 || edid[off + 3] != 0xFC)
                continue;

            var chars = new System.Text.StringBuilder();
            for (int i = off + 5; i < off + 18; i++)
            {
                byte b = edid[i];
                if (b == 0x0A) break;
                if (b >= 32 && b < 127) chars.Append((char)b);
            }
            return chars.ToString().Trim();
        }
        return "";
    }

    delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW info);

    const uint MONITORINFOF_PRIMARY = 1;

    public static List<MonitorInfo> EnumerateMonitors()
    {
        var result = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            if (GetMonitorInfoW(hMon, ref mi))
            {
                result.Add(new MonitorInfo
                {
                    DeviceName = mi.szDevice,
                    Handle = hMon,
                    Left = mi.rcMonitor.Left,
                    Top = mi.rcMonitor.Top,
                    Width = mi.rcMonitor.Right - mi.rcMonitor.Left,
                    Height = mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    IsPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    Model = GetMonitorModel(mi.szDevice),
                    Index = result.Count + 1
                });
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    // ---- Desktop ------------------------------------------------------------
    // Prismatik asks for DESKTOP_SWITCHDESKTOP - the right to *switch* desktops -
    // when all it needs is to attach a thread for reading, and then closes the
    // handle with CloseHandle while the thread is still bound to it. Both are
    // avoided here; that is the whole point of the DDA arm of this probe.
    public const uint DESKTOP_READOBJECTS = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    public const int ERROR_ACCESS_DENIED = 5;

    // ---- Windows ------------------------------------------------------------
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder name, int count);

    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public static string GetClassName(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(128);
        return GetClassNameW(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new System.Text.StringBuilder(256);
        int n = GetWindowTextW(hWnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "";
    }

    // ---- GDI ----------------------------------------------------------------
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr CreateDCW(string driver, string device, string? port, IntPtr devMode);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr point);

    [DllImport("gdi32.dll")]
    public static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
                                         IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
                                       byte[] bits, ref BITMAPINFO bmi, uint usage);

    public const int HALFTONE = 4;
    public const int COLORONCOLOR = 3;
    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;
    public const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }
}
