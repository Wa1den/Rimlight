using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Ambilight.Capture;
using Ambilight.Capture.Backends;

namespace Ambilight;

/// <summary>
/// Ties capture, zone sampling, colour correction and the serial link together.
///
/// Capture runs on its own threads inside the hybrid backend; this class owns one output
/// thread paced to the configured frame rate. The two are decoupled on purpose: capture
/// rate is dictated by the compositor and varies wildly between games, while the strip
/// wants a steady cadence.
/// </summary>
public sealed class AmbilightEngine : IDisposable
{
    /// <summary>Per-zone sampling needs enough pixels that each zone covers several.</summary>
    const int ReduceWidth = 256;

    readonly AdalightDevice _device = new();
    readonly ColorPipeline _pipeline = new();

    HybridBackend? _capture;
    Thread? _outputThread;
    volatile bool _running;
    volatile bool _paused;
    volatile bool _relayout;
    string _pauseReason = "";

    AmbilightConfig _cfg = new();
    LedZone[] _zones = Array.Empty<LedZone>();
    MonitorInfo? _monitor;

    byte[] _image = Array.Empty<byte>();
    long _imageVersion;
    byte[] _sampled = Array.Empty<byte>();
    byte[] _output = Array.Empty<byte>();

    readonly object _previewLock = new();
    byte[] _preview = Array.Empty<byte>();

    public string DeviceStatus => _device.Status;
    public long FramesSent => _device.FramesSent;
    public long FramesSkipped => _device.FramesSkipped;
    public long Reconnects => _device.Reconnects;
    public bool IsPaused => _paused;
    public string PauseReason => _pauseReason;
    public LedZone[] Zones => _zones;
    public HybridBackend? Capture => _capture;
    public double OutputFps { get; private set; }

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
        ProbeLog.Log("движок", "метод захвата: " + _cfg.CaptureMode);
    }

    static HybridBackend NewCapture(AmbilightConfig cfg) => new()
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

        LayoutVersion++;
    }

    public MonitorInfo? Monitor => _monitor;

    public void Start(AmbilightConfig cfg)
    {
        Stop();
        _cfg = cfg;

        var monitors = Native.EnumerateMonitors();
        _monitor = monitors.FirstOrDefault(m => m.DeviceName == cfg.MonitorDeviceName)
                   ?? monitors.FirstOrDefault(m => m.IsPrimary)
                   ?? monitors.FirstOrDefault();

        if (_monitor == null)
        {
            ProbeLog.Log("движок", "мониторы не найдены");
            return;
        }

        _zones = LedLayout.Build(cfg, _monitor.Width, _monitor.Height);
        _sampled = new byte[_zones.Length * 3];
        _output = new byte[_zones.Length * 3];
        _preview = new byte[_zones.Length * 3];
        _pipeline.Reset(_zones.Length);

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

        ProbeLog.Log("движок", $"старт: {_monitor.DeviceName} {_monitor.Width}x{_monitor.Height}, " +
                               $"диодов {_zones.Length}, порт {cfg.PortName}");
    }

    void OutputLoop()
    {
        // Thread.Sleep rounds up to the 15.6 ms system tick, which capped output at ~32 fps
        using var pacer = new PrecisionTimer();

        var sw = Stopwatch.StartNew();
        var fpsWindow = Stopwatch.StartNew();
        int framesThisWindow = 0;
        double lastMs = 0;
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
                LedLayout.Sample(_image, w, h, stride, _zones, _sampled);
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
                _pipeline.Process(_sampled, _output, _cfg, _zones.Length, dt <= 0 ? periodMs : dt);

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
        ProbeLog.Log("движок", "пауза: " + reason);
    }

    public void Resume()
    {
        if (!_paused) return;
        _paused = false;
        _pauseReason = "";
        _pipeline.Reset(_zones.Length);   // do not fade in from stale colours
        ProbeLog.Log("движок", "продолжение");
    }

    public void Stop()
    {
        if (!_running && _capture == null) return;

        _running = false;
        _outputThread?.Join(2000);
        _outputThread = null;

        if (_cfg.OffOnExit) _device.Blackout();
        _device.Close();

        _capture?.Stop();
        _capture?.Dispose();
        _capture = null;

        ProbeLog.Log("движок", "стоп");
    }

    public void Dispose() => Stop();
}
