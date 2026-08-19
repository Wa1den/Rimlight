using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Ambilight.Capture;

/// <summary>
/// Timestamped log in the same spirit as Prismatik's own, so a session can be
/// picked apart afterwards the same way its logs were.
/// </summary>
public static class ProbeLog
{
    static readonly object Lock = new();
    static string Path;
    static bool _enabled = true;
    static bool _headerWritten;
    // per-source, otherwise three backends would flip a single shared key back and forth
    static readonly System.Collections.Generic.Dictionary<string, string> LastStatus = new();

    static ProbeLog()
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "probe.log");
    }

    public static string FilePath => Path;

    /// <summary>Points the log somewhere else, or turns it off entirely.</summary>
    public static void Configure(string path, bool enabled)
    {
        lock (Lock)
        {
            Path = path;
            _enabled = enabled;
            _headerWritten = false;
        }
        if (enabled) Log("log", "лог включён: " + path);
    }

    public static void Log(string source, string message)
    {
        if (!_enabled) return;

        // header written on first use, so merely referencing the logger never creates a file
        lock (Lock)
        {
            if (!_headerWritten)
            {
                _headerWritten = true;
                try
                {
                    File.AppendAllText(Path,
                        $"{Environment.NewLine}===== сессия {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}",
                        Encoding.UTF8);
                }
                catch { /* logging must never kill the app */ }
            }
        }

        var line = string.Format(CultureInfo.InvariantCulture, "{0:HH:mm:ss:fff} [{1}] {2}{3}",
            DateTime.Now, source, message, Environment.NewLine);
        lock (Lock)
        {
            try { File.AppendAllText(Path, line, Encoding.UTF8); } catch { /* logging must never kill the probe */ }
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
