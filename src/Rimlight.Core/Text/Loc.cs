using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Rimlight.Capture;

namespace Rimlight.Text;

/// <summary>
/// Strings by key. Two languages are built in and written out as JSON next to the config
/// on first run; from then on the folder is what gets read. Any other file in that folder
/// joins the language list, so a translation needs no rebuild.
/// </summary>
public static class Loc
{
    /// <summary>
    /// Bumped whenever the built-in strings change. Files on disk deliberately win over
    /// the built-ins so translations can be corrected - but that also meant an old file
    /// silently shadowed newly reworded labels, so a mismatched version rewrites it. Only
    /// the two built-in files are rewritten; added languages are left alone.
    /// </summary>
    const string Version = "22";

    /// <summary>
    /// Bookkeeping entries rather than translated text: the version a file was written
    /// from, and the name to show in the language list.
    /// </summary>
    const string VersionKey = "_version";
    const string NameKey = "_name";

    /// <summary>The languages the program carries inside itself.</summary>
    static readonly string[] BuiltinCodes = { "ru", "en" };

    public static string Language { get; private set; } = "ru";

    static Dictionary<string, string> _current = new();

    /// <summary>Built-ins plus whatever usable files the folder holds; rebuilt on load.</summary>
    static string[] _available = BuiltinCodes;
    static readonly Dictionary<string, string> _names = new();

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

    public static string[] Available => _available;

    public static string DisplayName(string code) =>
        _names.TryGetValue(code, out var name) ? name : BuiltinName(code);

    static string BuiltinName(string code) => code switch
    {
        "ru" => "Русский",
        "en" => "English",
        _ => code
    };

    public static void Load(string language)
    {
        WriteDefaults();
        Scan();

        Language = Array.IndexOf(_available, language) >= 0 ? language : "ru";

        // английский подложкой: строка, пропущенная в переводе, показывается
        // по-английски, а не ключом
        var strings = English();
        if (Array.IndexOf(BuiltinCodes, Language) >= 0) Overlay(strings, Builtin(Language));
        Overlay(strings, ReadLocale(Language));

        _current = strings;
    }

    static void Overlay(Dictionary<string, string> onto, Dictionary<string, string>? from)
    {
        if (from == null) return;
        foreach (var kv in from) onto[kv.Key] = kv.Value;
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
    ///
    /// Anything other than Russian gets the English half: a language added as a file has
    /// no translation for these, so they follow the same English fallback as the keys.
    /// </summary>
    public static string P(string ru, string en) => Language == "ru" ? ru : en;

    /// <summary>
    /// Builds the language list out of the folder: the file name is the code, "_name" is
    /// what the list shows. Everything that reads as a translation is offered, even a
    /// half-finished one - the keys it lacks come from English.
    /// </summary>
    static void Scan()
    {
        _names.Clear();
        foreach (var code in BuiltinCodes) _names[code] = BuiltinName(code);

        var extra = new List<string>();
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                string code = Path.GetFileNameWithoutExtension(path);
                if (code.Length == 0) continue;

                var loaded = ReadLocale(code);
                if (loaded == null) continue;

                if (Array.IndexOf(BuiltinCodes, code) < 0) extra.Add(code);

                if (loaded.TryGetValue(NameKey, out var name) && name.Trim().Length > 0) _names[code] = name.Trim();
                else if (!_names.ContainsKey(code)) _names[code] = code;
            }
        }
        catch (Exception ex)
        {
            ProbeLog.Log("lang", P("не удалось прочитать папку переводов: ",
                                  "could not read the translation folder: ") + ex.Message);
        }

        extra.Sort(StringComparer.OrdinalIgnoreCase);
        _available = BuiltinCodes.Concat(extra).ToArray();
    }

    /// <summary>
    /// How many known keys a file must carry to be taken for a translation. Verifying the
    /// whole set would reject a partial translation that works perfectly well, while this
    /// much keeps an unrelated JSON file, an exported config for one, out of the list.
    /// </summary>
    const int MinKnownKeys = 8;

    /// <summary>Reads one file of the folder; null if it is missing or is not a translation.</summary>
    static Dictionary<string, string>? ReadLocale(string code)
    {
        string path = Path.Combine(Directory, code + ".json");
        if (!File.Exists(path)) return null;

        Dictionary<string, string>? loaded;
        try
        {
            // нестроковое значение бросает исключение здесь - это и есть проверка формата
            loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            ProbeLog.Log("lang", P("не удалось прочитать перевод ",
                                  "could not read translation ") + Path.GetFileName(path) + ": " + ex.Message);
            return null;
        }

        if (loaded == null || !IsLocale(loaded))
        {
            ProbeLog.Log("lang", P("не похоже на перевод, файл пропущен: ",
                                  "not a translation, file skipped: ") + Path.GetFileName(path));
            return null;
        }

        return loaded;
    }

    static bool IsLocale(Dictionary<string, string> loaded)
    {
        var known = English();
        int hits = 0;
        foreach (var key in loaded.Keys)
            if (known.ContainsKey(key) && ++hits >= MinKnownKeys) return true;

        return false;
    }

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
            foreach (var code in BuiltinCodes)
            {
                string path = Path.Combine(Directory, code + ".json");
                if (File.Exists(path) && CurrentVersionOf(path) == Version) continue;

                File.WriteAllText(path, JsonSerializer.Serialize(Builtin(code), opts));
            }
        }
        catch (Exception ex)
        {
            ProbeLog.Log("lang", P("не удалось записать переводы: ",
                                  "could not write the translations: ") + ex.Message);
        }
    }

    static string CurrentVersionOf(string path)
    {
        try
        {
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return d != null && d.TryGetValue(VersionKey, out var v) ? v : "";
        }
        catch
        {
            return "";
        }
    }

    static Dictionary<string, string> Builtin(string code)
    {
        var d = code == "en" ? English() : Russian();
        d[NameKey] = BuiltinName(code);
        d[VersionKey] = Version;
        return d;
    }

    static Dictionary<string, string> Russian() => new()
    {
        ["off"] = "выключено",

        ["tab.main"] = "Основное",
        ["tab.device"] = "Устройство",
        ["tab.layout"] = "Раскладка",
        ["tab.crop"] = "Кадрирование",
        ["tab.color"] = "Цвет",
        ["tab.capture"] = "Захват",
        ["tab.power"] = "Питание",
        ["tab.about"] = "О программе",

        ["main.boost"] = "Усилить превью",
        ["main.boost.note"] = "Повышает яркость только в превью. Малые значения на светодиодах заметно ярче, чем на мониторе.",
        ["main.startmin"] = "Запускать свёрнутым в трей",
        ["about.head"] = "Rimlight",
        ["about.version"] = "Версия {0}",
        ["about.text"] = "Программа фоновой подсветки монитора: цвета по краям экрана усредняются по зонам и отправляются на адресную светодиодную ленту через COM-порт по протоколу Adalight.",
        ["about.text2"] = "Захват экрана выполняется методами Desktop Duplication, Windows Graphics Capture и GDI с автоматическим переключением, если текущий метод перестаёт выдавать кадры. Кадры могут передаваться модулю подсветки корпуса.",
        ["about.repo"] = "Репозиторий проекта:",
        ["about.firmware"] = "Прошивка контроллера и исходная задумка:",
        ["main.stats"] = "Отображать статистику",
        ["main.diag.head"] = "Статистика и лог",
        ["main.stats.detailed"] = "Подробная статистика",
        ["main.stats.detailed.note"] = "Данные о задержках с разделением по этапам. Пишутся и в лог, если он включён.",
        ["main.stats.note"] = "Блок статистики под превью: метод захвата, частота кадров, состояние порта.",
        ["nav.preview"] = "Показывать превью",
        ["main.tray"] = "Сворачивать в трей",
        ["main.autostart"] = "Запускать вместе с Windows",
        ["main.log"] = "Писать лог",
        ["main.log.note"] = "Лог сохраняется в папке настроек.",
        ["main.language"] = "Язык",
        ["main.language.note"] = "Переводы лежат в JSON-файлах в папке lang рядом с настройками. Добавленный туда файл появляется в списке при следующем запуске, непереведённые строки берутся из английского. Как сделать перевод, описано в README.",
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
        ["layout.note"] = "Отступ зон от края экрана: сопоставляет превью с реальным положением диодов. Проценты считаются от ширины экрана.",
        ["layout.total"] = "Всего диодов: {0}",

        ["crop.head"] = "Фильм шире экрана идёт с чёрными полосами сверху и снизу, и диоды вдоль них не светят. Программа находит границы картинки и переносит зоны выборки внутрь неё.",
        ["crop.enable"] = "Адаптивное кадрирование",
        ["crop.enable.note"] = "Пока полосы есть, зоны выборки стоят внутри картинки; когда пропадают — возвращаются к краям экрана.",
        ["crop.vertical"] = "Полосы сверху и снизу",
        ["crop.vertical.note"] = "Фильм 2.35:1 или 21:9 на экране 16:9.",
        ["crop.horizontal"] = "Полосы слева и справа",
        ["crop.horizontal.note"] = "Видео 4:3 на широком экране и вертикальные ролики.",
        ["crop.stretch"] = "Растягивать картинку на всю ленту",
        ["crop.stretch.note"] = "Картинка раскладывается по всему кольцу, тёмных участков на ленте не остаётся. Если выключить, зоны только сходят с полос, оставаясь напротив своих мест на экране.",
        ["crop.min"] = "Минимальная полоса, % стороны",
        ["crop.min.note"] = "Полосы тоньше этой доли принимаются за тёмный край кадра.",
        ["crop.max"] = "Максимальная полоса, % стороны",
        ["crop.max.note"] = "Ограничивает глубину переноса, чтобы тёмная сцена не срезала картинку. У фильма 2.39:1 на экране 16:9 полоса — около 17% высоты.",
        ["crop.level"] = "Порог черноты",
        ["crop.level.note"] = "Значение канала, ниже которого пиксель считается чёрным. При нуле мешает шум сжатия, при высоком за полосу принимается тёмный край картинки.",
        ["crop.overlook"] = "Пропускать помехи, % стороны",
        ["crop.overlook.note"] = "Запас на субтитры и панель плеера, которые рисуются поверх полосы. Больше — держится увереннее, слишком много — захватывает край картинки.",
        ["crop.hold"] = "Задержка подтверждения, с",
        ["crop.hold.note"] = "Новые границы применяются, только если продержались столько времени. Больше — устойчивее к тёмным сценам, меньше — быстрее отклик.",
        ["crop.status.off"] = "Поиск полос выключен.",
        ["crop.status.none"] = "Полосы не найдены.",
        ["crop.status.v"] = "Найдено: сверху и снизу {0:0.0}%.",
        ["crop.status.h"] = "Найдено: слева и справа {0:0.0}%.",
        ["crop.status.both"] = "Найдено: сверху и снизу {0:0.0}%, слева и справа {1:0.0}%.",

        ["color.brightness"] = "Максимальная яркость",
        ["color.minluma"] = "Порог темноты",
        ["color.shadow"] = "Обесцвечивание тёмного",
        ["color.shadow.note"] = "Баланс белого применяется одинаково ко всем уровням, поэтому на почти чёрном участке экрана от кадра остаётся только оттенок: чёрная полоса плеера с белыми цифрами при тёплом балансе светит тёмно-красным. Чем ниже яркость, тем сильнее цвет сводится к серому. Ноль отключает.",
        ["color.saturation"] = "Насыщенность",
        ["color.gamma"] = "Гамма",
        ["color.temperature"] = "Цветовая температура, K",
        ["color.gainR"] = "Усиление красного",
        ["color.gainG"] = "Усиление зелёного",
        ["color.gainB"] = "Усиление синего",
        ["color.dither"] = "Дизеринг",
        ["color.dither.note"] = "Сглаживает ступени на тёмных сценах, перенося ошибку округления на соседний диод.",
        ["color.rise"] = "Сглаживание: подъём",
        ["color.rise.note"] = "Доля пути до нового, более яркого цвета, проходимая за кадр. Меньшие значения дают более плавные переходы, но увеличивают запаздывание.",
        ["color.fall"] = "Сглаживание: спад",
        ["color.fall.note"] = "То же при уменьшении яркости. Спад обычно делается медленнее подъёма, чтобы яркость снижалась плавно и не было мерцания на тёмных сценах.",

        ["capture.method"] = "Метод захвата",
        ["capture.auto"] = "Авто",
        ["capture.dda"] = "Только DDA",
        ["capture.wgc"] = "Только WGC",
        ["capture.gdi"] = "Только GDI (медленный)",
        ["capture.method.note"] = "Авто использует быстрые методы и переключается на медленный, когда они перестают выдавать кадры; применяется сразу. DDA (Desktop Duplication) — самый быстрый. WGC (Windows Graphics Capture) — немного медленнее, стабильнее в играх и полноэкранных приложениях. GDI — самый медленный, но работает почти везде.",
        ["capture.fps"] = "Максимум кадров в секунду",
        ["capture.fps.free"] = "без ограничения",
        ["capture.fps.note"] = "Ограничивает частоту, с которой кадры экрана сводятся к цветам зон, и вместе с ней нагрузку на видеокарту. Без ограничения кадры идут с той частотой, которую успевает принять лента. Ограничение кадров может повысить задержку вывода подсветки на ленту.",
        ["capture.onchange"] = "Отправлять только при смене цветов",
        ["capture.onchange.note"] = "Прошивка выключает ленту, если данные не приходят 10 секунд, поэтому одинаковый кадр всё равно отправляется раз в 2 секунды.",
        ["capture.publish"] = "Передавать кадры модулю подсветки корпуса",
        ["capture.publish.note"] = "Кадры помещаются в разделяемую память для модуля подсветки корпуса (CaseLight), которому тогда не нужен собственный захват экрана. Если модуль не запущен, копирование бесполезно, поэтому по умолчанию выключено.",

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
        ["stats.latency"] = "задержка",
        ["stats.ms"] = "мс",
        ["stats.worst"] = "худшая за 10 с",
        ["stats.stages"] = "по этапам",
        ["stats.stage.grab"] = "экран→захват",
        ["stats.stage.reduce"] = "свод",
        ["stats.stage.relay"] = "реле",
        ["stats.stage.out"] = "провод",
        ["stats.dropped"] = "отброшено:",
        ["stats.drop.queue"] = "очередь",
        ["stats.drop.rate"] = "темп",
        ["stats.sources"] = "источники",
        ["stats.sent"] = "отправлено",
        ["stats.skipped"] = "пропущено одинаковых",
        ["stats.reconnects"] = "переподключений",
        ["stats.notrunning"] = "захват не запущен",

        ["warn.paused"] = "Пауза: {0}. Лента выключена.",
        ["warn.port"] = "Не удалось открыть порт. Проверьте имя порта и что он не занят другой программой.",
        ["warn.count"] = "Сумма диодов {0} должна совпадать со значением NUM_LEDS в прошивке, иначе изображение смещается вдоль ленты.",

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
        ["layout.overlay.note"] = "Схема отображается поверх всех окон на выбранном мониторе и обновляется при изменении настроек. Щелчок по ячейке подсвечивает её и соответствующий светодиод зелёным: так проверяется соответствие номеров. Esc закрывает схему."
    };

    static Dictionary<string, string> English() => new()
    {
        ["off"] = "off",

        ["tab.main"] = "General",
        ["tab.device"] = "Device",
        ["tab.layout"] = "Layout",
        ["tab.crop"] = "Cropping",
        ["tab.color"] = "Colour",
        ["tab.capture"] = "Capture",
        ["tab.power"] = "Power",
        ["tab.about"] = "About",

        ["main.boost"] = "Brighten preview",
        ["main.boost.note"] = "Raises brightness in the preview only. Low values are noticeably brighter on LEDs than on a monitor.",
        ["main.startmin"] = "Start minimised to tray",
        ["about.head"] = "Rimlight",
        ["about.version"] = "Version {0}",
        ["about.text"] = "Monitor bias lighting software: colours along the screen edges are averaged per zone and sent to an addressable LED strip over a serial port using the Adalight protocol.",
        ["about.text2"] = "Screen capture uses Desktop Duplication, Windows Graphics Capture and GDI, switching automatically when the current method stops delivering frames. Frames can be shared with the case lighting module.",
        ["about.repo"] = "Project repository:",
        ["about.firmware"] = "Controller firmware and the original idea:",
        ["main.stats"] = "Show statistics",
        ["main.diag.head"] = "Statistics and log",
        ["main.stats.detailed"] = "Detailed statistics",
        ["main.stats.detailed.note"] = "Latency figures broken down by stage. Written to the log too, when it is on.",
        ["main.stats.note"] = "The statistics block under the preview: capture method, frame rates, port state.",
        ["nav.preview"] = "Show preview",
        ["main.tray"] = "Minimise to tray",
        ["main.autostart"] = "Start with Windows",
        ["main.log"] = "Write log",
        ["main.log.note"] = "The log is saved in the settings folder.",
        ["main.language"] = "Language",
        ["main.language.note"] = "Translations are JSON files in the lang folder next to the settings. A file added there appears in the list on the next start, with untranslated lines taken from English. The README describes how to make one.",
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
        ["layout.note"] = "Margin between the zones and the screen edge, used to align the preview with the physical LED positions. Percentages are of screen width.",
        ["layout.total"] = "Total LEDs: {0}",

        ["crop.head"] = "Material wider than the screen comes with black bars above and below it, and the LEDs along them do not light. The program finds the edges of the picture and moves the sampling zones inside them.",
        ["crop.enable"] = "Adaptive cropping",
        ["crop.enable.note"] = "While bars are present the sampling zones sit inside the picture; when the bars go, the zones return to the edges of the screen.",
        ["crop.vertical"] = "Bars above and below",
        ["crop.vertical.note"] = "2.35:1 or 21:9 material on a 16:9 screen.",
        ["crop.horizontal"] = "Bars left and right",
        ["crop.horizontal.note"] = "4:3 material on a wide screen, and vertical video.",
        ["crop.stretch"] = "Spread the picture over the whole strip",
        ["crop.stretch.note"] = "The picture is laid out across the entire ring, leaving no part of the strip dark. With this off the zones only step clear of the bars, staying opposite their own places on the screen.",
        ["crop.min"] = "Smallest bar, % of the side",
        ["crop.min.note"] = "Bars thinner than this are taken for a dark edge of the frame.",
        ["crop.max"] = "Largest bar, % of the side",
        ["crop.max.note"] = "Caps how far the sampling may move in, so a dark scene cannot cut the picture off. A 2.39:1 film on a 16:9 screen puts about 17% of the height in each bar.",
        ["crop.level"] = "Black threshold",
        ["crop.level.note"] = "The channel value below which a pixel counts as black. At zero the speckle of compression gets in the way; set too high, a dark edge of the picture is taken for bar.",
        ["crop.overlook"] = "Step over interference, % of the side",
        ["crop.overlook.note"] = "Margin for subtitles and player controls, which are drawn over the bar. Higher holds more reliably, too high takes in the edge of the picture.",
        ["crop.hold"] = "Confirmation delay, s",
        ["crop.hold.note"] = "New edges are acted on only once they have held for this long. Higher stands up to dark scenes better, lower responds sooner.",
        ["crop.status.off"] = "Bar detection off.",
        ["crop.status.none"] = "No bars found.",
        ["crop.status.v"] = "Found: {0:0.0}% top and bottom.",
        ["crop.status.h"] = "Found: {0:0.0}% left and right.",
        ["crop.status.both"] = "Found: {0:0.0}% top and bottom, {1:0.0}% left and right.",

        ["color.brightness"] = "Maximum brightness",
        ["color.minluma"] = "Darkness threshold",
        ["color.shadow"] = "Shadow desaturation",
        ["color.shadow.note"] = "White balance applies the same tint at every level, so in a nearly black part of the frame that tint is all that reaches the strip: a black player bar with white digits lights it dark red under a warm balance. The lower the brightness, the further the colour is pulled towards grey. Zero switches it off.",
        ["color.saturation"] = "Saturation",
        ["color.gamma"] = "Gamma",
        ["color.temperature"] = "Colour temperature, K",
        ["color.gainR"] = "Red gain",
        ["color.gainG"] = "Green gain",
        ["color.gainB"] = "Blue gain",
        ["color.dither"] = "Dithering",
        ["color.dither.note"] = "Smooths banding in dark scenes by passing the rounding error to the next LED.",
        ["color.rise"] = "Smoothing: rise",
        ["color.rise.note"] = "The share of the way towards a new, brighter colour covered per frame. Lower values give smoother transitions but increase the lag.",
        ["color.fall"] = "Smoothing: fall",
        ["color.fall.note"] = "The same for decreasing brightness. Fall is usually slower than rise so brightness drops gradually without flicker in dark scenes.",

        ["capture.method"] = "Capture method",
        ["capture.auto"] = "Auto",
        ["capture.dda"] = "DDA only",
        ["capture.wgc"] = "WGC only",
        ["capture.gdi"] = "GDI only (slow)",
        ["capture.method.note"] = "Auto uses the fast methods and switches to the slow one when they stop delivering frames; applies immediately. DDA (Desktop Duplication) is the fastest. WGC (Windows Graphics Capture) is slightly slower and steadier in games and fullscreen applications. GDI is the slowest but works almost everywhere.",
        ["capture.fps"] = "Maximum frames per second",
        ["capture.fps.free"] = "no limit",
        ["capture.fps.note"] = "Limits how often screen frames are reduced to zone colours, and with that the load on the graphics card. With no limit frames go through as fast as the strip can accept them. Limiting the frame rate can increase the delay before a change reaches the strip.",
        ["capture.onchange"] = "Send only when colours change",
        ["capture.onchange.note"] = "The firmware turns the strip off after 10 seconds without data, so an identical frame is still sent every 2 seconds.",
        ["capture.publish"] = "Share frames with the case lighting module",
        ["capture.publish.note"] = "Frames are placed in shared memory for the case lighting module (CaseLight), which then needs no screen capture of its own. With the module not running the copy is useless, so this is off by default.",

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
        ["stats.latency"] = "latency",
        ["stats.ms"] = "ms",
        ["stats.worst"] = "worst in 10 s",
        ["stats.stages"] = "stages",
        ["stats.stage.grab"] = "screen→capture",
        ["stats.stage.reduce"] = "reduce",
        ["stats.stage.relay"] = "relay",
        ["stats.stage.out"] = "wire",
        ["stats.dropped"] = "dropped:",
        ["stats.drop.queue"] = "queue",
        ["stats.drop.rate"] = "rate",
        ["stats.sources"] = "sources",
        ["stats.sent"] = "sent",
        ["stats.skipped"] = "identical skipped",
        ["stats.reconnects"] = "reconnects",
        ["stats.notrunning"] = "capture not running",

        ["warn.paused"] = "Paused: {0}. The strip is off.",
        ["warn.port"] = "Could not open the port. Check the port name and that no other program is using it.",
        ["warn.count"] = "The LED total {0} must match NUM_LEDS in the firmware, otherwise the image is shifted along the strip.",

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
        ["layout.overlay.note"] = "The map is shown on top of all windows on the selected monitor and follows setting changes. Clicking a cell highlights it and the matching LED in green to verify the numbering. Esc closes the map."
    };
}
