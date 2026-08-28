namespace Rimlight.Leds;

/// <summary>
/// Everything <see cref="ColorPipeline"/> needs, and nothing else.
///
/// The pipeline used to read the Rimlight application's own config object, which made it
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

    /// <summary>
    /// Ceiling on the mean duty across the whole strip, 0..1. One means no ceiling.
    ///
    /// A fraction rather than a current, because what a duty costs in amperes depends on
    /// how many LEDs there are and which kind they are - neither of which this library has
    /// any business knowing. The caller converts.
    ///
    /// Not the same instrument as <see cref="MaxBrightness"/>, which takes the same share
    /// off every frame whatever it costs. This one only engages when the strip as a whole
    /// goes bright - the case a supply is actually sized for - and leaves ordinary
    /// pictures, where most of the strip is dim, at full brightness.
    /// </summary>
    public double PowerLimit { get; init; } = 1.0;

    /// <summary>
    /// Level no channel drops below, 0..1 of the output scale. Zero switches it off.
    ///
    /// A floor rather than a fade to a chosen colour: a floor cannot amplify anything, so
    /// a nearly black picture cannot be turned into a noisy one, which is what scaling a
    /// dark colour up to a minimum does.
    /// </summary>
    public double MinBacklight { get; init; }

    /// <summary>Asymmetric smoothing: light rises quickly, falls gently.</summary>
    public double SmoothingRise { get; init; } = 0.55;
    public double SmoothingFall { get; init; } = 0.18;

    public ColorSettings() { }
}
