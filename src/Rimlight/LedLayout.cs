using System;
using System.Collections.Generic;
using Rimlight.Leds;

namespace Rimlight;

/// <summary>
/// Builds the ring of zones that follows the screen edges - the shape of the strip mounted
/// behind the monitor. Free-form placements live in their own consumer; the zone type and
/// the sampler they share sit in Rimlight.Core.
/// </summary>
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
    public static LedZone[] Build(RimlightConfig cfg, int screenWidth, int screenHeight)
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
}
