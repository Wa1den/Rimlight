using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ambilight;

public enum Corner { BottomRight, BottomLeft, TopLeft, TopRight }

public enum CaptureMode
{
    /// <summary>DDA and WGC together, GDI covering the gaps.</summary>
    Auto,
    DdaOnly,
    WgcOnly,
    GdiOnly
}

public sealed class AmbilightConfig
{
    // ---- device -------------------------------------------------------------
    public string MonitorDeviceName { get; set; } = "";      // empty = primary

    /// <summary>
    /// An active Desktop Duplication client can make Windows draw the mouse cursor through
    /// composition instead of its hardware plane, which shows up as cursor flicker. This
    /// lets a single method be forced so the cause can be confirmed and worked around.
    /// </summary>
    public CaptureMode CaptureMode { get; set; } = CaptureMode.Auto;
    public string PortName { get; set; } = "COM4";
    public int BaudRate { get; set; } = 1000000;

    // ---- strip layout -------------------------------------------------------
    public int TopCount { get; set; } = 43;
    public int BottomCount { get; set; } = 43;
    public int LeftCount { get; set; } = 17;
    public int RightCount { get; set; } = 17;

    public Corner StartCorner { get; set; } = Corner.BottomRight;
    public bool CounterClockwise { get; set; } = true;

    /// <summary>
    /// Shifts the whole mapping along the strip, in LEDs. A corner alone cannot express
    /// where the first LED physically sits: starting at a corner and heading up puts it on
    /// the side, heading sideways puts it on the bottom. This covers both, and any strip
    /// that happens to begin mid-edge.
    /// </summary>
    public int IndexOffset { get; set; }

    /// <summary>
    /// Both are percentages of screen WIDTH, applied as the same pixel count on every
    /// side. The strip sits at a uniform physical distance from the screen edge all
    /// round, so equal pixels is right and equal percentages of each dimension is not.
    /// </summary>
    public double EdgeMarginPercent { get; set; } = 3.0;

    /// <summary>
    /// Vertical counterpart, also a percentage of WIDTH so the two stay comparable in
    /// pixels. Strips are rarely mounted at exactly the same distance top and side.
    /// </summary>
    public double EdgeMarginPercentV { get; set; } = 3.0;

    public double DepthPercent { get; set; } = 5.0;

    [JsonIgnore]
    public int TotalLeds => TopCount + BottomCount + LeftCount + RightCount;

    // ---- colour -------------------------------------------------------------
    public double MaxBrightness { get; set; } = 1.0;      // 0..1 overall cap
    public double MinLuma { get; set; } = 0.0;            // below this the strip goes dark
    public double Saturation { get; set; } = 1.0;         // 1 = untouched
    public double Gamma { get; set; } = 2.2;              // 2.2 = neutral round trip
    public int TemperatureK { get; set; } = 6500;         // 6500 = neutral
    public double GainR { get; set; } = 1.0;
    public double GainG { get; set; } = 1.0;
    public double GainB { get; set; } = 1.0;
    public bool Dithering { get; set; } = true;

    /// <summary>Asymmetric smoothing: light rises quickly, falls gently.</summary>
    public double SmoothingRise { get; set; } = 0.55;
    public double SmoothingFall { get; set; } = 0.18;

    // ---- output -------------------------------------------------------------
    public int MaxFps { get; set; } = 60;

    /// <summary>
    /// Skips identical frames. The stock firmware blanks the strip after OFF_TIME (10 s)
    /// of silence, so a keepalive is mandatory, not optional.
    /// </summary>
    public bool SendOnlyOnChange { get; set; } = true;
    public int KeepAliveMs { get; set; } = 2000;

    public bool MinimizeToTray { get; set; } = true;
    public bool Autostart { get; set; }
    public bool WriteLog { get; set; } = true;
    public string Language { get; set; } = "ru";

    // window geometry, so it comes back where it was left
    public double WindowWidth { get; set; } = 1220;
    public double WindowHeight { get; set; } = 820;
    // nullable rather than NaN: System.Text.Json refuses to write NaN and the whole
    // save was failing because of it, taking every other setting down with it
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>
    /// Brightens the on-screen preview only. A byte of 30 renders almost black on a
    /// monitor while the same value on a WS2812 is clearly visible in a dim room, so the
    /// literal colours read far darker than the strip actually looks.
    /// </summary>
    public bool PreviewBoost { get; set; } = true;

    public bool OffOnExit { get; set; } = true;
    public bool OffOnDisplayOff { get; set; } = true;
    public bool OffOnLock { get; set; } = true;
    public bool OffOnSuspend { get; set; } = true;

    // ---- persistence --------------------------------------------------------
    [JsonIgnore]
    public static string LogPath => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Path)!, "ambilight.log");

    [JsonIgnore]
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Ambilight", "config.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AmbilightConfig Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                string json = File.ReadAllText(Path);
                var cfg = JsonSerializer.Deserialize<AmbilightConfig>(json, Options) ?? new AmbilightConfig();

                // a file written before the margin was split has one tuned value; carrying
                // it across beats snapping the vertical side back to a default
                if (!json.Contains("EdgeMarginPercentV"))
                    cfg.EdgeMarginPercentV = cfg.EdgeMarginPercent;

                return cfg;
            }
        }
        catch (Exception ex)
        {
            Ambilight.Capture.ProbeLog.Log("config", "не удалось прочитать конфиг: " + ex.Message);
        }
        return new AmbilightConfig();
    }

    public void SaveTo(string path)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    public static AmbilightConfig LoadFrom(string path) =>
        JsonSerializer.Deserialize<AmbilightConfig>(File.ReadAllText(path), Options)
        ?? throw new InvalidOperationException("пустой файл настроек");

    /// <summary>Independent copy, used as the "last applied" snapshot behind Cancel.</summary>
    public AmbilightConfig Clone()
    {
        var copy = new AmbilightConfig();
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>Copies every stored value onto this instance, keeping the object identity
    /// the running engine already holds.</summary>
    public void CopyFrom(AmbilightConfig other)
    {
        foreach (var prop in typeof(AmbilightConfig).GetProperties())
            if (prop.CanRead && prop.CanWrite)
                prop.SetValue(this, prop.GetValue(other));
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex)
        {
            Ambilight.Capture.ProbeLog.Log("config", "не удалось сохранить конфиг: " + ex.Message);
        }
    }
}
