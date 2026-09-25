using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Rimlight.Capture;

/// <summary>
/// Timestamped log in the same spirit as Prismatik's own, so a session can be
/// picked apart afterwards the same way its logs were.
/// </summary>
public static class ProbeLog
{
    /// <summary>
    /// Past this size the file is renamed to *.old.log and a fresh one is started, so the
    /// log never takes more than twice this on disk. The per-second telemetry alone would
    /// otherwise grow it by tens of megabytes a day on a machine that is never switched off.
    /// </summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    static readonly object Lock = new();
    static string Path;
    static bool _enabled = true;
    static bool _headerWritten;
    static long _size = -1;   // unknown until the first write looks at the file
    // per-source, otherwise three backends would flip a single shared key back and forth
    static readonly System.Collections.Generic.Dictionary<string, string> LastStatus = new();

    static ProbeLog()
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "probe.log");
    }

    public static string FilePath => Path;

    /// <summary>Where the previous file goes when the current one outgrows <see cref="MaxBytes"/>.</summary>
    public static string OldFilePath => System.IO.Path.ChangeExtension(Path, ".old.log");

    /// <summary>Points the log somewhere else, or turns it off entirely.</summary>
    public static void Configure(string path, bool enabled)
    {
        lock (Lock)
        {
            Path = path;
            _enabled = enabled;
            _headerWritten = false;
            _size = -1;
        }
        if (enabled) Log("log", "лог включён: " + path);
    }

    public static void Log(string source, string message)
    {
        if (!_enabled) return;

        var line = string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss:fff} [{1}] {2}{3}",
            DateTime.Now, source, message, Environment.NewLine);
        lock (Lock)
        {
            try
            {
                if (_size < 0) _size = File.Exists(Path) ? new FileInfo(Path).Length : 0;

                string? header = null;
                if (_size >= MaxBytes)
                {
                    File.Move(Path, OldFilePath, overwrite: true);
                    _size = 0;
                    if (_headerWritten)
                        header = $"===== продолжение сессии, начало в {System.IO.Path.GetFileName(OldFilePath)} ====={Environment.NewLine}";
                }
                // header written on first use, so merely referencing the logger never creates a file
                if (!_headerWritten)
                {
                    _headerWritten = true;
                    header = $"{Environment.NewLine}===== сессия {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}";
                }

                var text = header + line;
                File.AppendAllText(Path, text, Encoding.UTF8);
                _size += Encoding.UTF8.GetByteCount(text);
            }
            catch
            {
                _size = -1;   // re-read the real size next time instead of trusting a guess
                /* logging must never kill the app */
            }
        }
    }

    /// <summary>Logs only when the status actually changes, to keep the file readable.</summary>
    public static void LogStatusChange(string source, BackendStatus status, string text)
    {
        var key = status + "|" + text;
        lock (Lock)
        {
            if (LastStatus.TryGetValue(source, out var prev) && prev == key) return;
            LastStatus[source] = key;
        }
        Log(source, $"статус -> {status}: {text}");
    }
}
