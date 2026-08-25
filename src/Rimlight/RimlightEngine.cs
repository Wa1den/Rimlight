using System;
using System.Diagnostics;
using System.Threading;
using Rimlight.Capture;
using Rimlight.Capture.Backends;
using Rimlight.Frames;
using Rimlight.Leds;
using Rimlight.Text;

namespace Rimlight;

/// <summary>
/// Ties capture, zone sampling, colour correction and the serial link together.
///
/// Capture runs on its own threads inside the hybrid backend; this class owns one output
/// thread paced to the configured frame rate. The two are decoupled on purpose: capture
/// rate is dictated by the compositor and varies wildly between games, while the strip
/// wants a steady cadence.
/// </summary>
public sealed class RimlightEngine : IDisposable
{
    /// <summary>Per-zone sampling needs enough pixels that each zone covers several.</summary>
    const int ReduceWidth = 256;

    readonly AdalightDevice _device = new();
    readonly ColorPipeline _pipeline = new();
    readonly FramePublisher _publisher = new();
    readonly CropDetector _crop = new();

    HybridBackend? _capture;
    Thread? _outputThread;
    volatile bool _running;
    volatile bool _paused;
    volatile bool _relayout;
    string _pauseReason = "";

    RimlightConfig _cfg = new();
    LedZone[] _zones = Array.Empty<LedZone>();

    /// <summary>
    /// The zones actually read from the frame. Identical to <see cref="_zones"/> until the
    /// crop detector finds bars; keeping the two apart means the preview and the layout
    /// overlay go on showing where the LEDs physically are.
    /// </summary>
    LedZone[] _sampleZones = Array.Empty<LedZone>();
    MonitorInfo? _monitor;

    byte[] _image = Array.Empty<byte>();
    long _imageVersion;
    byte[] _sampled = Array.Empty<byte>();
    byte[] _output = Array.Empty<byte>();

    readonly object _previewLock = new();
    byte[] _preview = Array.Empty<byte>();

    public string DeviceStatus => _device.Status;
    public bool DeviceHasError => _device.HasError;
    public long FramesSent => _device.FramesSent;
    public long FramesSkipped => _device.FramesSkipped;
    public long Reconnects => _device.Reconnects;
    public bool IsPaused => _paused;
    public string PauseReason => _pauseReason;
    public LedZone[] Zones => _zones;
    public string PublisherStatus => _publisher.Status;
    public long FramesPublished => _publisher.Published;
    public HybridBackend? Capture => _capture;
    public double OutputFps { get; private set; }

    /// <summary>Where the picture was last found inside the frame, for the settings panel.</summary>
    public CropRect Crop => _crop.Rect;

    /// <summary>Bumped whenever the zones are rebuilt, so the UI can refresh its preview.</summary>
    public int LayoutVersion { get; private set; }

    /// <summary>
    /// Applies layout changes on the output thread. Offset and margins can then be tuned
    /// live against the actual strip instead of through a reconnect.
    /// </summary>
    public void RequestRelayout() => _relayout = true;


    /// <summary>
    /// Swaps the capture method without touching the serial link, so switching does not
    /// cost a port reopen and its bootloader wait.
    /// </summary>
    public void RestartCapture()
    {
        if (_monitor == null) return;

        _capture?.Stop();
        _capture?.Dispose();
        _capture = NewCapture(_cfg);
        _capture.Start(_monitor);
        ProbeLog.Log(Loc.P("движок", "engine"), Loc.P("метод захвата: ", "capture method: ") + _cfg.CaptureMode);
    }

    static HybridBackend NewCapture(RimlightConfig cfg) => new()
    {
        ReduceWidth = ReduceWidth,

        // no point reducing faster than the strip is driven; the surplus work would only
        // add to the GPU contention that starves the compositor in the first place
        MinReduceIntervalMs = 1000.0 / Math.Clamp(cfg.MaxFps, 1, 240),
        UseDda = cfg.CaptureMode is CaptureMode.Auto or CaptureMode.DdaOnly,
        UseWgc = cfg.CaptureMode is CaptureMode.Auto or CaptureMode.WgcOnly,
        UseGdi = cfg.CaptureMode is CaptureMode.Auto or CaptureMode.GdiOnly
    };

    void RebuildZones()
    {
        if (_monitor == null) return;

        int before = _zones.Length;
        _zones = LedLayout.Build(_cfg, _monitor.Width, _monitor.Height);

        if (_zones.Length != before)
        {
            _sampled = new byte[_zones.Length * 3];
            _output = new byte[_zones.Length * 3];
            lock (_previewLock) _preview = new byte[_zones.Length * 3];
            _pipeline.Reset(_zones.Length);

            // the frame header encodes the LED count, so the device has to be reopened -
            // but without the bootloader pause, or editing the count would blank the strip
            _device.Open(_cfg.PortName, _cfg.BaudRate, _zones.Length, waitBootloader: false);
        }

        RemapZones();
        LayoutVersion++;
    }

    /// <summary>
    /// Rebuilds the sampling rectangles from the strip geometry and the current crop. Called
    /// when either changes, not per frame - the crop settles and then stays put for minutes.
    /// </summary>
    void RemapZones()
    {
        if (_sampleZones.Length != _zones.Length) _sampleZones = new LedZone[_zones.Length];

        if (_crop.Rect.IsFull) Array.Copy(_zones, _sampleZones, _zones.Length);
        else CropMapper.Apply(_zones, _sampleZones, _crop.Rect, _cfg.CropStretch);
    }

    public MonitorInfo? Monitor => _monitor;

    public void Start(RimlightConfig cfg)
    {
        Stop();
        _cfg = cfg;

        _monitor = ScreenChoice.Find(Native.EnumerateMonitors(), cfg.MonitorDeviceName, cfg.MonitorModel);

        if (_monitor == null)
        {
            ProbeLog.Log(Loc.P("движок", "engine"), Loc.P("мониторы не найдены", "no monitors found"));
            return;
        }

        _zones = LedLayout.Build(cfg, _monitor.Width, _monitor.Height);
        _sampled = new byte[_zones.Length * 3];
        _output = new byte[_zones.Length * 3];
        _preview = new byte[_zones.Length * 3];
        _pipeline.Reset(_zones.Length);
        _crop.Reset();
        RemapZones();

        _capture = NewCapture(cfg);
        _capture.Start(_monitor);

        _device.Open(cfg.PortName, cfg.BaudRate, _zones.Length);

        _running = true;
        _outputThread = new Thread(OutputLoop)
        {
            IsBackground = true,
            Name = "ambilight-output",
            Priority = ThreadPriority.AboveNormal
        };
        _outputThread.Start();
        LayoutVersion++;

        ProbeLog.Log(Loc.P("движок", "engine"),
                     Loc.P($"старт: {_monitor.DeviceName} {_monitor.Width}x{_monitor.Height}, диодов {_zones.Length}, порт {cfg.PortName}",
                           $"start: {_monitor.DeviceName} {_monitor.Width}x{_monitor.Height}, {_zones.Length} LEDs, port {cfg.PortName}"));
    }

    void OutputLoop()
    {
        // Thread.Sleep rounds up to the 15.6 ms system tick, which capped output at ~32 fps
        using var pacer = new PrecisionTimer();

        var sw = Stopwatch.StartNew();
        var fpsWindow = Stopwatch.StartNew();
        int framesThisWindow = 0;
        double lastMs = 0;
        double lastCropMs = 0;
        long lastReconnectAttempt = 0;
        bool everSampled = false;

        while (_running)
        {
            double periodMs = 1000.0 / Math.Clamp(_cfg.MaxFps, 1, 240);
            double startMs = sw.Elapsed.TotalMilliseconds;

            // reducing faster than the strip is driven is wasted GPU work, so the capture
            // throttle tracks the cap live rather than only at startup
            if (_capture != null) _capture.MinReduceIntervalMs = periodMs;

            if (_relayout)
            {
                _relayout = false;
                RebuildZones();
            }

            // Switching the detector off has to reach the zones even on a still screen,
            // where no further frame is going to arrive to carry the change.
            if (!_cfg.AdaptiveCrop && !_crop.Rect.IsFull)
            {
                _crop.Reset();
                RemapZones();
            }

            // Followed live rather than only at startup, so the checkbox takes effect
            // without restarting the engine and its 2.5 s bootloader wait.
            if (_cfg.PublishFrames) _publisher.Open();
            else if (_publisher.IsOpen) _publisher.Close();

            if (_paused)
            {
                Thread.Sleep(50);
                lastMs = sw.Elapsed.TotalMilliseconds;
                continue;
            }

            int w = 0, h = 0, stride = 0;
            bool haveNewFrame = _capture != null &&
                _capture.TryGetImage(ref _image, ref _imageVersion, out w, out h, out stride) &&
                w > 0 && h > 0;

            if (haveNewFrame)
            {
                // Straight off the capture, before any of our own colour work: the module
                // downstream has its own zones and its own correction to apply.
                if (_cfg.PublishFrames) _publisher.Publish(_image, w, h, stride, _monitor);

                // Before sampling, and only on a frame that is actually new: the detector
                // measures the picture, and its hold time counts real elapsed time.
                if (_cfg.AdaptiveCrop)
                {
                    double cropDt = startMs - lastCropMs;
                    lastCropMs = startMs;
                    if (_crop.Update(_image, w, h, stride, _cfg.ToCropSettings(),
                                     cropDt <= 0 || cropDt > 1000 ? periodMs : cropDt))
                        RemapZones();
                }

                ZoneSampler.Sample(_image, w, h, stride, _sampleZones, _sampled);
                everSampled = true;
                framesThisWindow++;
            }

            // Smoothing is a filter over time, so it has to advance on the clock rather than
            // on capture events. Stepping it only when a frame arrived left it stranded
            // half-way whenever the screen went still after a change - most visibly in the
            // layout overlay, where one click produces exactly one frame and then silence.
            if (everSampled)
            {
                double dt = startMs - lastMs;
                lastMs = startMs;
                _pipeline.Process(_sampled, _output, _cfg.ToColorSettings(), _zones.Length, dt <= 0 ? periodMs : dt);
                NeutraliseShadows(_cfg.ShadowNeutral);

                // после обесцвечивания, чтобы превью показывало то же, что уходит на ленту
                lock (_previewLock) Buffer.BlockCopy(_output, 0, _preview, 0, _output.Length);
            }

            // Send on every tick, not only when capture had something new. A still screen
            // produces no frames at all, and the firmware blanks the strip after 10 s of
            // silence - so the colours have to keep going out regardless. Send() itself
            // skips identical frames and honours the keepalive interval.
            if (_output.Length > 0 && !_device.Send(_output, _cfg.SendOnlyOnChange, _cfg.KeepAliveMs))
            {
                // the port dropped; retry at a human pace rather than spinning
                long now = Environment.TickCount64;
                if (now - lastReconnectAttempt > 2000)
                {
                    lastReconnectAttempt = now;
                    _device.TryReconnect(_cfg.PortName, _cfg.BaudRate, _zones.Length);
                }
            }

            if (fpsWindow.ElapsedMilliseconds >= 1000)
            {
                OutputFps = framesThisWindow * 1000.0 / fpsWindow.ElapsedMilliseconds;
                framesThisWindow = 0;
                fpsWindow.Restart();
            }

            double restMs = periodMs - (sw.Elapsed.TotalMilliseconds - startMs);
            if (restMs > 0.2) pacer.Wait(restMs);
        }
    }

    /// <summary>
    /// Takes the colour out of what is nearly black, once the pipeline has had its say.
    ///
    /// Done here rather than inside the pipeline because that one is the shared copy of the
    /// case lighting code, kept identical on both sides. Working on the finished bytes is
    /// enough: the tint is a proportion between the channels, and pulling them back towards
    /// their own luminance removes it without touching how bright the LED ends up.
    ///
    /// Note the scale. MinLuma is compared in linear light inside the pipeline, this knee
    /// against gamma-encoded bytes, so the two numbers do not mean the same thing.
    /// </summary>
    void NeutraliseShadows(double knee)
    {
        if (knee <= 0) return;

        double limit = knee * 255.0;

        for (int i = 0; i + 2 < _output.Length; i += 3)
        {
            double r = _output[i], g = _output[i + 1], b = _output[i + 2];

            double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            if (y >= limit) continue;

            // 1 на пороге, 0 на чёрном: чем темнее, тем ближе к серому
            double keep = y / limit;

            _output[i] = Fade(y, r, keep);
            _output[i + 1] = Fade(y, g, keep);
            _output[i + 2] = Fade(y, b, keep);
        }
    }

    static byte Fade(double luma, double channel, double keep) =>
        (byte)Math.Clamp(Math.Round(luma + (channel - luma) * keep), 0, 255);

    /// <summary>Latest colours actually sent, for the on-screen preview.</summary>
    public void CopyPreview(byte[] dest)
    {
        lock (_previewLock)
        {
            if (dest.Length >= _preview.Length && _preview.Length > 0)
                Buffer.BlockCopy(_preview, 0, dest, 0, _preview.Length);
        }
    }

    /// <summary>Darkens the strip and stops sending, for lock, sleep and display off.</summary>
    public void Pause(string reason)
    {
        if (_paused) return;
        _pauseReason = reason;
        _paused = true;
        _device.Blackout();
        ProbeLog.Log(Loc.P("движок", "engine"), Loc.P("пауза: ", "paused: ") + reason);
    }

    public void Resume()
    {
        if (!_paused) return;
        _paused = false;
        _pauseReason = "";
        _pipeline.Reset(_zones.Length);   // do not fade in from stale colours
        ProbeLog.Log(Loc.P("движок", "engine"), Loc.P("продолжение", "resumed"));
    }

    public void Stop()
    {
        if (!_running && _capture == null) return;

        _running = false;
        _outputThread?.Join(2000);
        _outputThread = null;

        if (_cfg.OffOnExit) _device.Blackout();
        _device.Close();
        _publisher.Close();

        _capture?.Stop();
        _capture?.Dispose();
        _capture = null;

        ProbeLog.Log(Loc.P("движок", "engine"), Loc.P("стоп", "stopped"));
    }

    public void Dispose() => Stop();
}
