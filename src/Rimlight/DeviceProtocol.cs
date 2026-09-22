namespace Rimlight;

/// <summary>
/// What the controller on the serial port understands.
///
/// A separate file rather than a line in Config.cs because the controller probe links it:
/// the probe needs the classification of ports and nothing else from the application.
/// </summary>
public enum DeviceProtocol
{
    /// <summary>
    /// Stock Adalight, as in the Gyver_Ambilight sketch on a Nano: 'A' 'd' 'a', no checksum
    /// on the pixels, and a board that resets when DTR goes up.
    /// </summary>
    Adalight,

    /// <summary>
    /// The AWA extension the HyperSerial firmwares speak: 'A' 'w' 'a' and a Fletcher
    /// checksum over the pixels, so a frame damaged on the wire is dropped instead of shown.
    /// </summary>
    Awa
}
