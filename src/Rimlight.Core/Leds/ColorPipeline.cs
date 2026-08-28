using System;

namespace Rimlight.Leds;

/// <summary>
/// Turns per-zone screen colours into bytes for the LEDs.
///
/// Everything up to the final encode happens in linear light, which is where scaling,
/// white balance and blending are actually meaningful. Prismatik does this arithmetic on
/// gamma-encoded values, which is why its output dims oddly on high-contrast scenes.
///
/// The frame is walked twice: once to work out what each channel wants, and once to put
/// bytes on the wire. Splitting them is what lets the power ceiling see the whole strip
/// before anything is committed - a limit that has to be applied per LED is not a limit on
/// the strip at all.
/// </summary>
public sealed class ColorPipeline
{
    double[] _smoothR = Array.Empty<double>();
    double[] _smoothG = Array.Empty<double>();
    double[] _smoothB = Array.Empty<double>();

    /// <summary>Gamma-encoded 0..255 values for the frame being built, before quantising.</summary>
    double[] _encoded = Array.Empty<double>();

    bool _primed;

    public void Reset(int ledCount)
    {
        _smoothR = new double[ledCount];
        _smoothG = new double[ledCount];
        _smoothB = new double[ledCount];
        _encoded = new double[ledCount * 3];
        _primed = false;
    }

    static double SrgbToLinear(double v) =>
        v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    /// <summary>
    /// Kelvin to linear-sRGB gains via the Planckian locus, normalised so the brightest
    /// channel stays at 1 - warming the colour must not also dim the strip.
    /// </summary>
    public static (double r, double g, double b) TemperatureGains(int kelvin)
    {
        double t = Math.Clamp(kelvin, 1500, 15000);

        // Kim et al. cubic approximation of the locus in CIE xy
        double x = t <= 4000
            ? -0.2661239e9 / (t * t * t) - 0.2343589e6 / (t * t) + 0.8776956e3 / t + 0.179910
            : -3.0258469e9 / (t * t * t) + 2.1070379e6 / (t * t) + 0.2226347e3 / t + 0.240390;

        double y = t <= 2222
            ? -1.1063814 * x * x * x - 1.34811020 * x * x + 2.18555832 * x - 0.20219683
            : t <= 4000
                ? -0.9549476 * x * x * x - 1.37418593 * x * x + 2.09137015 * x - 0.16748867
                : 3.0817580 * x * x * x - 5.87338670 * x * x + 3.75112997 * x - 0.37001483;

        if (y <= 0) return (1, 1, 1);

        double bigY = 1.0;
        double bigX = x * bigY / y;
        double bigZ = (1 - x - y) * bigY / y;

        double r = 3.2406 * bigX - 1.5372 * bigY - 0.4986 * bigZ;
        double g = -0.9689 * bigX + 1.8758 * bigY + 0.0415 * bigZ;
        double b = 0.0557 * bigX - 0.2040 * bigY + 1.0570 * bigZ;

        r = Math.Max(0, r); g = Math.Max(0, g); b = Math.Max(0, b);
        double max = Math.Max(r, Math.Max(g, b));
        if (max <= 0) return (1, 1, 1);
        return (r / max, g / max, b / max);
    }

    /// <param name="inRgb">Zone averages, 3 bytes per LED, gamma-encoded.</param>
    /// <param name="outRgb">Bytes for the wire, 3 per LED.</param>
    /// <param name="dtMs">Real time since the previous frame; smoothing is tied to it so
    /// the response does not change when the capture rate drops.</param>
    public void Process(byte[] inRgb, byte[] outRgb, in ColorSettings cfg, int ledCount, double dtMs)
    {
        if (_smoothR.Length != ledCount) Reset(ledCount);

        var (tr, tg, tb) = TemperatureGains(cfg.TemperatureK);
        double gr = tr * cfg.GainR, gg = tg * cfg.GainG, gb = tb * cfg.GainB;
        double invGamma = 1.0 / Math.Max(0.1, cfg.Gamma);

        // A frame-rate independent EMA: at 60 fps the configured factor applies as-is,
        // at 20 fps it is scaled so the visible response stays the same.
        double step = Math.Clamp(dtMs / 16.67, 0.1, 6.0);

        for (int i = 0; i < ledCount; i++)
        {
            int o = i * 3;

            double r = SrgbToLinear(inRgb[o] / 255.0);
            double g = SrgbToLinear(inRgb[o + 1] / 255.0);
            double b = SrgbToLinear(inRgb[o + 2] / 255.0);

            // white balance and per-channel trim (this is what matches the wall colour)
            r *= gr; g *= gg; b *= gb;

            // saturation around the luminance of the pixel itself
            if (Math.Abs(cfg.Saturation - 1.0) > 0.001)
            {
                double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                r = y + (r - y) * cfg.Saturation;
                g = y + (g - y) * cfg.Saturation;
                b = y + (b - y) * cfg.Saturation;
            }

            r *= cfg.MaxBrightness; g *= cfg.MaxBrightness; b *= cfg.MaxBrightness;

            r = Math.Clamp(r, 0, 1); g = Math.Clamp(g, 0, 1); b = Math.Clamp(b, 0, 1);

            // dark cutoff, so a nearly black screen does not leave the strip faintly lit
            double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            if (luma < cfg.MinLuma) { r = g = b = 0; }

            if (!_primed)
            {
                _smoothR[i] = r; _smoothG[i] = g; _smoothB[i] = b;
            }
            else
            {
                _smoothR[i] = Blend(_smoothR[i], r, cfg, step);
                _smoothG[i] = Blend(_smoothG[i], g, cfg, step);
                _smoothB[i] = Blend(_smoothB[i], b, cfg, step);
            }

            _encoded[o] = GammaEncode(_smoothR[i], invGamma);
            _encoded[o + 1] = GammaEncode(_smoothG[i], invGamma);
            _encoded[o + 2] = GammaEncode(_smoothB[i], invGamma);
        }

        _primed = true;

        // Floor first, ceiling second. The supply is a hard limit and a minimum glow is a
        // preference, so where the two disagree the ceiling has the last word.
        ApplyMinBacklight(_encoded, ledCount * 3, cfg.MinBacklight);
        ApplyPowerLimit(_encoded, ledCount * 3, cfg.PowerLimit);

        // dithering error travels along the strip, not through time - see Quantise
        double errR = 0, errG = 0, errB = 0;

        for (int i = 0; i < ledCount; i++)
        {
            int o = i * 3;
            outRgb[o] = Quantise(_encoded[o], ref errR, cfg.Dithering);
            outRgb[o + 1] = Quantise(_encoded[o + 1], ref errG, cfg.Dithering);
            outRgb[o + 2] = Quantise(_encoded[o + 2], ref errB, cfg.Dithering);
        }
    }

    /// <summary>
    /// Lifts a nearly black LED up to the floor, so a black picture leaves the strip
    /// glowing instead of dark.
    ///
    /// The lift is one amount added to all three channels of the LED, worked out from its
    /// brightest channel. Flooring each channel on its own instead would reach into every
    /// dark colour on screen and raise its weak channels towards its strong one, which
    /// takes the colour out of exactly the scenes that have least of it to spare. Adding
    /// the same amount to all three leaves an LED alone the moment any channel of it
    /// clears the floor, and keeps the distances between the channels while it does not.
    ///
    /// Additive rather than a multiplier for the same reason the floor is a floor: scaling
    /// a nearly black colour up to a set level multiplies whatever noise is in it by the
    /// same factor.
    /// </summary>
    static void ApplyMinBacklight(double[] encoded, int count, double level)
    {
        if (level <= 0) return;

        double floor = Math.Clamp(level, 0, 1) * 255.0;

        for (int j = 0; j + 2 < count; j += 3)
        {
            double top = Math.Max(encoded[j], Math.Max(encoded[j + 1], encoded[j + 2]));
            if (top >= floor) continue;

            double lift = floor - top;
            encoded[j] += lift;
            encoded[j + 1] += lift;
            encoded[j + 2] += lift;
        }
    }

    /// <summary>
    /// Scales the whole frame down until its mean duty fits under the ceiling.
    ///
    /// Duty is what the strip draws current for, and duty is the encoded value - so this
    /// belongs after the gamma encode and not before it. One factor for every channel of
    /// every LED, so the picture keeps its colours and only loses brightness.
    /// </summary>
    static void ApplyPowerLimit(double[] encoded, int count, double limit)
    {
        if (limit >= 1.0 || count <= 0) return;

        double total = 0;
        for (int j = 0; j < count; j++) total += encoded[j];

        double allowed = count * 255.0 * Math.Max(0, limit);
        if (total <= allowed || total <= 0) return;

        double scale = allowed / total;
        for (int j = 0; j < count; j++) encoded[j] *= scale;
    }

    /// <summary>Rises fast, falls slowly - matches how the eye reads ambient light.</summary>
    static double Blend(double current, double target, in ColorSettings cfg, double step)
    {
        double a = target > current ? cfg.SmoothingRise : cfg.SmoothingFall;
        a = 1 - Math.Pow(1 - Math.Clamp(a, 0.01, 1.0), step);
        return current + (target - current) * a;
    }

    /// <summary>Linear light to the 0..255 scale the strip is driven on.</summary>
    static double GammaEncode(double linear, double invGamma) =>
        Math.Pow(Math.Clamp(linear, 0, 1), invGamma) * 255.0;

    /// <summary>
    /// Rounds to 8 bit, passing the rounding error to the NEXT LED along the strip.
    ///
    /// Carrying it through time instead makes each LED alternate between adjacent levels,
    /// and near the bottom of the range 1 versus 2 is a doubling of brightness - clearly
    /// visible as flicker. Spreading the error spatially smooths the same banding with no
    /// temporal component at all.
    /// </summary>
    static byte Quantise(double value, ref double error, bool dither)
    {
        double v = Math.Clamp(value, 0, 255);

        if (!dither)
        {
            error = 0;
            return (byte)Math.Clamp(Math.Round(v), 0, 255);
        }

        double wanted = v + error;
        double q = Math.Clamp(Math.Floor(wanted), 0, 255);
        error = Math.Clamp(wanted - q, -1.0, 1.0);
        return (byte)q;
    }
}
