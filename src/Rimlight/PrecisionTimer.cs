using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Rimlight;

/// <summary>
/// Sub-millisecond pacing.
///
/// Thread.Sleep rounds up to the system timer tick, 15.625 ms by default. Asking for the
/// 16.6 ms of a 60 fps frame therefore actually waits 31.25 ms, pinning output at about
/// 32 fps no matter how fast capture runs. A high-resolution waitable timer gets the real
/// interval without raising the timer resolution process-wide, which would cost battery
/// and affect every other program.
/// </summary>
public sealed class PrecisionTimer : IDisposable
{
    const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    const uint TIMER_ALL_ACCESS = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period,
                                        IntPtr routine, IntPtr arg, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    IntPtr _handle;

    public PrecisionTimer()
    {
        _handle = CreateWaitableTimerExW(IntPtr.Zero, null,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);

        // pre-1803 Windows has no high-resolution timers; fall back to Sleep
        if (_handle == IntPtr.Zero)
            Capture.ProbeLog.Log("таймер", "высокоточный таймер недоступен, пейсинг через Sleep");
    }

    public void Wait(double milliseconds)
    {
        if (milliseconds <= 0) return;

        if (_handle == IntPtr.Zero)
        {
            Thread.Sleep((int)Math.Max(1, milliseconds));
            return;
        }

        // negative due time is relative, in 100 ns units
        long due = -(long)(milliseconds * 10_000.0);
        if (due == 0) return;

        if (SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            WaitForSingleObject(_handle, (uint)Math.Max(1, milliseconds * 2));
        else
            Thread.Sleep((int)Math.Max(1, milliseconds));
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
