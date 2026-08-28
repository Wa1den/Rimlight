using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;

namespace LatencyProbe;

/// <summary>
/// The clock the whole measurement hangs off, and the schedule of colours it drives.
///
/// One Stopwatch (QPC underneath, so sub-microsecond) is the single source of time: the
/// digits on screen, the colour of the cells and the line written to the log all come from
/// the same reading, taken once per rendered frame. If they came from separate readings the
/// video would show a timestamp that belongs to a slightly different moment than the colour
/// next to it, and that difference is the same size as the thing being measured.
/// </summary>
public sealed class BenchRun
{
    /// <summary>The 60 fps frame the counter reports. Not the display's rate on purpose -
    /// it is a fixed ruler, so the number means the same thing on any monitor.</summary>
    public const double FrameMs = 1000.0 / 60.0;

    /// <summary>
    /// Primaries and the two extremes, in this order deliberately: red -> green -> blue ->
    /// white are all rises, which the strip follows quickly, and white -> black is the one
    /// fall in the cycle. Rimlight smooths falls far more heavily than rises (0.18 against
    /// 0.55 by default), so the two are worth reading as separate numbers.
    /// </summary>
    public static readonly (string Name, Color Color)[] Palette =
    {
        ("красный", Color.FromRgb(255, 0, 0)),
        ("зелёный", Color.FromRgb(0, 255, 0)),
        ("синий",   Color.FromRgb(0, 0, 255)),
        ("белый",   Color.FromRgb(255, 255, 255)),
        ("чёрный",  Color.FromRgb(0, 0, 0)),
    };

    readonly Stopwatch _watch = new();

    public BenchRun(double intervalMs) => IntervalMs = Math.Max(200, intervalMs);

    /// <summary>How long each colour is held.</summary>
    public double IntervalMs { get; }

    public bool Running => _watch.IsRunning;

    public void Start() => _watch.Restart();

    public void Stop() => _watch.Stop();

    /// <summary>
    /// Milliseconds since the start. Read once per frame and pass the value around; calling
    /// this twice in one frame gives two different answers.
    /// </summary>
    public double Read() => _watch.Elapsed.TotalMilliseconds;

    public int StepAt(double ms) => (int)(ms / IntervalMs);

    public static (string Name, Color Color) ColorOf(int step)
    {
        int n = Palette.Length;
        return Palette[((step % n) + n) % n];
    }

    public static long Frame60(double ms) => (long)(ms / FrameMs);

    /// <summary>Seconds with milliseconds, fixed width so the digits do not dance.</summary>
    public static string Clock(double ms) =>
        (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture);
}
