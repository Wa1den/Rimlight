namespace Ambilight.Leds;

/// <summary>
/// Everything <see cref="ColorPipeline"/> needs, and nothing else.
///
/// The pipeline used to read the Ambilight application's own config object, which made it
/// unusable from anywhere else. The two consumers want genuinely different values anyway -
/// LEDs on a motherboard and inside DRAM render colour nothing like the strip behind the
/// monitor - so each owns its own instance of this.
/// </summary>
public readonly record struct ColorSettings
{
    public double MaxBrightness { get; init; } = 1.0;   // 0..1 overall cap
    public double MinLuma { get; init; } = 0.0;         // below this the output goes dark
    public double Saturation { get; init; } = 1.0;      // 1 = untouched
    public double Gamma { get; init; } = 2.2;           // 2.2 = neutral round trip
    public int TemperatureK { get; init; } = 6500;      // 6500 = neutral
    public double GainR { get; init; } = 1.0;
    public double GainG { get; init; } = 1.0;
    public double GainB { get; init; } = 1.0;
    public bool Dithering { get; init; } = true;

    /// <summary>Asymmetric smoothing: light rises quickly, falls gently.</summary>
    public double SmoothingRise { get; init; } = 0.55;
    public double SmoothingFall { get; init; } = 0.18;

    public ColorSettings() { }
}
