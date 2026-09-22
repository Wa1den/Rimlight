using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Rimlight;

namespace AwaProbe;

/// <summary>
/// Brings up a HyperSerial controller without the main program: asks the firmware to
/// introduce itself, walks the strip through solid colours and a running dot, and reads
/// back the controller's own count of frames received, shown and broken.
///
/// Kept apart from Rimlight on purpose. When the strip stays dark this separates "the
/// hardware or the firmware is wrong" from "the program sends the wrong thing" - with the
/// program in the loop the two look identical.
/// </summary>
static class Program
{
    const int Fps = 60;

    /// <summary>
    /// Steps wait for a key rather than a timer. What is being checked is visual - which
    /// LED lights first, whether red and green are swapped, where the dot stops - and two
    /// seconds of each went by faster than anyone could look at a strip behind a monitor.
    /// Off when input is redirected, so the tool still runs unattended from a script.
    /// </summary>
    static bool _interactive;

    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        _interactive = !Console.IsInputRedirected;

        int code = Execute(args);

        // Started by a double click the window belongs to this process alone and vanishes
        // the moment it exits, taking the result with it.
        if (_interactive && OwnsConsole())
        {
            Console.WriteLine();
            Console.Write("Нажмите любую клавишу, чтобы закрыть окно.");
            Console.ReadKey(true);
        }
        return code;
    }

    static int Execute(string[] args)
    {
        string? portName = null;
        int leds = 122;
        int level = 128;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--list":
                    return List();
                case "--selftest":
                    return SelfTest();
                case "--auto":
                    _interactive = false;
                    break;
                case "--leds" when i + 1 < args.Length && int.TryParse(args[i + 1], out int n) && n is >= 1 and <= 4096:
                    leds = n; i++;
                    break;
                case "--brightness" when i + 1 < args.Length && int.TryParse(args[i + 1], out int b) && b is >= 0 and <= 255:
                    level = b; i++;
                    break;
                case "-h" or "--help" or "/?":
                    Usage();
                    return 0;
                default:
                    if (args[i].StartsWith("COM", StringComparison.OrdinalIgnoreCase)) portName = args[i].ToUpperInvariant();
                    else { Usage(); return 1; }
                    break;
            }
        }

        portName ??= FindController();
        if (portName == null) return 1;

        try
        {
            return Run(portName, leds, level);
        }
        catch (UnauthorizedAccessException)
        {
            Line($"Порт {portName} занят другой программой — скорее всего, Rimlight. Её нужно закрыть.", ConsoleColor.Red);
            return 1;
        }
        catch (IOException ex)
        {
            Line($"Порт {portName} не открылся: {ex.Message}", ConsoleColor.Red);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Line("Прервано.", ConsoleColor.Yellow);
            return 5;
        }
    }

    static void Usage()
    {
        Console.WriteLine("""
            AwaProbe — проверка контроллера с прошивкой HyperSerialPico

              AwaProbe [COMn] [--leds N] [--brightness 0..255] [--auto]
              AwaProbe --list        показать порты и какой из них RP2040
              AwaProbe --selftest    проверить кодирование кадра без железа

            Без номера порта берётся единственный подключённый RP2040.
            По умолчанию 122 диода, яркость 128 из 255.
            Шаги переключаются клавишей, Esc прерывает; --auto — по таймеру.
            """);
    }

    // ---- порты ----------------------------------------------------------------

    static int List()
    {
        var ports = SerialDevices.Present();
        if (ports.Count == 0) { Console.WriteLine("Последовательных портов нет."); return 1; }

        foreach (var p in ports)
        {
            string id = p.Vid.Length > 0 ? $"VID {p.Vid}  PID {p.Pid}" : "без USB-идентификатора";
            bool awa = p.Protocol == DeviceProtocol.Awa;
            Line($"  {p.Name,-6} {id}{(awa ? "   ← " + Board(p) : "")}", awa ? ConsoleColor.Green : null);
        }
        return 0;
    }

    static string Board(SerialDevices.Port p) =>
        p.Vid == SerialDevices.RaspberryPiVid ? "RP2040" : "ESP32-S2";

    static string? FindController()
    {
        var picos = SerialDevices.Present().Where(p => p.Protocol == DeviceProtocol.Awa).ToList();
        if (picos.Count == 1) return picos[0].Name;

        if (picos.Count == 0)
            Line("RP2040 среди портов не найден. Прошита ли плата и воткнута ли она? Порт можно указать руками: AwaProbe COM5", ConsoleColor.Red);
        else
            Line($"Найдено несколько RP2040: {string.Join(", ", picos.Select(p => p.Name))}. Нужно указать порт.", ConsoleColor.Red);
        return null;
    }

    // ---- прогон на железе -------------------------------------------------------

    static int Run(string portName, int leds, int level)
    {
        Line($"Порт {portName}, {leds} диодов, яркость {level} из 255", ConsoleColor.Cyan);
        if (leds * 3 * level / 255 * 20 > 450)
            Console.WriteLine("  Белый на этой яркости берёт больше, чем даёт USB: лента должна быть запитана от блока питания.");
        Console.WriteLine();

        using var port = new SerialPort(portName, 2_000_000)
        {
            // TinyUSB on the controller reads only while DTR is up (tud_cdc_connected), so
            // with it down every byte is dropped without a word. The Nano is the opposite:
            // DTR resets it into the bootloader, which is why Rimlight keeps it low.
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 100,
            WriteTimeout = 1000
        };
        port.Open();
        Thread.Sleep(50);
        port.DiscardInBuffer();

        // 1. приветствие: доказывает, что прошивка жива и связь есть, до всякой ленты
        Console.Write("1. Приветствие прошивки... ");
        port.Write(AwaFrame.HelloQuery, 0, AwaFrame.HelloQuery.Length);
        string hello = ReadFor(port, 1500, "Awa driver");
        if (!hello.Contains("Awa driver"))
        {
            Line("нет ответа", ConsoleColor.Red);
            if (hello.Length > 0) Console.WriteLine("   пришло: " + Clean(hello));
            Console.WriteLine("   Это не про ленту и не про пайку: плата либо не прошита HyperSerialPico, либо это не тот порт.");
            return 2;
        }
        Line("есть", ConsoleColor.Green);

        // Ответ на этот запрос — строка счётчиков, а за ней приветствие; счётчики только
        // что сброшены и ничего не говорят, поэтому показывается одно приветствие.
        var greeting = hello.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Awa driver"));
        Console.WriteLine("   " + (greeting ?? Clean(hello)));
        Console.WriteLine();

        var rgb = new byte[leds * 3];

        if (_interactive)
        {
            Line("Каждый шаг держится на ленте, пока не нажата клавиша. Esc — выход.", ConsoleColor.DarkGray);
            Console.WriteLine();
        }

        // 2. сплошные цвета
        Console.WriteLine("2. Сплошные цвета.");
        foreach (var (name, r, g, b) in new[] { ("красный", level, 0, 0), ("зелёный", 0, level, 0), ("синий", 0, 0, level), ("белый", level, level, level) })
            Step(port, "   " + name, 2.0, _ => Fill(rgb, (byte)r, (byte)g, (byte)b));
        Console.WriteLine("   Перепутанные красный и зелёный значат ленту с другим порядком цветов, а не ошибку пайки.");
        Console.WriteLine();

        // 3. первый диод и бегущая точка
        Step(port, "3. Первый диод горит красным — он должен быть у места пайки Din.", 1.5, _ =>
        {
            Array.Clear(rgb);
            rgb[0] = (byte)level;
            return rgb;
        });

        // по кругу и вдвое медленнее, пока смотрят; без человека — один проход
        Step(port, "   Зелёная точка бежит от первого диода до последнего.", leds / (double)Fps, f =>
        {
            Array.Clear(rgb);
            int dot = _interactive ? f / 2 % leds : Math.Min(f, leds - 1);
            rgb[dot * 3 + 1] = (byte)level;
            return rgb;
        });
        Console.WriteLine("   Если точка останавливается, не дойдя до конца, — на этом диоде обрыв данных.");
        Console.WriteLine();

        // 4. счётчики контроллера: окно в секунду закрывается, пока кадры идут, поэтому
        //    сначала поток, потом вопрос
        Console.Write("4. Счётчики контроллера... ");
        Stream(port, (int)(1.5 * Fps), _ => Fill(rgb, 0, 0, 0));
        port.DiscardInBuffer();
        port.Write(AwaFrame.StatsQuery, 0, AwaFrame.StatsQuery.Length);
        string stats = ReadFor(port, 1500, "HyperHDR frames");

        int result = 0;
        var line = stats.Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.StartsWith("HyperHDR frames"));
        if (line == null)
        {
            Line("нет ответа", ConsoleColor.Yellow);
            result = 3;
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("   " + line);
            Explain(line, ref result);
        }

        Stream(port, 3, _ => Fill(rgb, 0, 0, 0));
        Console.WriteLine();
        Line(result == 0 ? "Готово: контроллер принимает кадры AWA." : "Готово, но есть замечания выше.",
             result == 0 ? ConsoleColor.Green : ConsoleColor.Yellow);
        return result;
    }

    /// <summary>
    /// Counts from the last full second the controller measured. "Received" is every header
    /// it found, "good" the ones whose checksums held; the difference is frames that
    /// arrived damaged and were dropped rather than shown.
    /// </summary>
    static void Explain(string line, ref int result)
    {
        int shown = Number(line, "frames:"), received = Number(line, "receiv.:"), good = Number(line, "good:"), broken = Number(line, "incompl.:");
        if (shown < 0 || received < 0) return;

        Console.WriteLine($"   показано {shown} кадров в секунду, принято {received}, целых {good}, битых {broken}");
        if (broken > 0)
        {
            Line("   Есть битые кадры — связь по USB теряет байты. Кабель или порт хаба.", ConsoleColor.Yellow);
            result = 3;
        }
        if (shown < Fps * 0.8)
        {
            Line($"   Показано меньше отправленных {Fps} — лента не успевает защёлкивать кадры.", ConsoleColor.Yellow);
            result = 3;
        }
    }

    static int Number(string line, string key)
    {
        int i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return -1;
        var digits = new string(line.Skip(i + key.Length).SkipWhile(c => c == ' ').TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) ? n : -1;
    }

    static byte[] Fill(byte[] rgb, byte r, byte g, byte b)
    {
        for (int i = 0; i < rgb.Length; i += 3) { rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = b; }
        return rgb;
    }

    /// <summary>
    /// A steady stream rather than a single frame per step: the controller's statistics are
    /// windows of one second, closed only while data keeps arriving.
    /// </summary>
    static void Stream(SerialPort port, int frames, Func<int, byte[]> frameAt)
    {
        var sw = Stopwatch.StartNew();
        for (int f = 0; f < frames; f++)
        {
            var data = AwaFrame.Build(frameAt(f));
            port.Write(data, 0, data.Length);

            int wait = (int)((f + 1) * 1000L / Fps - sw.ElapsedMilliseconds);
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    /// <summary>
    /// Shows one picture until the user moves on, or for a fixed time when nobody is at
    /// the keyboard. The stream keeps running while it waits, so the controller's
    /// statistics window stays open and the strip never sees a gap.
    /// </summary>
    static void Step(SerialPort port, string text, double autoSeconds, Func<int, byte[]> frameAt)
    {
        Console.Write(text);

        if (!_interactive)
        {
            Console.WriteLine();
            Stream(port, Math.Max(1, (int)(autoSeconds * Fps)), frameAt);
            return;
        }

        Line("   — клавиша, дальше", ConsoleColor.DarkGray);

        var sw = Stopwatch.StartNew();
        for (int f = 0; ; f++)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Escape)
                {
                    // свой буфер: функция кадра перезаливает общий, и лента осталась бы гореть
                    var black = new byte[frameAt(0).Length];
                    Stream(port, 3, _ => black);
                    throw new OperationCanceledException();
                }
                return;
            }

            var data = AwaFrame.Build(frameAt(f));
            port.Write(data, 0, data.Length);

            int wait = (int)((f + 1) * 1000L / Fps - sw.ElapsedMilliseconds);
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    [DllImport("kernel32.dll")]
    static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    /// <summary>Whether this process is the only one on its console - that is, it was not started from a shell.</summary>
    static bool OwnsConsole()
    {
        try { return GetConsoleProcessList(new uint[2], 2) == 1; }
        catch { return false; }
    }

    static string ReadFor(SerialPort port, int ms, string until)
    {
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            try { sb.Append(port.ReadExisting()); } catch (TimeoutException) { }
            if (sb.ToString().Contains(until) && sb.ToString().EndsWith('\n')) break;
            Thread.Sleep(20);
        }
        return sb.ToString();
    }

    static string Clean(string s) =>
        string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

    static void Line(string text, ConsoleColor? color = null)
    {
        if (color is { } c) Console.ForegroundColor = c;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    // ---- самопроверка без железа ------------------------------------------------

    /// <summary>
    /// Runs the encoder's output through a transcription of the firmware's own parser.
    /// Settles the wire format before any hardware is involved, so that a dark strip later
    /// cannot be the encoder's fault.
    /// </summary>
    static int SelfTest()
    {
        int failed = 0;
        void Check(string what, bool ok)
        {
            Line($"  {(ok ? "да " : "НЕТ")}  {what}", ok ? ConsoleColor.Green : ConsoleColor.Red);
            if (!ok) failed++;
        }

        var rnd = new Random(1);
        var rgb = new byte[122 * 3];
        rnd.NextBytes(rgb);

        var p = new FirmwareParser();
        p.Feed(AwaFrame.Build(rgb));
        Check("кадр на 122 диода принят", p.Accepted == 1);
        Check("цвета дошли без искажений", p.LastFrame.AsSpan().SequenceEqual(rgb));

        // ищем полезную нагрузку, у которой третья сумма выходит ровно 0x41, чтобы проверить
        // подмену на 0xaa — на случайных кадрах она встречается примерно раз из 255
        byte[]? tricky = null;
        for (int seed = 0; seed < 20000 && tricky == null; seed++)
        {
            var candidate = new byte[12 * 3];
            new Random(seed).NextBytes(candidate);
            var frame = AwaFrame.Build(candidate);
            if (frame[^1] == 0xaa && ExtRaw(candidate) == 0x41) tricky = candidate;
        }
        if (tricky != null)
        {
            p = new FirmwareParser();
            p.Feed(AwaFrame.Build(tricky));
            Check("кадр с контрольной суммой 0x41 принят после подмены на 0xaa", p.Accepted == 1);
        }
        else Check("нашёлся кадр с контрольной суммой 0x41", false);

        var broken = AwaFrame.Build(rgb);
        broken[100] ^= 0x10;
        p = new FirmwareParser();
        p.Feed(broken);
        Check("кадр с испорченным байтом отброшен", p.Accepted == 0);

        p = new FirmwareParser();
        p.Feed(AwaFrame.HelloQuery);
        p.Feed(AwaFrame.StatsQuery);
        Check("оба запроса распознаны как запросы, а не как кадры", p.Queries == 2 && p.Accepted == 0);

        p = new FirmwareParser();
        var noisy = new byte[37];
        rnd.NextBytes(noisy);
        p.Feed(noisy);
        p.Feed(AwaFrame.Build(rgb));
        Check("после мусора на линии парсер находит следующий кадр", p.Accepted == 1);

        p = new FirmwareParser();
        p.Feed(StockAdalight(rgb));
        Check("стоковый кадр Adalight прошивкой не принимается", p.Accepted == 0);

        Console.WriteLine();
        Line(failed == 0 ? "Кодирование совпадает с парсером прошивки." : $"Не сошлось проверок: {failed}.",
             failed == 0 ? ConsoleColor.Green : ConsoleColor.Red);
        return failed == 0 ? 0 : 4;
    }

    static int ExtRaw(byte[] payload)
    {
        int ext = 0;
        byte position = 0;
        foreach (byte b in payload) ext = (ext + (b ^ position++)) % 255;
        return ext;
    }

    /// <summary>What Rimlight sends today: 'A' 'd' 'a' and no trailer.</summary>
    static byte[] StockAdalight(byte[] rgb)
    {
        int n = rgb.Length / 3 - 1;
        var frame = new byte[6 + rgb.Length];
        frame[0] = (byte)'A'; frame[1] = (byte)'d'; frame[2] = (byte)'a';
        frame[3] = (byte)(n >> 8); frame[4] = (byte)(n & 0xFF);
        frame[5] = (byte)(frame[3] ^ frame[4] ^ 0x55);
        rgb.CopyTo(frame, 6);
        return frame;
    }
}
