using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rimlight.Leds;

namespace Rimlight;

public enum Corner { BottomRight, BottomLeft, TopLeft, TopRight }

public enum CaptureMode
{
    /// <summary>DDA and WGC together, GDI covering the gaps.</summary>
    Auto,
    DdaOnly,
    WgcOnly,
    GdiOnly
}

public sealed class RimlightConfig
{
    // ---- device -------------------------------------------------------------
    public string MonitorDeviceName { get; set; } = "";      // empty = primary

    /// <summary>
    /// Which screen the layout is bound to, stored as the EDID model rather than the device
    /// name. Windows hands out <c>\\.\DISPLAYn</c> in the order it finds the outputs, so moving
    /// a cable between ports of the graphics card renumbers them: in one observed case an
    /// ultrawide and a portrait screen swapped names between sessions, which would have
    /// pointed the capture at the wrong screen. The device name is kept as well, to tell
    /// apart two screens of the same model.
    /// </summary>
    public string MonitorModel { get; set; } = "";

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

    // ---- adaptive crop ------------------------------------------------------

    /// <summary>
    /// Follows the black bars of letterboxed material and samples the picture inside them
    /// instead of the screen. Off by default: it changes where every zone reads from, which
    /// is not something to switch on behind the back of a strip that is already tuned.
    /// </summary>
    public bool AdaptiveCrop { get; set; }

    /// <summary>Letterboxing - bars above and below. The common case, so on by default.</summary>
    public bool CropVertical { get; set; } = true;

    /// <summary>Pillarboxing - bars left and right. 4:3 material on a wide screen.</summary>
    public bool CropHorizontal { get; set; } = true;

    /// <summary>Below this a bar is taken for a dark edge and ignored. Percent of the side.</summary>
    public double CropMinPercent { get; set; } = 2.0;

    /// <summary>
    /// Ceiling on the crop, as a percent of the side.
    ///
    /// Sized for a 21:9 screen, where 16:9 material leaves a pillar of about 12% at each
    /// end. A 16:9 screen wants more: 2.39:1 film puts about 17% of the height in each
    /// letterbox bar, and at this setting the sampling would stop short of the picture.
    /// </summary>
    public double CropMaxPercent { get; set; } = 14.0;

    /// <summary>Per-channel value below which a pixel counts as black.</summary>
    public int CropBlackLevel { get; set; } = 16;

    /// <summary>
    /// How much of a lit run inside the bar is stepped over rather than taken for the edge
    /// of the picture - subtitles, the progress bar, the buttons of a player. Percent of
    /// the side.
    ///
    /// Subtitles are handled by ignoring the middle of each row and hold at any setting,
    /// but the buttons of a player sit out at the ends where they are read: below five the
    /// bar is lost the moment the mouse moves, and the default leaves margin above that.
    /// Raised too far, a genuinely thin band of picture is stepped over as if it were a
    /// control as well.
    /// </summary>
    public double CropOverlookPercent { get; set; } = 8.0;

    /// <summary>How long a new reading has to hold before the sampling moves.</summary>
    public double CropHoldMs { get; set; } = 700;

    /// <summary>
    /// Extra margin taken inside the picture once a bar is found, in percent of the side.
    /// See <see cref="CropSettings.InsetPercent"/>.
    /// </summary>
    public double CropInsetPercent { get; set; } = 0.5;

    /// <summary>
    /// Spreads the picture across the whole ring, so the LEDs behind a bar light from the
    /// nearest part of the picture rather than sitting dark. With this off the zones only
    /// slide clear of the bars and keep their positions otherwise.
    /// </summary>
    public bool CropStretch { get; set; } = true;

    /// <summary>The subset the detector reads, on the same footing as the colour settings.</summary>
    public CropSettings ToCropSettings() => new()
    {
        Vertical = CropVertical,
        Horizontal = CropHorizontal,
        MinPercent = CropMinPercent,
        MaxPercent = CropMaxPercent,
        BlackLevel = CropBlackLevel,
        OverlookPercent = CropOverlookPercent,
        HoldMs = CropHoldMs,
        InsetPercent = CropInsetPercent
    };

    // ---- frame --------------------------------------------------------------

    /// <summary>
    /// How the frame is worked over before the zones are read, in percent from -50 to 50.
    /// Zero takes the picture as captured.
    ///
    /// Below zero it is defocused: an LED then draws its colour from the neighbourhood
    /// around its sampling zone without that zone growing, and neighbouring stretches of
    /// the strip run into each other. Above zero it is sharpened: neighbouring zones are
    /// pushed apart, so a light patch beside a dark one reads brighter. See
    /// <see cref="FrameFilter"/>.
    /// </summary>
    public int Sharpness { get; set; }

    // ---- colour -------------------------------------------------------------
    public double MaxBrightness { get; set; } = 1.0;      // 0..1 overall cap
    public double MinLuma { get; set; } = 0.0;            // below this the strip goes dark

    /// <summary>
    /// How far up the scale colour is taken out of the shadows. Zero switches it off.
    ///
    /// White balance is a proportion between the channels, so it applies the same tint at
    /// every level, and where the picture is almost black that tint is all there is to see.
    /// At 5000 K the gains are R 1.000, G 0.793, B 0.629, so a grey of 15 reaches the strip
    /// as 23, 20, 18: a black player bar with white digits lights it dark red. Fading the
    /// channels towards their own luminance as the level falls removes the tint without
    /// changing how bright the LED ends up, and leaves normal content alone because there
    /// the level is high.
    ///
    /// Applied after the pipeline rather than inside it: ColorPipeline is the shared copy
    /// kept identical with the case lighting module, so it is not this project's to change.
    /// </summary>
    public double ShadowNeutral { get; set; }

    /// <summary>
    /// Level the strip never drops below, 0..1. Zero switches it off.
    ///
    /// Works with <see cref="MinLuma"/> rather than against it: the cutoff decides what
    /// counts as black, and this decides what black looks like. Only LEDs whose every
    /// channel is under the level are lifted, so a dark scene that still has a colour in
    /// it keeps that colour.
    /// </summary>
    public double MinBacklight { get; set; }

    public double Saturation { get; set; } = 1.0;         // 1 = untouched
    public double Gamma { get; set; } = 1.0;              // 2.2 = neutral round trip
    public int TemperatureK { get; set; } = 5600;         // 6500 = neutral
    public double GainR { get; set; } = 1.0;
    public double GainG { get; set; } = 1.0;
    public double GainB { get; set; } = 1.0;
    public bool Dithering { get; set; }

    /// <summary>
    /// What the strip is allowed to draw, in amperes. Zero, the default, is no ceiling.
    ///
    /// Stated as a current rather than as a share of full brightness because that is what
    /// the setting is actually about: the supply is a fixed number of amperes whatever the
    /// strip is asked to show, and a share of full brightness would silently mean a
    /// different current every time the LED count changed.
    ///
    /// Unlike <see cref="MaxBrightness"/> this only engages on frames that would really
    /// cost that much, so an ordinary picture - where most of the strip is dim - is left
    /// at full brightness.
    /// </summary>
    public double PowerLimitAmps { get; set; }

    /// <summary>
    /// What one LED draws at full white, in amperes. A WS2812 is three dice of about 20 mA
    /// each; strips vary a little with the LED bin and the voltage that survives the run,
    /// so this is a nameplate figure and not a measurement of any particular strip.
    /// </summary>
    public const double AmpsPerLedWhite = 0.060;

    /// <summary>
    /// What one LED draws showing nothing at all. The controller inside each package runs
    /// whatever the colour is, and across a hundred-odd LEDs it adds up to a tenth of an
    /// amp - small, but it is spent before any light is made, so the ceiling cannot hand
    /// it out.
    /// </summary>
    public const double AmpsPerLedIdle = 0.001;

    /// <summary>What the whole strip draws at full white, ceiling included.</summary>
    [JsonIgnore]
    public double FullWhiteAmps => TotalLeds * (AmpsPerLedWhite + AmpsPerLedIdle);

    /// <summary>
    /// The ceiling as the pipeline wants it: a share of full duty. One means no ceiling,
    /// which is also what a setting at or above what the strip can draw comes to.
    /// </summary>
    [JsonIgnore]
    public double PowerLimitFraction
    {
        get
        {
            if (PowerLimitAmps <= 0 || PowerLimitAmps >= FullWhiteAmps) return 1.0;

            double forLight = PowerLimitAmps - TotalLeds * AmpsPerLedIdle;
            return Math.Clamp(forLight / (TotalLeds * AmpsPerLedWhite), 0, 1);
        }
    }

    /// <summary>Asymmetric smoothing: light rises quickly, falls gently.</summary>
    public double SmoothingRise { get; set; } = 0.9;
    public double SmoothingFall { get; set; } = 0.9;

    /// <summary>
    /// The subset the colour pipeline actually reads. Handing it the whole config would
    /// tie Rimlight.Core to this application's settings file.
    /// </summary>
    public ColorSettings ToColorSettings() => new()
    {
        MaxBrightness = MaxBrightness,
        MinLuma = MinLuma,
        Saturation = Saturation,
        Gamma = Gamma,
        TemperatureK = TemperatureK,
        GainR = GainR,
        GainG = GainG,
        GainB = GainB,
        Dithering = Dithering,
        MinBacklight = MinBacklight,
        PowerLimit = PowerLimitFraction,
        SmoothingRise = SmoothingRise,
        SmoothingFall = SmoothingFall
    };

    // ---- output -------------------------------------------------------------

    /// <summary>
    /// Ceiling on how often a frame is reduced and sent, in frames per second. Zero, the
    /// default, removes the ceiling and leaves only the floor the controller itself sets.
    ///
    /// The cap is not what protects the strip: <see cref="AdalightDevice"/> already refuses
    /// a frame that comes closer together than the controller's own cycle, whatever this
    /// says. What the cap saves is graphics card work, and it is paid for in latency,
    /// because a frame arriving inside the throttle window is dropped rather than held -
    /// the picture then waits for the next one. Measured through the screen with a camera
    /// at 240 fps: 30 ms from screen to strip at 60, 10-20 ms with no cap.
    /// </summary>
    public int MaxFps { get; set; }

    /// <summary>
    /// Skips identical frames. The stock firmware blanks the strip after OFF_TIME (10 s)
    /// of silence, so a keepalive is mandatory, not optional.
    /// </summary>
    public bool SendOnlyOnChange { get; set; }
    public int KeepAliveMs { get; set; } = 2000;

    /// <summary>
    /// Mirrors every captured frame into shared memory so the case-lighting module can
    /// work off the same picture instead of capturing the screen a second time. Off by
    /// default - with nothing attached it is a memcpy nobody reads.
    /// </summary>
    public bool PublishFrames { get; set; }

    /// <summary>
    /// The close button hides the window instead of quitting. Off by default: a window
    /// that will not close is a surprise, and the tray icon is there to be found only by
    /// someone who already knows the program is running.
    /// </summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Open straight into the tray - useful together with autostart.</summary>
    public bool StartMinimized { get; set; }
    public bool Autostart { get; set; }
    public bool WriteLog { get; set; }

    /// <summary>
    /// Asks GitHub at startup whether a newer release exists. Off by default: it is the
    /// only thing here that reaches outside the machine, and that is not a thing to start
    /// doing without being asked.
    /// </summary>
    public bool CheckUpdates { get; set; }
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

    /// <summary>The zone preview panel; off shrinks the window to the settings column.</summary>
    public bool ShowPreview { get; set; } = true;

    /// <summary>The statistics block under the preview; only visible while the preview is.</summary>
    public bool ShowStats { get; set; } = true;

    /// <summary>
    /// Adds the diagnostic rows - latency, its split by stage, the source breakdown - and
    /// turns on the per-second line in the log. Off by default: it answers questions
    /// nobody has until something is wrong, and the log line is a line a second.
    /// </summary>
    public bool DetailedStats { get; set; }

    public bool OffOnExit { get; set; } = true;
    public bool OffOnDisplayOff { get; set; } = true;
    public bool OffOnLock { get; set; }
    public bool OffOnSuspend { get; set; } = true;

    // ---- persistence --------------------------------------------------------
    [JsonIgnore]
    public static string LogPath => System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Path)!, "rimlight.log");

    [JsonIgnore]
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Rimlight", "config.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Set by the migration, reported once the log destination is known. Load() runs from a
    /// field initialiser, before the log has been pointed at the settings folder, so logging
    /// directly here would drop a stray file next to the executable.
    /// </summary>
    public static string? MigrationNote { get; private set; }

    /// <summary>Folder used before the application was renamed from Ambilight.</summary>
    static string LegacyDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ambilight");

    /// <summary>
    /// Carries settings over from the old name once. Hand-tuned margins, gains and offsets
    /// represent real time at the strip, so a rename must not quietly discard them.
    /// </summary>
    static void MigrateFromLegacy()
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Path)!;
            if (File.Exists(Path) || !File.Exists(System.IO.Path.Combine(LegacyDirectory, "config.json")))
                return;

            System.IO.Directory.CreateDirectory(dir);
            File.Copy(System.IO.Path.Combine(LegacyDirectory, "config.json"), Path);

            // translations too, so hand edits survive the move
            string langFrom = System.IO.Path.Combine(LegacyDirectory, "lang");
            string langTo = System.IO.Path.Combine(dir, "lang");
            if (System.IO.Directory.Exists(langFrom) && !System.IO.Directory.Exists(langTo))
            {
                System.IO.Directory.CreateDirectory(langTo);
                foreach (var f in System.IO.Directory.GetFiles(langFrom, "*.json"))
                    File.Copy(f, System.IO.Path.Combine(langTo, System.IO.Path.GetFileName(f)), true);
            }

            MigrationNote = "настройки перенесены из " + LegacyDirectory;
        }
        catch (Exception ex)
        {
            MigrationNote = "не удалось перенести старые настройки: " + ex.Message;
        }
    }

    public static RimlightConfig Load()
    {
        MigrateFromLegacy();
        try
        {
            if (File.Exists(Path))
            {
                string json = File.ReadAllText(Path);
                var cfg = JsonSerializer.Deserialize<RimlightConfig>(json, Options) ?? new RimlightConfig();

                // a file written before the margin was split has one tuned value; carrying
                // it across beats snapping the vertical side back to a default
                if (!json.Contains("EdgeMarginPercentV"))
                    cfg.EdgeMarginPercentV = cfg.EdgeMarginPercent;

                return cfg;
            }
        }
        catch (Exception ex)
        {
            Rimlight.Capture.ProbeLog.Log("config", "не удалось прочитать конфиг: " + ex.Message);
        }
        return new RimlightConfig();
    }

    public void SaveTo(string path)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    public static RimlightConfig LoadFrom(string path) =>
        JsonSerializer.Deserialize<RimlightConfig>(File.ReadAllText(path), Options)
        ?? throw new InvalidOperationException("пустой файл настроек");

    /// <summary>Independent copy, used as the "last applied" snapshot behind Cancel.</summary>
    public RimlightConfig Clone()
    {
        var copy = new RimlightConfig();
        copy.CopyFrom(this);
        return copy;
    }

    /// <summary>Copies every stored value onto this instance, keeping the object identity
    /// the running engine already holds.</summary>
    public void CopyFrom(RimlightConfig other)
    {
        foreach (var prop in typeof(RimlightConfig).GetProperties())
            if (prop.CanRead && prop.CanWrite)
                prop.SetValue(this, prop.GetValue(other));
    }

    /// <summary>
    /// Settings that describe this particular installation rather than a preference, and
    /// so survive a reset: which screen and port, how the strip is physically laid out,
    /// where the window sits, and which language the interface is in.
    ///
    /// Everything else is reset by name lookup rather than by an explicit list, so a
    /// setting added later is covered without anyone having to remember this method.
    /// </summary>
    /// <summary>
    /// Where the window is and how big it is. Not settings the user edits - they are
    /// written on the way out rather than by the Apply button - so they are left out of
    /// both the reset and the comparison behind the unsaved-changes bar.
    /// </summary>
    static readonly string[] Geometry =
    {
        nameof(WindowWidth), nameof(WindowHeight), nameof(WindowLeft), nameof(WindowTop),
        nameof(WindowMaximized)
    };

    static readonly string[] Preserved = new[]
    {
        nameof(MonitorDeviceName), nameof(MonitorModel), nameof(CaptureMode),
        nameof(PortName), nameof(BaudRate),

        nameof(TopCount), nameof(BottomCount), nameof(LeftCount), nameof(RightCount),
        nameof(StartCorner), nameof(CounterClockwise), nameof(IndexOffset),
        nameof(EdgeMarginPercent), nameof(EdgeMarginPercentV), nameof(DepthPercent),

        nameof(Language)
    }.Concat(Geometry).ToArray();

    /// <summary>
    /// Whether the two carry the same settings, so an edit that has been undone by hand
    /// can stop counting as an unsaved change.
    ///
    /// Slider values compare exactly because a slider snaps to its tick and the value it
    /// stores is a pure function of that tick - returning to the same position produces
    /// the same double, bit for bit.
    /// </summary>
    public bool SameSettingsAs(RimlightConfig other)
    {
        foreach (var prop in typeof(RimlightConfig).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (Array.IndexOf(Geometry, prop.Name) >= 0) continue;

            if (!Equals(prop.GetValue(this), prop.GetValue(other))) return false;
        }

        return true;
    }

    public void ResetToDefaults()
    {
        var shipped = new RimlightConfig();

        foreach (var prop in typeof(RimlightConfig).GetProperties())
            if (prop.CanRead && prop.CanWrite && Array.IndexOf(Preserved, prop.Name) < 0)
                prop.SetValue(this, prop.GetValue(shipped));
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
            Rimlight.Capture.ProbeLog.Log("config", "не удалось сохранить конфиг: " + ex.Message);
        }
    }
}
