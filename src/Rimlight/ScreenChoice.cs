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

    /// <summary>The same against a fresh enumeration, for callers that hold no list.</summary>
    public static MonitorInfo? Find(string deviceName, string model) =>
        Find(Native.EnumerateMonitors(), deviceName, model);
}
