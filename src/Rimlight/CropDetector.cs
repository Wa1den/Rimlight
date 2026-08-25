using System;
using Rimlight.Leds;

namespace Rimlight;

/// <summary>Everything <see cref="CropDetector"/> needs, and nothing else.</summary>
public readonly record struct CropSettings
{
    /// <summary>Look for letterboxing - the black bars above and below the picture.</summary>
    public bool Vertical { get; init; } = true;

    /// <summary>Look for pillarboxing - the black bars left and right of the picture.</summary>
    public bool Horizontal { get; init; } = true;

    /// <summary>Bars thinner than this are ignored: a slightly dark edge is not a bar.</summary>
    public double MinPercent { get; init; } = 2.0;

    /// <summary>Upper bound on how far the sampling may move in from each side.</summary>
    public double MaxPercent { get; init; } = 25.0;

    /// <summary>Per-channel value below which a pixel counts as black. 0..255.</summary>
    public int BlackLevel { get; init; } = 16;

    /// <summary>
    /// How deep a run of lit rows may be and still be taken for something drawn on top of
    /// the bar rather than for the edge of the picture. Percent of the side.
    /// </summary>
    public double OverlookPercent { get; init; } = 5.0;

    /// <summary>How long a new set of bars has to hold before it is acted on.</summary>
    public double HoldMs { get; init; } = 700;

    public CropSettings() { }
}

/// <summary>The part of the frame that carries picture, in normalised 0..1 coordinates.</summary>
public readonly record struct CropRect(double X0, double Y0, double X1, double Y1)
{
    public static readonly CropRect Full = new(0, 0, 1, 1);

    public double Width => X1 - X0;
    public double Height => Y1 - Y0;

    public bool IsFull => X0 <= 0 && Y0 <= 0 && X1 >= 1 && Y1 >= 1;
}

/// <summary>
/// Finds the black bars around a letterboxed or pillarboxed picture, so the sampling can
/// move in past them instead of averaging the strip down with screen that carries nothing.
///
/// Runs on the reduced frame, a couple of hundred pixels wide - the scan costs less than
/// the zone sampling that follows it, and it stops at the first lit row, so on ordinary
/// full-screen content it touches barely anything.
///
/// The whole difficulty is in not reacting to what merely looks like a bar: a dark scene,
/// a fade to black, subtitles and player controls drawn over the bar itself. That is what
/// the hold time, the symmetry rule and the overlook run are for.
///
/// Lives in the application rather than in Core: the case lighting module works off zones
/// placed by hand inside a computer case, where there is no such thing as a letterbox, and
/// a copy of this in its Core would only be one more file to keep in step.
/// </summary>
public sealed class CropDetector
{
    /// <summary>
    /// Share of a row that may be lit while the row still counts as bar. Video compression
    /// leaves speckle in what should be flat black, and a stray bright pixel is not a
    /// picture edge.
    /// </summary>
    const double LitTolerance = 0.02;

    /// <summary>
    /// Middle share of each row left out of the decision. Subtitles and the player buttons
    /// sit in the centre and are drawn over the bar, so judging a row by its ends alone is
    /// what lets the bar be seen through them.
    /// </summary>
    const double IgnoreCenter = 0.5;

    /// <summary>Below this the two rectangles are the same one; keeps noise off the strip.</summary>
    const double Epsilon = 0.004;

    /// <summary>
    /// Share of the hold time required to move back out. Releasing is deliberately quicker
    /// than biting in: a wrong crop hides picture until it is undone, while a wrong release
    /// costs a moment of black at the edge.
    /// </summary>
    const double ReleaseShare = 0.4;

    public CropRect Rect { get; private set; } = CropRect.Full;

    CropRect _candidate = CropRect.Full;
    bool _haveCandidate;
    double _heldMs;

    public void Reset()
    {
        Rect = CropRect.Full;
        _candidate = CropRect.Full;
        _haveCandidate = false;
        _heldMs = 0;
    }

    /// <summary>
    /// Measures the frame and moves <see cref="Rect"/> once a new reading has held long
    /// enough. Returns true only on the frames where it actually moved, so the caller
    /// remaps its zones then and not on every frame.
    /// </summary>
    public bool Update(ReadOnlySpan<byte> image, int width, int height, int stride,
                       CropSettings s, double dtMs)
    {
        if (width < 8 || height < 8) return false;

        // A fade to black leaves nothing to measure against, and the frame before it was
        // the truthful one - so hold whatever is already applied rather than reading the
        // whole screen as one enormous bar. Same for a scene dark enough to be black.
        if (!HasLight(image, width, height, stride, s.BlackLevel)) return false;

        double top = 0, bottom = 0, left = 0, right = 0;

        if (s.Vertical)
        {
            int overlook = Portion(s.OverlookPercent, height);
            int a = DarkRows(image, width, height, stride, s.BlackLevel, overlook, fromTop: true);
            int b = DarkRows(image, width, height, stride, s.BlackLevel, overlook, fromTop: false);

            // Both halves dark all the way through, with light somewhere in the frame: the
            // ends of every row are black, which is pillarboxing seen edge-on rather than a
            // bar above and below. Nothing to crop vertically.
            if (a + b < height - 1)
                // Bars come in pairs. Taking the smaller of the two makes the crop symmetric
                // and, where a scene merely happens to be dark at one end, keeps it honest.
                top = bottom = Fraction(Math.Min(a, b), height, s);
        }

        if (s.Horizontal)
        {
            // Inside the picture found above, not the whole height: with letterboxing in
            // place the top and bottom of every column are bar, and judging the columns by
            // those would find side bars that are not there.
            int y0 = (int)(top * height);
            int y1 = height - (int)(bottom * height);

            int overlook = Portion(s.OverlookPercent, width);
            int a = DarkCols(image, width, y0, y1, stride, s.BlackLevel, overlook, fromLeft: true);
            int b = DarkCols(image, width, y0, y1, stride, s.BlackLevel, overlook, fromLeft: false);

            if (a + b < width - 1)
                left = right = Fraction(Math.Min(a, b), width, s);
        }

        var target = new CropRect(left, top, 1 - right, 1 - bottom);

        if (Same(target, Rect))
        {
            _haveCandidate = false;
            _heldMs = 0;
            return false;
        }

        if (!_haveCandidate || !Same(target, _candidate))
        {
            _candidate = target;
            _haveCandidate = true;
            _heldMs = 0;
            return false;
        }

        _heldMs += dtMs;
        double need = Deeper(target, Rect) ? s.HoldMs : s.HoldMs * ReleaseShare;
        if (_heldMs < need) return false;

        Rect = target;
        _haveCandidate = false;
        _heldMs = 0;
        return true;
    }

    /// <summary>Bars below the floor read as none at all; above the ceiling they are capped.</summary>
    static double Fraction(int bar, int side, CropSettings s)
    {
        double f = bar / (double)side;
        if (f * 100.0 < s.MinPercent) return 0;
        return Math.Min(f, s.MaxPercent / 100.0);
    }

    static int Portion(double percent, int side) =>
        Math.Max(0, (int)Math.Round(percent / 100.0 * side));

    /// <summary>
    /// Whether the frame carries any picture at all, sampled coarsely and stopping at the
    /// first lit pixel. On anything but a black screen this reads a handful of pixels.
    /// </summary>
    static bool HasLight(ReadOnlySpan<byte> image, int width, int height, int stride, int level)
    {
        const int Step = 4;

        for (int y = 0; y < height; y += Step)
        {
            int row = y * stride;
            for (int x = 0; x < width; x += Step)
                if (IsLit(image, row + x * 4, level)) return true;
        }

        return false;
    }

    static bool Same(CropRect a, CropRect b) =>
        Math.Abs(a.X0 - b.X0) <= Epsilon && Math.Abs(a.Y0 - b.Y0) <= Epsilon &&
        Math.Abs(a.X1 - b.X1) <= Epsilon && Math.Abs(a.Y1 - b.Y1) <= Epsilon;

    /// <summary>True when the target moves in past the current rectangle on any side.</summary>
    static bool Deeper(CropRect target, CropRect now) =>
        target.X0 > now.X0 + Epsilon || target.Y0 > now.Y0 + Epsilon ||
        target.X1 < now.X1 - Epsilon || target.Y1 < now.Y1 - Epsilon;

    /// <summary>
    /// How many rows in from one edge are bar.
    ///
    /// The answer is the last dark row, not the first lit one. Subtitles and the progress
    /// bar of a player are painted over the black, and stopping at them would put the
    /// boundary halfway up it - so a short run of lit rows is stepped over and only a run
    /// deeper than the overlook ends the bar.
    /// </summary>
    static int DarkRows(ReadOnlySpan<byte> image, int width, int height, int stride,
                        int level, int overlook, bool fromTop)
    {
        int limit = height / 2;
        int lastDark = 0, litRun = 0;

        for (int i = 0; i < limit; i++)
        {
            int y = fromTop ? i : height - 1 - i;

            if (RowIsDark(image, y, width, stride, level))
            {
                lastDark = i + 1;
                litRun = 0;
            }
            else if (++litRun > overlook) break;
        }

        return lastDark;
    }

    static int DarkCols(ReadOnlySpan<byte> image, int width, int y0, int y1, int stride,
                        int level, int overlook, bool fromLeft)
    {
        int limit = width / 2;
        int lastDark = 0, litRun = 0;

        for (int i = 0; i < limit; i++)
        {
            int x = fromLeft ? i : width - 1 - i;

            if (ColIsDark(image, x, y0, y1, stride, level))
            {
                lastDark = i + 1;
                litRun = 0;
            }
            else if (++litRun > overlook) break;
        }

        return lastDark;
    }

    static bool RowIsDark(ReadOnlySpan<byte> image, int y, int width, int stride, int level)
    {
        int edge = (int)(width * (1 - IgnoreCenter) / 2);
        if (edge < 1) edge = width / 2;

        int row = y * stride;
        int lit = 0, allowed = (int)(edge * 2 * LitTolerance);

        for (int x = 0; x < edge; x++)
        {
            if (IsLit(image, row + x * 4, level) && ++lit > allowed) return false;
            if (IsLit(image, row + (width - 1 - x) * 4, level) && ++lit > allowed) return false;
        }

        return true;
    }

    static bool ColIsDark(ReadOnlySpan<byte> image, int x, int y0, int y1, int stride, int level)
    {
        int span = y1 - y0;
        if (span < 2) return true;

        int edge = (int)(span * (1 - IgnoreCenter) / 2);
        if (edge < 1) edge = span / 2;

        int col = x * 4;
        int lit = 0, allowed = (int)(edge * 2 * LitTolerance);

        for (int y = 0; y < edge; y++)
        {
            if (IsLit(image, (y0 + y) * stride + col, level) && ++lit > allowed) return false;
            if (IsLit(image, (y1 - 1 - y) * stride + col, level) && ++lit > allowed) return false;
        }

        return true;
    }

    /// <summary>
    /// Brightest channel rather than luminance: a bar is black in all three, and a deep
    /// blue edge of picture would pass a luminance test that weights blue at 0.07.
    /// </summary>
    static bool IsLit(ReadOnlySpan<byte> image, int i, int level) =>
        image[i] > level || image[i + 1] > level || image[i + 2] > level;
}

/// <summary>
/// Moves the sampling zones onto the picture once the bars are known. The strip geometry is
/// left alone - this produces a second set of rectangles to sample through, so the preview
/// and the overlay keep showing where the LEDs actually are.
/// </summary>
public static class CropMapper
{
    public static void Apply(LedZone[] src, LedZone[] dst, CropRect r, bool stretch)
    {
        for (int i = 0; i < src.Length && i < dst.Length; i++)
        {
            var z = src[i];

            if (stretch)
            {
                dst[i] = z with
                {
                    X0 = r.X0 + z.X0 * r.Width,
                    X1 = r.X0 + z.X1 * r.Width,
                    Y0 = r.Y0 + z.Y0 * r.Height,
                    Y1 = r.Y0 + z.Y1 * r.Height
                };
            }
            else
            {
                (double x0, double x1) = Fit(z.X0, z.X1, r.X0, r.X1);
                (double y0, double y1) = Fit(z.Y0, z.Y1, r.Y0, r.Y1);
                dst[i] = z with { X0 = x0, X1 = x1, Y0 = y0, Y1 = y1 };
            }
        }
    }

    /// <summary>
    /// Slides a span inside the bounds keeping its length, so a zone that falls in the bar
    /// ends up against the edge of the picture instead of collapsing to a line.
    /// </summary>
    static (double, double) Fit(double a, double b, double lo, double hi)
    {
        double len = Math.Min(b - a, hi - lo);
        if (a < lo) return (lo, lo + len);
        if (b > hi) return (hi - len, hi);
        return (a, b);
    }
}
