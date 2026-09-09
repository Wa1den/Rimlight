using System.Collections.Generic;
using System.Linq;
using Rimlight.Capture;

namespace Rimlight;

/// <summary>
/// Matches the screen named in the settings against the screens actually attached.
///
/// <c>\\.\DISPLAY2</c> is not an identity. Windows hands those names out in the order it finds
/// the outputs, so moving a cable between ports of the graphics card renumbers them: in one
/// observed case an ultrawide and a portrait screen swapped names between sessions, which
/// would have pointed the capture at the wrong screen and sized the zones for the wrong
/// panel. The model out of EDID survives that, so it is asked first, and the device name is
/// kept only to tell apart two screens of the same model.
/// </summary>
public static class ScreenChoice
{
    /// <summary>The screen the settings point at, or the primary one when it is gone.</summary>
    public static MonitorInfo? Find(IReadOnlyList<MonitorInfo> monitors, string deviceName, string model)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            var sameModel = monitors.Where(m => m.Model == model).ToList();
            if (sameModel.Count == 1) return sameModel[0];

            // два экрана одной модели: различить их можно только по имени устройства
            var exact = sameModel.FirstOrDefault(m => m.DeviceName == deviceName);
            if (exact != null) return exact;
        }

        // Настройки без модели пишет версия до этой правки, и там имя устройства - всё, что
        // есть. Оно же остаётся последней зацепкой, когда сохранённой модели среди
        // подключённых экранов больше нет.
        return monitors.FirstOrDefault(m => m.DeviceName == deviceName)
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? monitors.FirstOrDefault();
    }

    /// <summary>
    /// The same screen or none, for a display configuration that changed under a running
    /// capture.
    ///
    /// <see cref="Find"/> falls back to the primary screen, which is what a start-up needs
    /// and the wrong answer here: it would move the light onto another panel and size the
    /// zones for it without anybody asking. The one screen left attached is an exception,
    /// because settings written for a monitor whose EDID gave no model have nothing but
    /// the device name to go on, and the device name is exactly what a driver restart
    /// renumbers.
    /// </summary>
    public static MonitorInfo? FindSame(IReadOnlyList<MonitorInfo> monitors, string deviceName, string model)
    {
        if (!string.IsNullOrWhiteSpace(model))
        {
            var sameModel = monitors.Where(m => m.Model == model).ToList();

            // ни одного экрана этой модели: панель отключили, а не переименовали
            if (sameModel.Count == 0) return null;
            if (sameModel.Count == 1) return sameModel[0];

            // два экрана одной модели различает только имя устройства, и когда оно
            // сменилось, сказать, который из них тот самый, нечем
            return sameModel.FirstOrDefault(m => m.DeviceName == deviceName);
        }

        return monitors.FirstOrDefault(m => m.DeviceName == deviceName)
            ?? (monitors.Count == 1 ? monitors[0] : null);
    }

    /// <summary>Whether capture can carry on untouched: same panel, same handle, same size.</summary>
    public static bool Same(MonitorInfo? a, MonitorInfo? b) =>
        a != null && b != null &&
        a.DeviceName == b.DeviceName && a.Handle == b.Handle &&
        a.Width == b.Width && a.Height == b.Height;

    /// <summary>The same against a fresh enumeration, for callers that hold no list.</summary>
    public static MonitorInfo? Find(string deviceName, string model) =>
        Find(Native.EnumerateMonitors(), deviceName, model);
}
