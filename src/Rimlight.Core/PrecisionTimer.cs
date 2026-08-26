using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Rimlight.Capture;

/// <summary>
/// Sub-millisecond pacing.
///
/// Thread.Sleep rounds up to the system timer tick, 15.625 ms by default. Asking for the
/// 16.6 ms of a 60 fps frame therefore actually waits 31.25 ms, pinning output at about
/// 32 fps no matter how fast capture runs. A high-resolution waitable timer gets the real
/// interval without raising the timer resolution process-wide, which would cost battery
/// and affect every other program.
///
/// The same rounding applies to a wait with a timeout, which is why <see cref="Handle"/>
/// exists: pacing that also has to wake early on an event cannot use the timeout argument
/// of WaitAny and must put this timer into the handle array instead.
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

    /// <summary>
    /// The timer as a WaitHandle, for callers that wait on it together with other objects.
    /// Null where no high-resolution timer could be created.
    /// </summary>
    public WaitHandle? Handle { get; }

    public PrecisionTimer()
    {
        _handle = CreateWaitableTimerExW(IntPtr.Zero, null,
            CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);

        // pre-1803 Windows has no high-resolution timers; fall back to Sleep
        if (_handle == IntPtr.Zero)
        {
            ProbeLog.Log("таймер", "высокоточный таймер недоступен, пейсинг через Sleep");
            return;
        }

        // ownsHandle: false - CloseHandle below is ours, and doing it twice would throw
        Handle = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(_handle, false) };
    }

    /// <summary>
    /// Starts the countdown without waiting for it. Pair with <see cref="Handle"/> in a
    /// WaitAny; returns false where the timer is unavailable and the caller must fall back.
    /// </summary>
    public bool Arm(double milliseconds)
    {
        if (_handle == IntPtr.Zero) return false;

        // negative due time is relative, in 100 ns units
        long due = -(long)(milliseconds * 10_000.0);
        if (due >= 0) due = -1;
        return SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
    }

    public void Wait(double milliseconds)
    {
        if (milliseconds <= 0) return;

        if (_handle == IntPtr.Zero)
        {
            Thread.Sleep((int)Math.Max(1, milliseconds));
            return;
        }

        if (Arm(milliseconds))
            WaitForSingleObject(_handle, (uint)Math.Max(1, milliseconds * 2));
        else
            Thread.Sleep((int)Math.Max(1, milliseconds));
    }

    public void Dispose()
    {
        Handle?.Dispose();
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
