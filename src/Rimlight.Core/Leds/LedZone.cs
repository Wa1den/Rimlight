using System;

namespace Rimlight.Leds;

public enum Side { Bottom, Left, Top, Right }

/// <summary>One LED's sampling rectangle, in normalised 0..1 screen coordinates.</summary>
public readonly record struct LedZone(double X0, double Y0, double X1, double Y1, Side Side)
{
    public double CenterX => (X0 + X1) / 2;
    public double CenterY => (Y0 + Y1) / 2;
}

/// <summary>
/// Averages screen regions into per-LED colours.
///
/// Deliberately knows nothing about how the zones were arranged: the strip behind the
/// monitor builds a ring, the case module places its zones by hand, and both end up here.
/// </summary>
public static class ZoneSampler
{
    /// <summary>
    /// Averages each zone out of the reduced BGRA frame. Values stay in the frame's own
    /// (gamma-encoded) space here; the colour pipeline converts to linear light.
    /// </summary>
    public static void Sample(ReadOnlySpan<byte> image, int width, int height, int stride,
                              LedZone[] zones, byte[] outRgb)
    {
        for (int i = 0; i < zones.Length; i++)
        {
            var z = zones[i];

            int x0 = Math.Clamp((int)(z.X0 * width), 0, width - 1);
            int x1 = Math.Clamp((int)Math.Ceiling(z.X1 * width), x0 + 1, width);
            int y0 = Math.Clamp((int)(z.Y0 * height), 0, height - 1);
            int y1 = Math.Clamp((int)Math.Ceiling(z.Y1 * height), y0 + 1, height);

            ulong sr = 0, sg = 0, sb = 0;
            int n = 0;

            for (int y = y0; y < y1; y++)
            {
                int row = y * stride;
                for (int x = x0; x < x1; x++)
                {
                    int p = row + x * 4;
                    sb += image[p];
                    sg += image[p + 1];
                    sr += image[p + 2];
                    n++;
                }
            }

            int o = i * 3;
            if (n == 0) { outRgb[o] = outRgb[o + 1] = outRgb[o + 2] = 0; continue; }
            outRgb[o] = (byte)(sr / (ulong)n);
            outRgb[o + 1] = (byte)(sg / (ulong)n);
            outRgb[o + 2] = (byte)(sb / (ulong)n);
        }
    }
}
