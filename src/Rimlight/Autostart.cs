using System;
using Microsoft.Win32;
using Rimlight.Capture;

namespace Rimlight;

/// <summary>
/// Per-user autostart through the standard Run key. User scope only - no elevation, and
/// nothing outside this account is touched.
/// </summary>
public static class Autostart
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Rimlight";

    /// <summary>Entry left behind by the previous name; removed so it cannot start a
    /// second copy from a path that no longer exists.</summary>
    const string LegacyValueName = "Ambilight";

    static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key == null) return;

            // whatever happens, the old entry must not survive
            if (key.GetValue(LegacyValueName) != null)
            {
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                ProbeLog.Log("автозапуск", "убрана запись под старым именем");
            }

            if (enabled)
            {
                if (string.IsNullOrEmpty(ExePath)) return;
                key.SetValue(ValueName, "\"" + ExePath + "\"");
                ProbeLog.Log("автозапуск", "включён: " + ExePath);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                ProbeLog.Log("автозапуск", "выключен");
            }
        }
        catch (Exception ex)
        {
            ProbeLog.Log("автозапуск", "не удалось изменить: " + ex.Message);
        }
    }
}
