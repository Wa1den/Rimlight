namespace Rimlight;

/// <summary>
/// Order of the three channels in each pixel on the wire.
///
/// The firmware takes the bytes of a pixel as R, G, B and hands them to the strip in the
/// order it was built for. A strip wired or built differently shows swapped colours, and
/// reflashing is the only fix on the firmware side; reordering here spares that. Names
/// follow Prismatik's ColorSequence: GRB means the green byte goes first.
/// </summary>
public enum ColorOrder { Rgb, Rbg, Grb, Gbr, Brg, Bgr }

public static class ColorOrders
{
    public static readonly ColorOrder[] All =
        { ColorOrder.Rgb, ColorOrder.Rbg, ColorOrder.Grb, ColorOrder.Gbr, ColorOrder.Brg, ColorOrder.Bgr };

    /// <summary>For each position on the wire, the channel of the source pixel that goes there.</summary>
    public static (int First, int Second, int Third) Sources(ColorOrder order) => order switch
    {
        ColorOrder.Rbg => (0, 2, 1),
        ColorOrder.Grb => (1, 0, 2),
        ColorOrder.Gbr => (1, 2, 0),
        ColorOrder.Brg => (2, 0, 1),
        ColorOrder.Bgr => (2, 1, 0),
        _ => (0, 1, 2)
    };
}
