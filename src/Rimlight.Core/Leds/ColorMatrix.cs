using System;

namespace Rimlight.Leds;

/// <summary>
/// Linear-light 3x3 map from screen RGB to LED drive, rows are output channels.
///
/// White balance alone fixes only the sum of the three channels. A mixed colour lands
/// wherever the LED's own primaries put it, and those are not the screen's: a WS2812 green
/// sits near 520 nm, well towards cyan of the sRGB green, so with white matched cyan still
/// comes out greener and more saturated than on screen and orange drifts with it. The LEDs
/// add light linearly, so a matrix is the whole correction - once the three primaries match,
/// every mixture between them does.
/// </summary>
public readonly record struct ColorMatrix(
    double Rr, double Rg, double Rb,
    double Gr, double Gg, double Gb,
    double Br, double Bg, double Bb)
{
    public static ColorMatrix Identity => new(1, 0, 0, 0, 1, 0, 0, 0, 1);

    public void Apply(ref double r, ref double g, ref double b)
    {
        double nr = Rr * r + Rg * g + Rb * b;
        double ng = Gr * r + Gg * g + Gb * b;
        double nb = Br * r + Bg * g + Bb * b;
        r = nr; g = ng; b = nb;
    }

    /// <summary>
    /// Builds the matrix from what can be judged by eye, one primary at a time.
    ///
    /// Hue moves a primary towards one neighbour by mixing that neighbour's channel in:
    /// positive takes red to yellow, green to cyan and blue to magenta, negative goes the
    /// other way round the circle. Saturation below one mixes in all three channels equally.
    /// Nothing is ever subtracted: a strip primary is already more saturated than the
    /// screen's, so it can only be brought back towards it.
    ///
    /// The rows are then scaled so that screen white still drives every channel to one,
    /// and the white point is applied last. That way the primaries are tuned without
    /// touching white, and white is tuned without touching the primaries. The price is that
    /// a primary pulled towards a neighbour dims that neighbour's own colour slightly, since
    /// the channel it borrows has to stay balanced on white.
    /// </summary>
    /// <param name="white">Drive of each channel on screen white, 0..1.</param>
    /// <param name="hue">Shift of red, green and blue, -1..1.</param>
    /// <param name="saturation">Saturation of red, green and blue, 0..1.</param>
    public static ColorMatrix FromPrimaries((double r, double g, double b) white,
        (double r, double g, double b) hue, (double r, double g, double b) saturation)
    {
        // m[row, column]: column is the screen primary, row the LED channel it drives
        var m = new double[3, 3];
        double[] h = { hue.r, hue.g, hue.b };
        double[] s = { saturation.r, saturation.g, saturation.b };

        for (int p = 0; p < 3; p++)
        {
            int next = (p + 1) % 3, prev = (p + 2) % 3;
            m[p, p] = 1;
            if (h[p] > 0) m[next, p] = h[p];
            else m[prev, p] = -h[p];

            // смешивание с серым того же уровня, что у ведущего канала
            double keep = Math.Clamp(s[p], 0, 1);
            for (int c = 0; c < 3; c++)
                m[c, p] = m[c, p] * keep + (1 - keep);
        }

        double[] w = { white.r, white.g, white.b };
        for (int c = 0; c < 3; c++)
        {
            double sum = m[c, 0] + m[c, 1] + m[c, 2];
            double k = sum > 0 ? Math.Clamp(w[c], 0, 1) / sum : 0;
            for (int p = 0; p < 3; p++) m[c, p] *= k;
        }

        return new(m[0, 0], m[0, 1], m[0, 2],
                   m[1, 0], m[1, 1], m[1, 2],
                   m[2, 0], m[2, 1], m[2, 2]);
    }
}
