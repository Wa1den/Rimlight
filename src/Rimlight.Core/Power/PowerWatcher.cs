using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Rimlight.Capture;
using Rimlight.Text;

namespace Rimlight.Power;

/// <summary>Whether anybody could see the lights right now.</summary>
public readonly record struct PowerState(bool DisplayOff, bool Locked, bool Suspended)
{
    public bool AnythingHidden => DisplayOff || Locked || Suspended;
}

/// <summary>
/// Reports when nobody can see the lights: screen blanked, session locked, machine asleep.
///
/// Only reports - what to do about it is the caller's decision, since one consumer drives a
/// strip over a serial port and another drives a case through a separate server. Lock and
/// sleep arrive through SystemEvents; display power does not, and needs an explicit power
/// setting registration against a window handle.
/// </summary>
public sealed class PowerWatcher : IDisposable
{
    static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    const int WM_POWERBROADCAST = 0x0218;
    const int PBT_POWERSETTINGCHANGE = 0x8013;
    const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    const int WTS_CURRENT_SESSION = -1;
    const int WTSSessionInfoEx = 25;
    const int WTS_SESSIONSTATE_LOCK = 0;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass,
                                                  out IntPtr buffer, out int bytesReturned);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);

    /// <summary>
    /// The head of WTSINFOEX_LEVEL1_W. The union it sits in is aligned to 8 by the
    /// LARGE_INTEGERs further down, so it starts one pointer past the level field.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct WtsInfoLevel1Head
    {
        public uint SessionId;
        public int SessionState;
        public int SessionFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    HwndSource? _source;
    IntPtr _registration = IntPtr.Zero;

    bool _displayOff, _locked, _suspended;

    /// <summary>Raised whenever any of the three conditions changes.</summary>
    public event EventHandler<PowerState>? Changed;

    /// <summary>
    /// Ticks up on every wake. Hardware that was re-enumerated during sleep is not ready
    /// the instant Windows says "resumed" - OpenRGB was seen dying 41 seconds after a wake
    /// - so a consumer can use this to hold off rather than hammer a half-initialised bus.
    /// </summary>
    public long LastResumeTicks { get; private set; }

    public PowerState State => new(_displayOff, _locked, _suspended);

    public PowerWatcher()
    {
        _locked = QueryLocked();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>
    /// Whether the session is locked right now.
    ///
    /// Registering for display power returns the current state straight away. Lock state
    /// arrives only with the next switch, so a start into an already locked session read
    /// as unlocked and the strip stayed lit.
    /// </summary>
    static bool QueryLocked()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, WTS_CURRENT_SESSION, WTSSessionInfoEx,
                                            out buffer, out int bytes) ||
                bytes < IntPtr.Size + Marshal.SizeOf<WtsInfoLevel1Head>())
                return false;

            var head = Marshal.PtrToStructure<WtsInfoLevel1Head>(buffer + IntPtr.Size);
            return head.SessionFlags == WTS_SESSIONSTATE_LOCK;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    /// <summary>Display power only reaches a window, so one has to be handed over.</summary>
    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _source = HwndSource.FromHwnd(helper.Handle);
        if (_source == null) return;

        _source.AddHook(WndProc);
        Guid guid = GUID_CONSOLE_DISPLAY_STATE;
        _registration = RegisterPowerSettingNotification(helper.Handle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_POWERBROADCAST && (int)wParam == PBT_POWERSETTINGCHANGE)
        {
            var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
            if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
            {
                // 0 = off, 1 = on, 2 = dimmed
                _displayOff = setting.Data == 0;
                ProbeLog.Log(Loc.P("питание", "power"), _displayOff ? Loc.P("экран выключен", "display off") : Loc.P("экран включён", "display on"));
                Raise();
            }
        }
        return IntPtr.Zero;
    }

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect)
            _locked = true;
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect)
            _locked = false;
        else return;

        ProbeLog.Log(Loc.P("питание", "power"), _locked ? Loc.P("сессия заблокирована", "session locked") : Loc.P("сессия разблокирована", "session unlocked"));
        Raise();
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _suspended = true;
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _suspended = false;
            LastResumeTicks = Environment.TickCount64;
        }
        else return;

        ProbeLog.Log(Loc.P("питание", "power"), _suspended ? Loc.P("уход в сон", "going to sleep") : Loc.P("пробуждение", "resumed"));
        Raise();
    }

    void Raise() => Changed?.Invoke(this, State);

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        if (_registration != IntPtr.Zero) UnregisterPowerSettingNotification(_registration);
        _source?.RemoveHook(WndProc);
    }
}
