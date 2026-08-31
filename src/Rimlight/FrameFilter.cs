using System;

namespace Rimlight;

/// <summary>
/// Takes the frame out of focus or sharpens it, on one axis with zero in the middle.
///
/// Both halves work on the reduced frame before the zones are read off it.
///
/// To the left the picture is defocused: every pixel is replaced by the average of a disc
/// around it, the way a lens out of focus spreads a point into a circle. That is a stronger
/// averaging without a wider sampling area - the zone keeps its place and its size, and the
/// colour it reads is drawn from the neighbourhood around it, so neighbouring stretches of
/// the strip run into each other.
///
/// To the right the picture is sharpened: each pixel is pushed away from the average of its
/// surroundings, so a light area beside a dark one gets lighter and the dark one darker.
/// The surroundings are measured over a disc wider than one sampling zone, which is what
/// makes the difference survive the averaging and reach the LEDs.
/// </summary>
public sealed class FrameFilter
{
    /// <summary>Ends of the scale, in percent, for each half.</summary>
    public const int MaxPercent = 50;

    /// <summary>
    /// The widest defocus, as a share of the frame width.
    ///
    /// The sampling area of one LED is around two percent of the width, so the end of the
    /// scale reaches a couple of times past it.
    /// </summary>
    const double WidestBlur = 0.04;

    /// <summary>
    /// What the sharpening compares a pixel against, as a share of the frame width.
    ///
    /// Wider than one sampling zone on purpose: a difference measured inside a zone is
    /// averaged away when the zone is read, and only a difference between neighbouring
    /// zones reaches the strip.
    /// </summary>
    const double SharpenScale = 0.03;

    /// <summary>How far a pixel is pushed from its surroundings at the end of the scale.</summary>
    const double StrongestSharpen = 1.5;

    byte[] _blurred = Array.Empty<byte>();

    /// <summary>Row sums per channel, for reading a span of any row in two lookups.</summary>
    int[] _rowSums = Array.Empty<int>();

    /// <summary>Half-width of the disc on each of its rows, and the pixels it covers.</summary>
    int[] _span = Array.Empty<int>();
    int _spanRadius = -1;
    int _spanArea;

    /// <summary>
    /// Works the frame over in place. Zero leaves it exactly as captured.
    /// </summary>
    /// <param name="sharpness">Percent, negative out of focus and positive sharpened.</param>
    public void Apply(int sharpness, byte[] image, int width, int height, int stride)
    {
        if (sharpness == 0 || width < 2 || height < 2 || stride <= 0) return;

        int bytes = height * stride;
        if (bytes <= 0 || bytes > image.Length) return;

        int percent = Math.Clamp(Math.Abs(sharpness), 1, MaxPercent);

        if (sharpness < 0)
        {
            // Reading through the row sums rather than the frame, so the result can go
            // straight back into the frame it was made from.
            Defocus(image, image, width, height, stride, Radius(percent, width, WidestBlur));
            return;
        }

        int radius = Radius(MaxPercent, width, SharpenScale);
        if (_blurred.Length < bytes) _blurred = new byte[bytes];

        Defocus(image, _blurred, width, height, stride, radius);
        PushApart(image, _blurred, width, height, stride, StrongestSharpen * percent / MaxPercent);
    }

    static int Radius(int percent, int width, double widest) =>
        Math.Max(1, (int)Math.Round(percent / (double)MaxPercent * widest * width));

    /// <summary>
    /// Replaces every pixel with the average of the disc around it.
    ///
    /// A disc and not a square: a square kernel smears a bright spot into a rectangle, and
    /// the shape shows up on a picture with small bright things in it. The cost of the
    /// round shape is kept down by summing each row of the disc out of the row sums, so a
    /// pixel costs two lookups per row of the disc rather than one per pixel of it.
    /// </summary>
    void Defocus(byte[] src, byte[] dst, int width, int height, int stride, int radius)
    {
        BuildRowSums(src, width, height, stride);
        BuildSpans(radius);

        int line = width + 1;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;

            for (int x = 0; x < width; x++)
            {
                int o = row + x * 4;
                int first = 0, second = 0, third = 0;

                // Дальше radius от боковых краёв ни одна строка диска за кадр не выходит,
                // и разбор со столбцами за краем не нужен.
                bool whole = x >= radius && x + radius < width;

                // Три канала считаются вместе: строка диска, её начало и конец у них общие,
                // и вычислять эти три числа трижды - тратить на них больше, чем на сумму.
                for (int i = 0; i < _span.Length; i++)
                {
                    int half = _span[i];
                    int at = Edge(y + i - radius, height) * 3 * line;

                    if (whole)
                    {
                        int from = x - half;
                        int to = x + half + 1;

                        first += _rowSums[at + to] - _rowSums[at + from];
                        at += line;
                        second += _rowSums[at + to] - _rowSums[at + from];
                        at += line;
                        third += _rowSums[at + to] - _rowSums[at + from];
                    }
                    else
                    {
                        int lo = x - half, hi = x + half;
                        int miss = lo < 0 ? -lo : 0;
                        int over = hi >= width ? hi - width + 1 : 0;
                        int from = lo < 0 ? 0 : lo;
                        int to = (hi >= width ? width - 1 : hi) + 1;

                        first += Edged(at, from, to, miss, over, width);
                        at += line;
                        second += Edged(at, from, to, miss, over, width);
                        at += line;
                        third += Edged(at, from, to, miss, over, width);
                    }
                }

                dst[o] = (byte)(first / _spanArea);
                dst[o + 1] = (byte)(second / _spanArea);
                dst[o + 2] = (byte)(third / _spanArea);
                dst[o + 3] = src[o + 3];    // прозрачность в кадре всюду одна
            }
        }
    }

    /// <summary>
    /// One row of the disc where it hangs over the side of the frame, with the columns
    /// outside counted as copies of the edge column.
    ///
    /// The sums alone give the row cut short, and a short row still divided by the area of
    /// the whole disc came back darkened: at a radius of five pixels the outermost column
    /// of a flat grey frame lost 43%, and those columns are what the side LEDs read. Rows
    /// above and below the frame already work this way, by clamping the row index.
    /// </summary>
    int Edged(int at, int from, int to, int miss, int over, int width) =>
        _rowSums[at + to] - _rowSums[at + from] +
        miss * _rowSums[at + 1] +                                        // _rowSums[at] = 0
        over * (_rowSums[at + width] - _rowSums[at + width - 1]);

    /// <summary>
    /// Pushes the frame away from its own blurred copy: the plain unsharp mask.
    /// </summary>
    static void PushApart(byte[] image, byte[] blurred, int width, int height, int stride, double amount)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;

            for (int x = 0; x < width; x++)
            {
                int o = row + x * 4;

                for (int c = 0; c < 3; c++)
                {
                    int value = image[o + c];
                    double pushed = value + amount * (value - blurred[o + c]);
                    image[o + c] = (byte)Math.Clamp(pushed, 0, 255);
                }
            }
        }
    }

    /// <summary>
    /// Running sums along every row, one set per channel, with a leading zero so a span
    /// from a to b comes out as sums[b + 1] - sums[a].
    /// </summary>
    void BuildRowSums(byte[] src, int width, int height, int stride)
    {
        int line = width + 1;
        int need = line * height * 3;
        if (_rowSums.Length < need) _rowSums = new int[need];

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;

            for (int c = 0; c < 3; c++)
            {
                int at = (y * 3 + c) * line;
                int sum = 0;

                _rowSums[at] = 0;
                for (int x = 0; x < width; x++)
                {
                    sum += src[row + x * 4 + c];
                    _rowSums[at + x + 1] = sum;
                }
            }
        }
    }

    /// <summary>The disc as a half-width per row, rebuilt only when the radius changes.</summary>
    void BuildSpans(int radius)
    {
        if (radius == _spanRadius) return;

        _span = new int[2 * radius + 1];
        _spanArea = 0;

        for (int i = 0; i < _span.Length; i++)
        {
            int dy = i - radius;
            int half = (int)Math.Sqrt((double)radius * radius - (double)dy * dy);

            _span[i] = half;
            _spanArea += 2 * half + 1;
        }

        _spanRadius = radius;
    }

    /// <summary>
    /// The nearest row inside the frame.
    ///
    /// Clamping rather than wrapping: the disc at the top of the picture is filled with more
    /// of the top, where wrapping would pull the bottom edge into it.
    /// </summary>
    static int Edge(int i, int n) => i < 0 ? 0 : i >= n ? n - 1 : i;
}
