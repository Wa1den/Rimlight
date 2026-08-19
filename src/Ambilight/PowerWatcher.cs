using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using Ambilight.Capture;

namespace Ambilight;

/// <summary>
/// Turns the strip off when nobody can see it: screen blanked, session locked, machine
/// asleep. Lock and sleep arrive through SystemEvents; display power does not, and needs
/// an explicit power-setting registration against a window handle.
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

    [StructLayout(LayoutKind.Sequential)]
    struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    readonly AmbilightEngine _engine;
    readonly Func<AmbilightConfig> _config;
    HwndSource? _source;
    IntPtr _registration = IntPtr.Zero;

    bool _displayOff, _locked, _suspended;

    public PowerWatcher(AmbilightEngine engine, Func<AmbilightConfig> config)
    {
        _engine = engine;
        _config = config;

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

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
                ProbeLog.Log("питание", _displayOff ? "экран выключен" : "экран включён");
                Apply();
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

        ProbeLog.Log("питание", _locked ? "сессия заблокирована" : "сессия разблокирована");
        Apply();
    }

    void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _suspended = true;
        else if (e.Mode == PowerModes.Resume) _suspended = false;
        else return;

        ProbeLog.Log("питание", _suspended ? "уход в сон" : "пробуждение");
        Apply();
    }

    void Apply()
    {
        var cfg = _config();

        string? reason =
            _suspended && cfg.OffOnSuspend ? "сон" :
            _locked && cfg.OffOnLock ? "блокировка" :
            _displayOff && cfg.OffOnDisplayOff ? "экран выключен" :
            null;

        if (reason != null) _engine.Pause(reason);
        else _engine.Resume();
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        if (_registration != IntPtr.Zero) UnregisterPowerSettingNotification(_registration);
        _source?.RemoveHook(WndProc);
    }
}
