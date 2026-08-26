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

    /// <summary>
    /// The capture throttle is set a little under the output period.
    ///
    /// Exactly one period looks right and is not: capture and output run on unrelated
    /// clocks, so a frame arriving a hair early was thrown away and the picture waited a
    /// whole extra period for the next one. The slack costs a few percent more reductions
    /// and removes that beat.
    /// </summary>
    const double ReduceSlack = 0.8;

    readonly AdalightDevice _device = new();
    readonly ColorPipeline _pipeline = new();
    readonly FramePublisher _publisher = new();
    readonly CropDetector _crop = new();

    HybridBackend? _capture;
    Thread? _outputThread;
    volatile bool _running;
    volatile bool _paused;
    volatile bool _relayout;
    volatile bool _restartCapture;
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

    /// <summary>
    /// How old the picture was when it reached the wire. Measured from the present time
    /// the compositor reports, so it covers the whole path - capture, readback, relay,
    /// pacing, colour - and is the number to watch when tuning any of them.
    ///
    /// Three figures because one will not do. The average says how it feels; the worst
    /// over the last ten seconds catches the stalls the average hides; and p99 says
    /// whether that worst is one frame in a thousand or one in twenty, which is the
    /// difference between a curiosity and the thing to fix next.
    ///
    /// A worst-ever peak was tried here and thrown out: one hiccup at startup pinned it
    /// for the rest of the session, after which it said nothing at all.
    /// </summary>
    public double FrameAgeMs { get; private set; }
    public double FrameAgeMaxMs { get; private set; }
    public double FrameAgeP99Ms { get; private set; }

    /// <summary>Seconds the rolling worst looks back over.</summary>
    const int AgeWindowSeconds = 10;

    /// <summary>Frames kept for the percentile - about eight seconds at 60 fps.</summary>
    const int AgeSamples = 512;

    /// <summary>Frames dropped because the serial port had not drained the previous one.</summary>
    public long FramesQueueFull => _device.FramesQueueFull;

    /// <summary>Frames dropped for arriving faster than the strip can be driven.</summary>
    public long FramesTooSoon => _device.FramesTooSoon;
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
    ///
    /// Deferred to the output thread rather than done on the caller's: that thread now
    /// waits on the backend's frame signal, and disposing the handle from under a waiter
    /// is a crash, where a stale frame for one tick is nothing.
    /// </summary>
    public void RestartCapture() => _restartCapture = true;

    void ApplyCaptureRestart()
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
        MinReduceIntervalMs = 1000.0 / Math.Clamp(cfg.MaxFps, 1, 240) * ReduceSlack,
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

        _restartCapture = false;
        FrameAgeMs = FrameAgeMaxMs = FrameAgeP99Ms = 0;
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
        double ageSum = 0, ageMax = 0;
        int ageCount = 0;

        // one bucket per second; the worst shown is the worst still inside the window
        var ageSeconds = new double[AgeWindowSeconds];
        int ageSecond = 0;

        // and every sample itself, for the percentile
        var ageRing = new double[AgeSamples];
        var ageSort = new double[AgeSamples];
        int ageRingIdx = 0, ageRingCount = 0;

        // Stamp of the newest picture folded into the output buffer, held until that
        // buffer actually reaches the wire. Timing it where the frame was processed
        // instead would have hidden the very delay a refused write causes.
        long pendingStamp = 0;

        // Rebuilt only when the capture object itself changes, which is why the swap was
        // moved onto this thread - see RestartCapture.
        HybridBackend? waitTarget = null;
        WaitHandle[] waitSet = Array.Empty<WaitHandle>();

        double lastMs = 0;
        double lastCropMs = 0;
        long lastReconnectAttempt = 0;
        bool everSampled = false;

        while (_running)
        {
            // Never ask for a rate the strip cannot take. At 1 Mbaud a 122-LED frame is
            // 3.7 ms on the wire and another 3.7 ms latching into the strip, so anything
            // above about 135 fps is a request the port can only answer by refusing - and
            // a refusal is worse than a slower cadence, because the colours then wait for
            // a whole further period. The setting stays the ceiling; this is the floor.
            double periodMs = Math.Max(1000.0 / Math.Clamp(_cfg.MaxFps, 1, 240),
                                       _device.MinFramePeriodMs);
            double startMs = sw.Elapsed.TotalMilliseconds;

            // reducing faster than the strip is driven is wasted GPU work, so the capture
            // throttle tracks the cap live rather than only at startup
            if (_capture != null) _capture.MinReduceIntervalMs = periodMs * ReduceSlack;

            if (_restartCapture)
            {
                _restartCapture = false;
                ApplyCaptureRestart();
            }

            if (!ReferenceEquals(waitTarget, _capture))
            {
                waitTarget = _capture;
                waitSet = waitTarget != null && pacer.Handle != null
                    ? new[] { pacer.Handle, waitTarget.FrameSignal }
                    : Array.Empty<WaitHandle>();
            }

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

            // Consume the signal before reading the frame rather than after. The image is
            // taken by polling, so a publish already picked up this way would otherwise
            // leave the event set and wake the loop a second time for a frame it has
            // seen - a pass that sends again too soon and is refused for it.
            _capture?.FrameSignal.Reset();

            int w = 0, h = 0, stride = 0;
            long stamp = 0;
            bool haveNewFrame = _capture != null &&
                _capture.TryGetImage(ref _image, ref _imageVersion, out w, out h, out stride, out stamp) &&
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
            long sentBefore = _device.FramesSent;

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

            if (haveNewFrame && stamp != 0) pendingStamp = stamp;

            // Only frames that carried new pixels, and only once they are on the wire: a
            // keepalive resend says nothing about how fast the picture gets through, and
            // a frame the port refused has not got through at all yet.
            if (pendingStamp != 0 && _device.FramesSent != sentBefore)
            {
                double ageMs = (Stopwatch.GetTimestamp() - pendingStamp) * 1000.0 / Stopwatch.Frequency;
                pendingStamp = 0;
                ageSum += ageMs;
                ageCount++;
                if (ageMs > ageMax) ageMax = ageMs;

                ageRing[ageRingIdx] = ageMs;
                ageRingIdx = (ageRingIdx + 1) % AgeSamples;
                if (ageRingCount < AgeSamples) ageRingCount++;
            }

            if (fpsWindow.ElapsedMilliseconds >= 1000)
            {
                OutputFps = framesThisWindow * 1000.0 / fpsWindow.ElapsedMilliseconds;
                FrameAgeMs = ageCount > 0 ? ageSum / ageCount : 0;

                ageSeconds[ageSecond] = ageMax;
                ageSecond = (ageSecond + 1) % ageSeconds.Length;

                double rolling = 0;
                foreach (double v in ageSeconds)
                    if (v > rolling) rolling = v;
                FrameAgeMaxMs = rolling;

                if (ageRingCount > 0)
                {
                    Array.Copy(ageRing, ageSort, ageRingCount);
                    Array.Sort(ageSort, 0, ageRingCount);
                    FrameAgeP99Ms = ageSort[Math.Min(ageRingCount - 1, (int)(ageRingCount * 0.99))];
                }

                framesThisWindow = 0;
                ageSum = ageMax = 0;
                ageCount = 0;
                fpsWindow.Restart();
            }

            double restMs = periodMs - (sw.Elapsed.TotalMilliseconds - startMs);
            if (restMs <= 0.2) continue;

            // Sit out the rest of the period, but come back the moment capture publishes.
            // The timeout argument of WaitAny cannot do this on its own: like Thread.Sleep
            // it rounds up to the 15.6 ms system tick, which is the whole reason the pacer
            // exists - so the pacer joins the wait as a handle of its own.
            if (waitSet.Length == 2 && pacer.Arm(restMs)) WaitHandle.WaitAny(waitSet);
            else pacer.Wait(restMs);
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
