using System;

namespace Rimlight.Capture;

public enum BackendStatus
{
    Stopped,
    Starting,
    Ok,
    Timeout,
    Error,
    Black
}

public struct BackendSnapshot
{
    public BackendStatus Status;
    public string StatusText;
    public byte R, G, B;
    public double FpsInstant;
    public double FpsAvg5s;
    public double P50Ms, P99Ms;
    public double AcquireMs, ReduceMs;
    public long Frames, Timeouts, Errors, BlackFrames;
    public long Skipped;
    public long DarkSpikes;
}

/// <summary>
/// Thread-safe metric accumulator. Capture threads call NoteFrame/NoteTimeout/NoteError.
/// The UI thread calls Tick (10 Hz) and Snapshot. Nothing on the capture-thread path
/// allocates, so the probe does not distort what it measures.
/// </summary>
public sealed class BackendMetrics
{
    public const int HistorySeconds = 180;   // ~3 min second-by-second strip
    const int IntervalWindow = 512;          // frame intervals kept for percentiles
    const int RateBuckets = 50;              // 50 x 100 ms = 5 s window

    readonly object _lock = new();

    readonly double[] _intervals = new double[IntervalWindow];
    int _intervalIdx, _intervalCount;
    readonly double[] _sortScratch = new double[IntervalWindow];

    readonly int[] _rate = new int[RateBuckets];
    int _rateIdx;
    long _lastRateTick;

    readonly BackendStatus[] _history = new BackendStatus[HistorySeconds];
    readonly BackendStatus[] _historyCopy = new BackendStatus[HistorySeconds];
    int _historyIdx;
    long _lastHistorySecond;

    // worst status seen inside the currently open one-second bucket
    BackendStatus _bucketWorst = BackendStatus.Stopped;

    BackendStatus _status = BackendStatus.Stopped;
    string _statusText = "остановлен";
    byte _r, _g, _b;
    double _acquireMs, _reduceMs;
    long _frames, _timeouts, _errors, _blackFrames, _skipped, _darkSpikes;
    double _lumaEma;
    long _lastFrameTicks;

    public BackendMetrics()
    {
        for (int i = 0; i < HistorySeconds; i++) _history[i] = BackendStatus.Stopped;
        _lastHistorySecond = NowSeconds();
        _lastRateTick = DateTime.UtcNow.Ticks;
    }

    static long NowSeconds() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;

    // Higher severity wins when several things happen inside one second bucket.
    static int Severity(BackendStatus s) => s switch
    {
        BackendStatus.Error => 5,
        BackendStatus.Black => 4,
        BackendStatus.Timeout => 3,
        BackendStatus.Ok => 2,
        BackendStatus.Starting => 1,
        _ => 0
    };

    void RaiseBucket(BackendStatus s)
    {
        if (Severity(s) > Severity(_bucketWorst)) _bucketWorst = s;
    }

    public void Reset(BackendStatus status, string text)
    {
        lock (_lock)
        {
            _intervalIdx = _intervalCount = 0;
            Array.Clear(_rate);
            _rateIdx = 0;
            _frames = _timeouts = _errors = _blackFrames = _skipped = _darkSpikes = 0;
            _lumaEma = 0;
            _acquireMs = _reduceMs = 0;
            _lastFrameTicks = 0;
            _status = status;
            _statusText = text;
            _bucketWorst = status;
            _r = _g = _b = 0;
        }
    }

    public void NoteFrame(byte r, byte g, byte b, bool isBlack, double acquireMs, double reduceMs)
    {
        long now = DateTime.UtcNow.Ticks;
        lock (_lock)
        {
            if (_lastFrameTicks != 0)
            {
                double ms = (now - _lastFrameTicks) / (double)TimeSpan.TicksPerMillisecond;
                _intervals[_intervalIdx] = ms;
                _intervalIdx = (_intervalIdx + 1) % IntervalWindow;
                if (_intervalCount < IntervalWindow) _intervalCount++;
            }
            _lastFrameTicks = now;

            _frames++;
            _rate[_rateIdx]++;
            _r = r; _g = g; _b = b;

            // A frame far darker than the recent norm is almost certainly the composed
            // desktop without the game on it, not a real cut to black. Counted separately
            // because pure-black detection misses "very dark" and those still make the
            // strip blink.
            double luma = 0.299 * r + 0.587 * g + 0.114 * b;
            if (_lumaEma > 30 && luma < _lumaEma * 0.25) _darkSpikes++;
            _lumaEma = _lumaEma == 0 ? luma : _lumaEma + (luma - _lumaEma) * 0.05;

            // light smoothing keeps the per-frame cost readout steady enough to read
            _acquireMs = _acquireMs == 0 ? acquireMs : _acquireMs + (acquireMs - _acquireMs) * 0.05;
            _reduceMs = _reduceMs == 0 ? reduceMs : _reduceMs + (reduceMs - _reduceMs) * 0.05;

            if (isBlack)
            {
                _blackFrames++;
                _status = BackendStatus.Black;
                _statusText = "ЧЁРНЫЙ КАДР";
            }
            else
            {
                _status = BackendStatus.Ok;
                _statusText = "OK";
            }
            RaiseBucket(_status);
        }
    }

    /// <summary>Total frames whose GPU readback was not ready yet - non-blocking skips.</summary>
    public void NoteSkipped(long total)
    {
        lock (_lock) { _skipped = total; }
    }


    public void NoteTimeout()
    {
        lock (_lock)
        {
            _timeouts++;
            _status = BackendStatus.Timeout;
            _statusText = "нет новых кадров";
            RaiseBucket(BackendStatus.Timeout);
        }
    }

    public void NoteError(string text)
    {
        lock (_lock)
        {
            _errors++;
            _status = BackendStatus.Error;
            _statusText = text;
            RaiseBucket(BackendStatus.Error);
        }
    }

    public void NoteStatus(BackendStatus status, string text)
    {
        lock (_lock)
        {
            _status = status;
            _statusText = text;
            RaiseBucket(status);
        }
    }

    /// <summary>Called from the UI timer; rolls the per-second and per-100 ms buckets.</summary>
    public void Tick()
    {
        long nowTicks = DateTime.UtcNow.Ticks;
        lock (_lock)
        {
            while ((nowTicks - _lastRateTick) >= TimeSpan.TicksPerMillisecond * 100)
            {
                _lastRateTick += TimeSpan.TicksPerMillisecond * 100;
                _rateIdx = (_rateIdx + 1) % RateBuckets;
                _rate[_rateIdx] = 0;
            }

            long sec = NowSeconds();
            while (_lastHistorySecond < sec)
            {
                _history[_historyIdx] = _bucketWorst;
                _historyIdx = (_historyIdx + 1) % HistorySeconds;
                _lastHistorySecond++;
                // the next second starts from whatever the steady state currently is
                _bucketWorst = _status;
            }
        }
    }

    public BackendSnapshot Snapshot()
    {
        lock (_lock)
        {
            int n = _intervalCount;
            double p50 = 0, p99 = 0;
            if (n > 0)
            {
                Array.Copy(_intervals, _sortScratch, n);
                Array.Sort(_sortScratch, 0, n);
                p50 = _sortScratch[Math.Min(n - 1, (int)(n * 0.50))];
                p99 = _sortScratch[Math.Min(n - 1, (int)(n * 0.99))];
            }

            int last1s = 0;
            for (int i = 0; i < 10; i++)
                last1s += _rate[((_rateIdx - i) % RateBuckets + RateBuckets) % RateBuckets];

            int last5s = 0;
            for (int i = 0; i < RateBuckets; i++) last5s += _rate[i];

            return new BackendSnapshot
            {
                Status = _status,
                StatusText = _statusText,
                R = _r, G = _g, B = _b,
                FpsInstant = last1s,
                FpsAvg5s = last5s / 5.0,
                P50Ms = p50,
                P99Ms = p99,
                AcquireMs = _acquireMs,
                ReduceMs = _reduceMs,
                Frames = _frames,
                Timeouts = _timeouts,
                Errors = _errors,
                BlackFrames = _blackFrames,
                Skipped = _skipped,
                DarkSpikes = _darkSpikes
            };
        }
    }

    /// <summary>Oldest-to-newest copy of the per-second strip.</summary>
    public BackendStatus[] HistorySnapshot()
    {
        lock (_lock)
        {
            for (int i = 0; i < HistorySeconds; i++)
                _historyCopy[i] = _history[(_historyIdx + i) % HistorySeconds];
            return _historyCopy;
        }
    }

    /// <summary>Newest-last copy of recent frame intervals, for the jitter sparkline.</summary>
    public int CopyIntervals(double[] dest)
    {
        lock (_lock)
        {
            int n = Math.Min(dest.Length, _intervalCount);
            for (int i = 0; i < n; i++)
            {
                int src = ((_intervalIdx - n + i) % IntervalWindow + IntervalWindow) % IntervalWindow;
                dest[i] = _intervals[src];
            }
            return n;
        }
    }
}
