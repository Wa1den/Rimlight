using System;
using System.Diagnostics;
using System.Threading;

namespace Rimlight.Capture.Backends;

/// <summary>
/// Where a frame was at each step of the way, on the Stopwatch clock.
///
/// One number for the whole path says how bad it is but never where, and guessing at
/// where has already cost several rounds of the wrong fix. Present comes from the
/// compositor, Acquire is when capture got its hands on the frame, Ready is when the
/// reduced pixels were back in main memory.
/// </summary>
public readonly record struct FrameStamps(long Present, long Acquire, long Ready)
{
    public bool IsEmpty => Present == 0;

    /// <summary>For paths with nothing better to say than "now".</summary>
    public static FrameStamps Now()
    {
        long t = Stopwatch.GetTimestamp();
        return new FrameStamps(t, t, t);
    }
}

public interface ICaptureBackend : IDisposable
{
    string Name { get; }
    BackendMetrics Metrics { get; }
    bool IsRunning { get; }
    void Start(MonitorInfo monitor);
    void Stop();
    void RequestSnapshot();
}

/// <summary>
/// Owns one dedicated capture thread. Each backend runs fully independently so
/// that one failing cannot take the others down - which is the entire point of
/// running three of them side by side.
/// </summary>
public abstract class CaptureBackendBase : ICaptureBackend
{
    volatile bool _running;
    Thread? _thread;

    protected MonitorInfo Monitor { get; private set; } = null!;

    public BackendMetrics Metrics { get; } = new();
    public abstract string Name { get; }
    public bool IsRunning => _running;

    protected bool ShouldRun => _running;

    /// <summary>
    /// Width the frame is reduced to. One average colour needs almost nothing; per-zone
    /// extraction needs enough pixels that each zone still covers several of them.
    /// </summary>
    public int ReduceWidth { get; set; } = 64;

    /// <summary>
    /// Lower bound on the gap between reductions, in ms. A 165 Hz display hands us 165
    /// frames a second, but the strip is driven at 60 - reducing every one of them means
    /// a full-frame copy and mip chain we throw away. That work competes with the game
    /// for the very GPU whose compositor we depend on, so doing less of it directly
    /// reduces the starvation it causes.
    /// </summary>
    public double MinReduceIntervalMs { get; set; }

    long _lastReduceTicks;

    protected bool ReduceDue()
    {
        if (MinReduceIntervalMs <= 0) return true;

        long now = DateTime.UtcNow.Ticks;
        double since = (now - _lastReduceTicks) / (double)TimeSpan.TicksPerMillisecond;
        if (since < MinReduceIntervalMs) return false;

        _lastReduceTicks = now;
        return true;
    }

    // Latest reduced frame, published by the capture thread and polled by consumers.
    // A version counter avoids handing out the same frame twice.
    readonly object _imageLock = new();
    byte[] _image = Array.Empty<byte>();
    int _imgW, _imgH, _imgStride;
    long _imageVersion;
    FrameStamps _imageStamps;

    /// <summary>
    /// Raised on every published frame, so a consumer can wake on the frame itself instead
    /// of polling. Polling was costing real latency: a consumer asking every few
    /// milliseconds actually asks every 15.6 ms, because that is what Thread.Sleep and
    /// wait timeouts round up to.
    ///
    /// Only a hint - the version check in <see cref="TryGetImage"/> stays authoritative,
    /// so a missed or spurious signal costs at most one extra pass round the caller's loop.
    /// </summary>
    readonly AutoResetEvent _frameSignal = new(false);

    /// <summary>
    /// Typed as the event rather than a bare WaitHandle so a consumer can clear it before
    /// polling the image - see the output loop, which does exactly that.
    /// </summary>
    public EventWaitHandle FrameSignal => _frameSignal;

    /// <param name="stamps">
    /// Where this frame has been, on the Stopwatch clock. Passed in rather than taken
    /// here so it survives the relay through the hybrid: what matters is the age of the
    /// picture, not of the copy.
    /// </param>
    protected void PublishImage(byte[] src, int width, int height, int stride, FrameStamps stamps = default)
    {
        lock (_imageLock)
        {
            if (_image.Length != src.Length) _image = new byte[src.Length];
            Array.Copy(src, _image, src.Length);
            _imgW = width; _imgH = height; _imgStride = stride;
            _imageStamps = stamps.IsEmpty ? FrameStamps.Now() : stamps;
            _imageVersion++;
        }
        _frameSignal.Set();
    }

    /// <summary>Copies the newest frame out if it is newer than <paramref name="version"/>.</summary>
    public bool TryGetImage(ref byte[] dest, ref long version, out int width, out int height, out int stride) =>
        TryGetImage(ref dest, ref version, out width, out height, out stride, out _);

    /// <param name="stamps">Where the picture has been on its way here.</param>
    public bool TryGetImage(ref byte[] dest, ref long version, out int width, out int height, out int stride,
                            out FrameStamps stamps)
    {
        lock (_imageLock)
        {
            width = _imgW; height = _imgH; stride = _imgStride; stamps = _imageStamps;
            if (_imageVersion == version || _image.Length == 0) return false;
            if (dest.Length != _image.Length) dest = new byte[_image.Length];
            Array.Copy(_image, dest, _image.Length);
            version = _imageVersion;
            return true;
        }
    }

    volatile bool _snapshotRequested;
    public void RequestSnapshot() => _snapshotRequested = true;

    /// <summary>True once, then clears - backends poll this on their capture thread.</summary>
    protected bool TakeSnapshotRequest()
    {
        if (!_snapshotRequested) return false;
        _snapshotRequested = false;
        return true;
    }

    public void Start(MonitorInfo monitor)
    {
        if (_running) return;
        Monitor = monitor;
        _running = true;
        Metrics.Reset(BackendStatus.Starting, "запуск");
        ProbeLog.Log(Name, $"старт, монитор {monitor.DeviceName} {monitor.Width}x{monitor.Height}");

        _thread = new Thread(ThreadBody)
        {
            IsBackground = true,
            Name = $"probe-{Name}",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    void ThreadBody()
    {
        try
        {
            RunLoop();
        }
        catch (Exception ex)
        {
            Metrics.NoteError(Short(ex));
            ProbeLog.Log(Name, "поток упал: " + ex);
        }
        finally
        {
            _running = false;
            Metrics.NoteStatus(BackendStatus.Stopped, "остановлен");
        }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _thread?.Join(3000);
        _thread = null;
        Metrics.NoteStatus(BackendStatus.Stopped, "остановлен");
        ProbeLog.Log(Name, "стоп; " + SummaryLine());
    }

    public virtual string SummaryLine()
    {
        var s = Metrics.Snapshot();
        return $"кадров={s.Frames} таймаутов={s.Timeouts} ошибок={s.Errors} чёрных={s.BlackFrames} тёмных={s.DarkSpikes} " +
               $"пропущено={s.Skipped} " +
               $"fps5s={s.FpsAvg5s:F1} p50={s.P50Ms:F1}мс p99={s.P99Ms:F1}мс " +
               $"получение={s.AcquireMs:F2}мс свод={s.ReduceMs:F2}мс";
    }

    protected static string Short(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    protected abstract void RunLoop();

    public virtual void Dispose()
    {
        Stop();
        _frameSignal.Dispose();
    }
}

/// <summary>Averages a BGRA buffer and reports whether the frame is entirely black.</summary>
public static class ColorMath
{
    public const int BlackThreshold = 3;   // 0..255 per channel

    public static (byte r, byte g, byte b, bool isBlack) AverageBgra(
        ReadOnlySpan<byte> data, int width, int height, int rowPitch)
    {
        ulong sr = 0, sg = 0, sb = 0;
        int max = 0;
        int count = 0;

        for (int y = 0; y < height; y++)
        {
            int row = y * rowPitch;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * 4;
                byte b = data[i];
                byte g = data[i + 1];
                byte r = data[i + 2];
                sb += b; sg += g; sr += r;
                if (b > max) max = b;
                if (g > max) max = g;
                if (r > max) max = r;
                count++;
            }
        }

        if (count == 0) return (0, 0, 0, true);
        return ((byte)(sr / (ulong)count), (byte)(sg / (ulong)count), (byte)(sb / (ulong)count),
                max <= BlackThreshold);
    }
}
