using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Rimlight.Capture;

namespace Rimlight.Text;

/// <summary>
/// Strings by key. Built-in defaults are written out as JSON next to the config on first
/// run, and whatever is on disk wins afterwards - so translations can be corrected or new
/// languages added without touching the program.
/// </summary>
public static class Loc
{
    /// <summary>
    /// Bumped whenever the built-in strings change. Files on disk deliberately win over
    /// the built-ins so translations can be corrected - but that also meant an old file
    /// silently shadowed newly reworded labels, so a mismatched version rewrites it.
    /// </summary>
    const string Version = "8";

    public static string Language { get; private set; } = "ru";

    static Dictionary<string, string> _current = new();

    /// <summary>
    /// Set by the application on startup. The library has no config file of its own, and
    /// two programs share these strings, so neither may assume the other's folder.
    /// </summary>
    public static string Directory { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rimlight", "lang");

    public static void Configure(string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory)) Directory = directory;
    }

    public static readonly string[] Available = { "ru", "en" };

    public static string DisplayName(string code) => code switch
    {
        "ru" => "Русский",
        "en" => "English",
        _ => code
    };

    public static void Load(string language)
    {
        Language = Array.IndexOf(Available, language) >= 0 ? language : "ru";
        WriteDefaults();

        var defaults = Builtin(Language);
        try
        {
            string path = Path.Combine(Directory, Language + ".json");
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (loaded != null)
                    foreach (var kv in loaded) defaults[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex)
        {
            ProbeLog.Log("lang", P("не удалось прочитать перевод: ", "could not read translation: ") + ex.Message);
        }

        _current = defaults;
    }

    /// <summary>Missing keys fall back to the key itself, so nothing ever renders blank.</summary>
    public static string T(string key) => _current.TryGetValue(key, out var v) ? v : key;

    /// <summary>
    /// A translated pair written inline, for one-off runtime text: log lines, device
    /// statuses, the words inside a statistics line.
    ///
    /// These are not worth dictionary keys. A key pays off when a string is reused or
    /// handed to a translator; a hundred and some log messages that each appear once would
    /// only turn into a hundred names nobody ever looks up, and the English would sit far
    /// away from the code that emits it.
    /// </summary>
    public static string P(string ru, string en) => Language == "en" ? en : ru;

    static void WriteDefaults()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            // without the relaxed encoder every Cyrillic character lands as a numeric
            // escape, which makes a file meant for hand editing unreadable
            var opts = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            foreach (var code in Available)
            {
                string path = Path.Combine(Directory, code + ".json");
                if (File.Exists(path) && CurrentVersionOf(path) == Version) continue;

                File.WriteAllText(path, JsonSerializer.Serialize(Builtin(code), opts));
            }
        }
        catch (Exception ex)
        {
            ProbeLog.Log("lang", "не удалось записать переводы: " + ex.Message);
        }
    }

    static string CurrentVersionOf(string path)
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return d != null && d.TryGetValue("_version", out var v) ? v : "";
        }
        catch
        {
            return "";
        }
    }

    static Dictionary<string, string> Builtin(string code)
    {
        var d = code == "en" ? English() : Russian();
        d["_version"] = Version;
        return d;
    }

    static Dictionary<string, string> Russian() => new()
    {
        ["tab.main"] = "Основное",
        ["tab.device"] = "Устройство",
        ["tab.layout"] = "Раскладка",
        ["tab.color"] = "Цвет",
        ["tab.capture"] = "Захват",
        ["tab.power"] = "Питание",
        ["tab.about"] = "О программе",

        ["main.boost"] = "Усилить превью",
        ["main.boost.note"] = "Поднимает яркость только на экране. Светодиод при значении 30 хорошо виден в тёмной комнате, а монитор рисует его почти чёрным.",
        ["main.startmin"] = "Запускать свёрнутым в трей",
        ["about.head"] = "Rimlight",
        ["about.version"] = "Версия {0}",
        ["about.text"] = "Фоновая подсветка монитора по картинке с экрана: края кадра усредняются по зонам и уходят на адресную ленту через COM-порт по протоколу Adalight.",
        ["about.text2"] = "Захват идёт лестницей — Desktop Duplication, Windows Graphics Capture, GDI — и переключается сам, когда быстрый путь перестаёт отдавать кадры, чтобы подсветка не замирала. Кадрами может делиться с модулем подсветки корпуса.",
        ["about.repo"] = "Репозиторий проекта:",
        ["about.firmware"] = "Прошивка контроллера и исходная задумка:",
        ["main.stats"] = "Отображать статистику",
        ["main.stats.note"] = "Блок статистики под превью: метод захвата, частота кадров, состояние порта.",
        ["nav.preview"] = "Показывать превью",
        ["main.tray"] = "Сворачивать в трей",
        ["main.autostart"] = "Запускать вместе с Windows",
        ["main.log"] = "Писать лог",
        ["main.log.note"] = "Лог складывается рядом с настройками.",
        ["main.language"] = "Язык",
        ["main.language.note"] = "Переводы лежат в JSON рядом с настройками, их можно править и дополнять.",
        ["main.export"] = "Экспорт настроек",
        ["main.import"] = "Импорт",
        ["main.exit"] = "Выход",
        ["main.paths"] = "Настройки: {0}",

        ["device.monitor"] = "Монитор",
        ["device.port"] = "COM-порт",
        ["device.baud"] = "Скорость, бод",
        ["device.baud.note"] = "Та же скорость должна быть выставлена в параметрах прошивки контроллера, иначе связи не будет.",
        ["device.apply"] = "Применить и переподключиться",

        ["layout.top"] = "Сверху",
        ["layout.bottom"] = "Снизу",
        ["layout.left"] = "Слева",
        ["layout.right"] = "Справа",
        ["layout.corner"] = "Стартовый угол",
        ["layout.corner.br"] = "правый нижний",
        ["layout.corner.bl"] = "левый нижний",
        ["layout.corner.tl"] = "левый верхний",
        ["layout.corner.tr"] = "правый верхний",
        ["layout.ccw"] = "Против часовой стрелки",
        ["layout.offset"] = "Смещение, диодов",
        ["layout.margin"] = "Отступ от углов по горизонтали, % ширины",
        ["layout.marginV"] = "Отступ от углов по вертикали, % ширины",
        ["layout.depth"] = "Глубина зоны, % ширины",
        ["layout.note"] = "Все проценты считаются от ширины экрана, чтобы значения были сопоставимы в пикселях. Отступы по горизонтали и по вертикали задаются независимо.",
        ["layout.total"] = "Всего диодов: {0}",

        ["color.brightness"] = "Максимальная яркость",
        ["color.minluma"] = "Порог темноты",
        ["color.saturation"] = "Насыщенность",
        ["color.gamma"] = "Гамма",
        ["color.temperature"] = "Цветовая температура, K",
        ["color.gainR"] = "Усиление R",
        ["color.gainG"] = "Усиление G",
        ["color.gainB"] = "Усиление B",
        ["color.dither"] = "Дизеринг",
        ["color.dither.note"] = "Сглаживает ступени на тёмных сценах, перенося ошибку округления на соседний диод.",
        ["color.rise"] = "Сглаживание: подъём",
        ["color.rise.note"] = "Сглаживание изменений во времени: какую долю пути до нового, более яркого цвета диод проходит за кадр. Меньше значение — плавнее, но подсветка сильнее запаздывает.",
        ["color.fall"] = "Сглаживание: спад",
        ["color.fall.note"] = "То же при затемнении. Спад обычно ставят медленнее подъёма: свет гаснет мягко, и подсветка не мерцает на тёмных сценах.",

        ["capture.method"] = "Метод захвата",
        ["capture.auto"] = "Авто",
        ["capture.dda"] = "Только DDA",
        ["capture.wgc"] = "Только WGC",
        ["capture.gdi"] = "Только GDI (медленный)",
        ["capture.method.note"] = "Авто использует быстрые методы и подстраховывается медленным, когда они не отдают кадры; применяется сразу. DDA (Desktop Duplication) — самый быстрый захват экрана. WGC (Windows Graphics Capture) — чуть медленнее, но стабильнее в играх и полноэкранных приложениях. GDI — самый медленный, зато работает почти везде.",
        ["capture.fps"] = "Потолок частоты, к/с",
        ["capture.onchange"] = "Слать только при смене цветов",
        ["capture.onchange.note"] = "Прошивка гасит ленту после 10 с молчания, поэтому одинаковый кадр всё равно повторяется раз в 2 с.",
        ["capture.publish"] = "Отдавать снимки экрана в модуль подсветки",
        ["capture.publish.note"] = "Кадр кладётся в разделяемую память, откуда его берёт CaseLight — подсветка корпуса. Своего захвата ему тогда не нужно. Без слушателя это просто лишнее копирование, поэтому по умолчанию выключено.",

        ["power.head"] = "Выключать подсветку при:",
        ["power.exit"] = "выходе из программы",
        ["power.display"] = "выключении экрана",
        ["power.lock"] = "блокировке компьютера",
        ["power.suspend"] = "уходе в сон",

        ["stats.monitor"] = "монитор",
        ["stats.method"] = "метод",
        ["stats.capture"] = "захват",
        ["stats.output"] = "вывод",
        ["stats.port"] = "порт",
        ["stats.sources"] = "источники",
        ["stats.sent"] = "отправлено",
        ["stats.skipped"] = "пропущено одинаковых",
        ["stats.reconnects"] = "переподключений",
        ["stats.notrunning"] = "захват не запущен",

        ["warn.paused"] = "Пауза: {0} — лента погашена.",
        ["warn.port"] = "Порт не открылся. Проверь номер и что его не занял другой софт.",
        ["warn.count"] = "Сумма диодов {0} должна совпадать с NUM_LEDS в прошивке — иначе картинка поедет по кругу.",

        ["dialog.filter"] = "Настройки Rimlight (*.json)|*.json",
        ["dialog.saveFail"] = "Не удалось сохранить: ",
        ["dialog.loadFail"] = "Не удалось прочитать: ",
        ["dialog.loaded"] = "Настройки загружены и применены.",
        ["tray.show"] = "Показать",
        ["apply"] = "Применить",
        ["cancel"] = "Отмена",
        ["unsaved"] = "Есть несохранённые изменения",
        ["capture.autoSuffix"] = "авто",
        ["layout.overlay.show"] = "Показать схему на экране",
        ["layout.overlay.hide"] = "Скрыть схему",
        ["layout.overlay.note"] = "Схема ложится поверх всего на выбранном мониторе и живёт вместе с настройками. Клик по ячейке зажигает её и соответствующий светодиод зелёным — так проверяется, что номера совпадают с лентой. Esc закрывает."
    };

    static Dictionary<string, string> English() => new()
    {
        ["tab.main"] = "General",
        ["tab.device"] = "Device",
        ["tab.layout"] = "Layout",
        ["tab.color"] = "Colour",
        ["tab.capture"] = "Capture",
        ["tab.power"] = "Power",
        ["tab.about"] = "About",

        ["main.boost"] = "Brighten preview",
        ["main.boost.note"] = "Affects the on-screen preview only. A value of 30 is clearly visible on an LED in a dim room, while a monitor renders it almost black.",
        ["main.startmin"] = "Start minimised to tray",
        ["about.head"] = "Rimlight",
        ["about.version"] = "Version {0}",
        ["about.text"] = "Bias lighting driven by what is on screen: the edges of each frame are averaged per zone and sent to an addressable strip over a serial port using the Adalight protocol.",
        ["about.text2"] = "Capture runs as a ladder - Desktop Duplication, Windows Graphics Capture, GDI - switching by itself when a fast path stops delivering, so the light never simply stops. Frames can be shared with the case lighting module.",
        ["about.repo"] = "Project repository:",
        ["about.firmware"] = "Controller firmware and the original idea:",
        ["main.stats"] = "Show statistics",
        ["main.stats.note"] = "The statistics block under the preview: capture method, frame rates, port state.",
        ["nav.preview"] = "Show preview",
        ["main.tray"] = "Minimise to tray",
        ["main.autostart"] = "Start with Windows",
        ["main.log"] = "Write log",
        ["main.log.note"] = "The log is kept next to the settings file.",
        ["main.language"] = "Language",
        ["main.language.note"] = "Translations live in JSON next to the settings and can be edited or extended.",
        ["main.export"] = "Export settings",
        ["main.import"] = "Import",
        ["main.exit"] = "Exit",
        ["main.paths"] = "Settings: {0}",

        ["device.monitor"] = "Monitor",
        ["device.port"] = "COM port",
        ["device.baud"] = "Baud rate",
        ["device.baud.note"] = "The controller firmware must be configured for the same baud rate, or there will be no connection.",
        ["device.apply"] = "Apply and reconnect",

        ["layout.top"] = "Top",
        ["layout.bottom"] = "Bottom",
        ["layout.left"] = "Left",
        ["layout.right"] = "Right",
        ["layout.corner"] = "Start corner",
        ["layout.corner.br"] = "bottom right",
        ["layout.corner.bl"] = "bottom left",
        ["layout.corner.tl"] = "top left",
        ["layout.corner.tr"] = "top right",
        ["layout.ccw"] = "Counter-clockwise",
        ["layout.offset"] = "Offset, LEDs",
        ["layout.margin"] = "Corner margin, horizontal, % of width",
        ["layout.marginV"] = "Corner margin, vertical, % of width",
        ["layout.depth"] = "Zone depth, % of width",
        ["layout.note"] = "All percentages are of screen width so the values stay comparable in pixels. Horizontal and vertical margins are set independently.",
        ["layout.total"] = "Total LEDs: {0}",

        ["color.brightness"] = "Maximum brightness",
        ["color.minluma"] = "Darkness threshold",
        ["color.saturation"] = "Saturation",
        ["color.gamma"] = "Gamma",
        ["color.temperature"] = "Colour temperature, K",
        ["color.gainR"] = "Gain R",
        ["color.gainG"] = "Gain G",
        ["color.gainB"] = "Gain B",
        ["color.dither"] = "Dithering",
        ["color.dither.note"] = "Smooths banding in dark scenes by passing the rounding error to the next LED.",
        ["color.rise"] = "Smoothing: rise",
        ["color.rise.note"] = "Smoothing over time: the share of the way towards a new, brighter colour an LED travels per frame. Lower is smoother but the light lags more.",
        ["color.fall"] = "Smoothing: fall",
        ["color.fall.note"] = "The same for dimming. Fall is usually slower than rise so the light fades softly and does not flicker on dark scenes.",

        ["capture.method"] = "Capture method",
        ["capture.auto"] = "Auto",
        ["capture.dda"] = "DDA only",
        ["capture.wgc"] = "WGC only",
        ["capture.gdi"] = "GDI only (slow)",
        ["capture.method.note"] = "Auto uses the fast methods and falls back to the slow one whenever they stop delivering frames; applies immediately. DDA (Desktop Duplication) is the fastest screen capture. WGC (Windows Graphics Capture) is slightly slower but steadier in games and fullscreen apps. GDI is the slowest but works almost everywhere.",
        ["capture.fps"] = "Frame rate cap",
        ["capture.onchange"] = "Send only when colours change",
        ["capture.onchange.note"] = "The firmware blanks the strip after 10 s of silence, so an identical frame is still repeated every 2 s.",
        ["capture.publish"] = "Share screen frames with the lighting module",
        ["capture.publish.note"] = "The frame is placed in shared memory for CaseLight, the case lighting, to pick up, so it needs no capture of its own. With nothing listening this is a pointless copy, hence off by default.",

        ["power.head"] = "Turn the strip off on:",
        ["power.exit"] = "application exit",
        ["power.display"] = "display off",
        ["power.lock"] = "workstation lock",
        ["power.suspend"] = "sleep",

        ["stats.monitor"] = "monitor",
        ["stats.method"] = "method",
        ["stats.capture"] = "capture",
        ["stats.output"] = "output",
        ["stats.port"] = "port",
        ["stats.sources"] = "sources",
        ["stats.sent"] = "sent",
        ["stats.skipped"] = "identical skipped",
        ["stats.reconnects"] = "reconnects",
        ["stats.notrunning"] = "capture not running",

        ["warn.paused"] = "Paused: {0} — the strip is off.",
        ["warn.port"] = "The port did not open. Check the name and that nothing else is holding it.",
        ["warn.count"] = "The LED total {0} must match NUM_LEDS in the firmware, or the picture will be rotated.",

        ["dialog.filter"] = "Rimlight settings (*.json)|*.json",
        ["dialog.saveFail"] = "Could not save: ",
        ["dialog.loadFail"] = "Could not read: ",
        ["dialog.loaded"] = "Settings loaded and applied.",
        ["tray.show"] = "Show",
        ["apply"] = "Apply",
        ["cancel"] = "Cancel",
        ["unsaved"] = "Unsaved changes",
        ["capture.autoSuffix"] = "auto",
        ["layout.overlay.show"] = "Show map on screen",
        ["layout.overlay.hide"] = "Hide map",
        ["layout.overlay.note"] = "The map sits on top of everything on the selected monitor and follows the settings live. Clicking a cell lights it and the matching LED green, which is how you confirm the numbering matches the strip. Esc closes it."
    };
}
