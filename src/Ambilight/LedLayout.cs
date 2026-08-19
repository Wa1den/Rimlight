using System;
using System.Collections.Generic;

namespace Ambilight;

public enum Side { Bottom, Left, Top, Right }

/// <summary>One LED's sampling rectangle, in normalised 0..1 screen coordinates.</summary>
public readonly record struct LedZone(double X0, double Y0, double X1, double Y1, Side Side)
{
    public double CenterX => (X0 + X1) / 2;
    public double CenterY => (Y0 + Y1) / 2;
}

public static class LedLayout
{
    /// <summary>
    /// Builds the sampling rectangles in physical strip order.
    ///
    /// Margin and depth are percentages of screen WIDTH converted to pixels and applied
    /// equally on all four sides: the strip is a fixed physical distance from the screen
    /// edge everywhere, so equal pixel bands match it and equal percentages of each
    /// dimension would not.
    /// </summary>
    public static LedZone[] Build(AmbilightConfig cfg, int screenWidth, int screenHeight)
    {
        double marginHPx = cfg.EdgeMarginPercent / 100.0 * screenWidth;
        double marginVPx = cfg.EdgeMarginPercentV / 100.0 * screenWidth;
        double depthPx = cfg.DepthPercent / 100.0 * screenWidth;

        // keep the bands sane on extreme settings
        double cap = Math.Min(screenWidth, screenHeight) / 3.0;
        depthPx = Math.Min(depthPx, cap);
        marginHPx = Math.Min(marginHPx, cap);
        marginVPx = Math.Min(marginVPx, cap);

        // horizontal strips are inset from the sides, vertical ones from top and bottom
        double mx = marginHPx / screenWidth;
        double my = marginVPx / screenHeight;
        double dx = depthPx / screenWidth;
        double dy = depthPx / screenHeight;

        var ring = new List<LedZone>(cfg.TotalLeds);

        // Canonical traversal: counter-clockwise as the viewer sees it, starting at the
        // bottom-right corner - so up the RIGHT edge first. Reading it off a clock face:
        // bottom-right is about 4 o'clock, and counter-clockwise runs 4 -> 3 -> 2 -> 12,
        // i.e. upwards on the right. Going left along the bottom instead would be
        // clockwise, which is what this used to do and why the strip came out reversed.
        AddVertical(ring, cfg.RightCount, 1 - my, my, 1 - dx, 1.0, Side.Right);
        AddHorizontal(ring, cfg.TopCount, 1 - mx, mx, 0.0, dy, Side.Top);
        AddVertical(ring, cfg.LeftCount, my, 1 - my, 0.0, dx, Side.Left);
        AddHorizontal(ring, cfg.BottomCount, mx, 1 - mx, 1 - dy, 1.0, Side.Bottom);

        if (ring.Count == 0) return Array.Empty<LedZone>();

        var list = new List<LedZone>(ring);
        if (!cfg.CounterClockwise) list.Reverse();

        // Rotate so the strip begins at the chosen corner: find the zone whose centre is
        // nearest that corner. Robust for either direction and any side counts.
        (double cx, double cy) = cfg.StartCorner switch
        {
            Corner.BottomRight => (1.0, 1.0),
            Corner.BottomLeft => (0.0, 1.0),
            Corner.TopLeft => (0.0, 0.0),
            _ => (1.0, 0.0)
        };

        int best = 0;
        double bestDist = double.MaxValue;
        for (int i = 0; i < list.Count; i++)
        {
            double ddx = list[i].CenterX - cx, ddy = list[i].CenterY - cy;
            double d = ddx * ddx + ddy * ddy;
            if (d < bestDist) { bestDist = d; best = i; }
        }

        int n = list.Count;
        int shift = ((best + cfg.IndexOffset) % n + n) % n;

        var ordered = new LedZone[n];
        for (int i = 0; i < n; i++)
            ordered[i] = list[(shift + i) % n];

        return ordered;
    }

    static void AddHorizontal(List<LedZone> into, int count, double fromX, double toX,
                              double y0, double y1, Side side)
    {
        if (count <= 0) return;
        for (int i = 0; i < count; i++)
        {
            double a = fromX + (toX - fromX) * i / count;
            double b = fromX + (toX - fromX) * (i + 1) / count;
            into.Add(new LedZone(Math.Min(a, b), y0, Math.Max(a, b), y1, side));
        }
    }

    static void AddVertical(List<LedZone> into, int count, double fromY, double toY,
                            double x0, double x1, Side side)
    {
        if (count <= 0) return;
        for (int i = 0; i < count; i++)
        {
            double a = fromY + (toY - fromY) * i / count;
            double b = fromY + (toY - fromY) * (i + 1) / count;
            into.Add(new LedZone(x0, Math.Min(a, b), x1, Math.Max(a, b), side));
        }
    }

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
