using System;
using System.Threading;

using Ambilight.Text;

namespace Ambilight.Capture.Backends;

/// <summary>
/// The architecture the real app will use: several capture paths at once, always taking
/// whichever has the freshest frame.
///
/// DDA and WGC starve at different moments - they read the composition path through
/// different machinery - so running both covers more gaps than either alone, and both are
/// nearly free (~0.04 ms per frame of our own work). GDI is the last resort: it never
/// starves, because it never touches that path, but a StretchBlt costs ~60 ms at
/// 3440x1440, so it is started only when the cheap paths have both gone quiet.
///
/// This is what keeps the light alive through Fellowship's starvation cycle instead of
/// freezing and then going dark the way Prismatik does.
/// </summary>
public sealed class HybridBackend : CaptureBackendBase
{
    public override string Name => "Гибрид";

    public const string SetupNote = "DDA + WGC, GDI при голодании";

    /// <summary>
    /// A source counts as alive if it delivered anything within this window.
    ///
    /// Judging it on a 250 ms gap was wrong: a screen whose only movement is a text caret
    /// blinking legitimately produces about two frames a second, and every pause between
    /// blinks looked like starvation. That triggered a probe, the probe caught the next
    /// blink and concluded the screen was moving while the path was silent, and the ladder
    /// dropped to GDI - on an idle desktop. Delivering slowly is not the same as not
    /// delivering, because a still picture needs no frames.
    /// </summary>
    const int AliveMs = 2000;

    /// <summary>Kept for the polling-activity checks, which really are per-tick.</summary>
    const int StarveMs = 250;

    /// <summary>A cheap source must hold up this long before GDI is shut down again.</summary>
    const int RecoverMs = 2000;

    /// <summary>
    /// How long a responsive-but-silent source is given before we suspect it is starved
    /// rather than simply looking at a screen that is not changing.
    /// </summary>
    const int IdleGraceMs = 1500;

    /// <summary>How long GDI is given to answer whether anything on screen is moving.</summary>
    const int ProbeMs = 700;

    const int PollHz = 240;

    enum Source { None, Dda, Wgc, Gdi }

    readonly DdaBackend _dda = new();
    readonly WgcBackend _wgc = new(WgcTarget.Monitor);
    readonly GdiBackend _gdi = new();

    byte[] _relay = Array.Empty<byte>();
    long _relayDda, _relayWgc, _relayGdi;

    Source _active = Source.None;
    long _ddaMs, _wgcMs, _gdiMs, _deadMs;
    long _lastDarkSpikes;
    long _lastSwitchTicks;

    public long Switches { get; private set; }

    /// <summary>Which source is carrying the picture right now, for diagnostics.</summary>
    public string ActiveSource => Describe(_active);

    /// <summary>
    /// Restricts which paths run. Every extra capture client raises the chance Windows
    /// demotes the mouse cursor off its hardware plane, which reads as cursor flicker, so
    /// being able to run exactly one is a real workaround and not just a debug knob.
    /// </summary>
    public bool UseDda { get; set; } = true;
    public bool UseWgc { get; set; } = true;
    public bool UseGdi { get; set; } = true;

    protected override void RunLoop()
    {
        _dda.ReduceWidth = ReduceWidth;
        _wgc.ReduceWidth = ReduceWidth;
        _gdi.ReduceWidth = ReduceWidth;

        _dda.MinReduceIntervalMs = MinReduceIntervalMs;
        _wgc.MinReduceIntervalMs = MinReduceIntervalMs;
        _gdi.MinReduceIntervalMs = MinReduceIntervalMs;

        if (UseDda) _dda.Start(Monitor);

        // WGC only comes up when DDA is not delivering. Measured at 0-1% of the time as a
        // gap filler, and every extra capture client costs GPU work that competes with the
        // compositor we are reading from - so idling one is worse than useless.
        bool wgcLazy = UseDda && UseWgc;
        if (UseWgc && !wgcLazy) _wgc.Start(Monitor);

        // with every cheap path disabled, GDI is the only thing left and must stay up
        bool gdiAlwaysOn = !UseDda && !UseWgc;
        if (gdiAlwaysOn) _gdi.Start(Monitor);

        long lastDdaFrames = -1, lastWgcFrames = -1, lastGdiFrames = -1;
        long lastDdaTicks = 0, lastWgcTicks = 0;

        // "no new frames" from DDA means the desktop image did not change, which is the
        // normal state whenever nothing on screen moves. Treating that as starvation made
        // the ladder flap to GDI the moment the cursor left this monitor. Track polling
        // activity separately from delivered frames to tell the two apart.
        long lastDdaTimeouts = -1, lastWgcTimeouts = -1;
        long lastDdaPoll = 0, lastWgcPoll = 0;
        long lastGdiChange = 0;
        byte gr = 0, gg = 0, gb = 0;
        long lastWgcUseful = 0;
        bool ddaProducingPrev = false;
        long cheapHealthySince = 0;
        long probeStart = 0;
        ulong probeHash = 0;
        bool staticConfirmed = false;
        long prevTick = Environment.TickCount64;

        // with a single method enabled there is nothing to fall back to or from
        bool ladder = (UseDda ? 1 : 0) + (UseWgc ? 1 : 0) + (UseGdi ? 1 : 0) > 1;

        double periodMs = 1000.0 / PollHz;

        try
        {
            while (ShouldRun)
            {
                long now = Environment.TickCount64;
                long dt = now - prevTick;
                prevTick = now;

                // pushed every pass: the cap can be changed while running, and the children
                // would otherwise keep the value they were handed at startup
                _dda.MinReduceIntervalMs = MinReduceIntervalMs;
                _wgc.MinReduceIntervalMs = MinReduceIntervalMs;
                _gdi.MinReduceIntervalMs = MinReduceIntervalMs;

                var ds = _dda.Metrics.Snapshot();
                var ws = _wgc.Metrics.Snapshot();

                // Frames == 0 means the backend was just (re)started and its metrics still
                // hold the reset state, including colour 0,0,0. Forwarding that is what
                // produced a black flash 15 ms after every switch to GDI.
                bool ddaNew = ds.Frames > 0 && ds.Frames != lastDdaFrames;
                if (ddaNew) { lastDdaFrames = ds.Frames; lastDdaTicks = now; lastDdaPoll = now; }
                if (ds.Timeouts != lastDdaTimeouts) { lastDdaTimeouts = ds.Timeouts; lastDdaPoll = now; }

                // bring WGC up only once DDA has actually gone quiet
                // on the same window: starting and stopping WGC between caret blinks was
                // churning a capture client once a second, which the cursor showed
                if (wgcLazy && !_wgc.IsRunning && lastDdaTicks != 0 && now - lastDdaTicks > AliveMs)
                    _wgc.Start(Monitor);
                else if (wgcLazy && _wgc.IsRunning && ddaProducingPrev && now - lastDdaTicks <= AliveMs
                         && now - lastWgcUseful > RecoverMs)
                    _wgc.Stop();

                bool wgcNew = ws.Frames > 0 && ws.Frames != lastWgcFrames;
                if (wgcNew) { lastWgcFrames = ws.Frames; lastWgcTicks = now; lastWgcPoll = now; }
                if (ws.Timeouts != lastWgcTimeouts) { lastWgcTimeouts = ws.Timeouts; lastWgcPoll = now; }

                bool ddaProducing = UseDda && lastDdaTicks != 0 && now - lastDdaTicks <= AliveMs;
                bool wgcProducing = UseWgc && lastWgcTicks != 0 && now - lastWgcTicks <= AliveMs;

                // responsive but silent: the path is fine, the picture just is not moving
                bool ddaIdle = UseDda && !ddaProducing && lastDdaPoll != 0 && now - lastDdaPoll <= StarveMs;
                bool wgcIdle = UseWgc && !wgcProducing && lastWgcPoll != 0 && now - lastWgcPoll <= StarveMs;

                // Is anything on screen actually changing? Only GDI can answer while the
                // cheap paths are silent, since it reads a different route entirely.
                if (_gdi.IsRunning)
                {
                    var probe = _gdi.Metrics.Snapshot();
                    if (probe.R != gr || probe.G != gg || probe.B != gb)
                    {
                        gr = probe.R; gg = probe.G; gb = probe.B;
                        lastGdiChange = now;
                    }
                }
                bool screenMoving = now - lastGdiChange <= 1000 && lastGdiChange != 0;

                if (_active == Source.Wgc) lastWgcUseful = now;
                ddaProducingPrev = ddaProducing;

                // a delivered frame means the picture moved, so any earlier verdict is stale
                if (ddaProducing || wgcProducing) { staticConfirmed = false; probeStart = 0; }

                Source want;
                if (!ladder)
                {
                    // Exactly one method is enabled, so there is no ladder to climb. Without
                    // this the idle branches below fall through to GDI and the status flaps
                    // to a source that is not even running.
                    want = UseDda ? Source.Dda : UseWgc ? Source.Wgc : Source.Gdi;
                }
                else if (ddaProducing) want = Source.Dda;
                else if (wgcProducing) want = Source.Wgc;
                else if (ddaIdle || wgcIdle)
                {
                    Source idleSrc = ddaIdle ? Source.Dda : Source.Wgc;

                    // AliveMs already granted the grace period, so anything reaching here
                    // has produced nothing at all for two seconds
                    if (staticConfirmed || !UseGdi)
                    {
                        want = idleSrc;
                    }
                    else
                    {
                        // Two cheap blits a few hundred ms apart, rather than starting the
                        // whole GDI backend: that used to churn a capture client every time
                        // the desktop went quiet, which visibly disturbed the mouse cursor.
                        if (probeStart == 0)
                        {
                            probeHash = GdiProbe.Sample(Monitor);
                            probeStart = now;
                        }

                        if (now - probeStart > ProbeMs)
                        {
                            ulong second = GdiProbe.Sample(Monitor);
                            bool moving = probeHash != 0 && second != 0 && second != probeHash;
                            probeStart = 0;

                            if (moving)
                            {
                                want = Source.Gdi;      // the cheap path really is starved
                                ProbeLog.Log(Name, "экран меняется, а быстрый путь молчит — GDI");
                            }
                            else
                            {
                                staticConfirmed = true; // screen is simply still; stay put
                                want = idleSrc;
                                ProbeLog.Log(Name, "экран статичен, остаюсь на " + Describe(idleSrc));
                            }
                        }
                        else want = idleSrc;            // hold still while probing
                    }
                }
                else want = Source.Gdi;

                if (want == Source.Gdi)
                {
                    cheapHealthySince = 0;
                    if (!_gdi.IsRunning && UseGdi)
                    {
                        lastGdiFrames = -1;   // its counter restarts from zero
                        _gdi.Start(Monitor);
                    }
                }
                else if (gdiAlwaysOn)
                {
                    // nothing to fall back from
                }
                else
                {
                    if (cheapHealthySince == 0) cheapHealthySince = now;

                    // do not tear the fallback down the instant a cheap path blinks back,
                    // or it would flap on every cycle of the starvation loop
                    if (_gdi.IsRunning && now - cheapHealthySince > RecoverMs)
                    {
                        _gdi.Stop();
                        ProbeLog.Log(Name, "дешёвый путь стабилен, GDI выключен");
                    }
                }

                if (want != _active)
                {
                    Switches++;
                    _lastSwitchTicks = now;
                    ProbeLog.Log(Name, $"источник: {Describe(_active)} -> {Describe(want)} (переключений {Switches})");
                    _active = want;
                }

                // Correlate dark flashes with source changes: if they cluster around a
                // switch, they are the composed desktop without the game on it, not a
                // genuine cut to black in the picture.
                long spikes = Metrics.Snapshot().DarkSpikes;
                if (spikes != _lastDarkSpikes)
                {
                    long sinceSwitch = now - _lastSwitchTicks;
                    ProbeLog.Log(Name, $"тёмная вспышка (всего {spikes}); источник {Describe(_active)}, " +
                                       $"{sinceSwitch} мс от последнего переключения");
                    _lastDarkSpikes = spikes;
                }

                switch (_active)
                {
                    case Source.Dda: _ddaMs += dt; break;
                    case Source.Wgc: _wgcMs += dt; break;
                    case Source.Gdi: _gdiMs += dt; break;
                    default: _deadMs += dt; break;
                }

                switch (_active)
                {
                    case Source.Dda when ddaNew:
                        Relay(_dda, ref _relayDda);
                        Metrics.NoteFrame(ds.R, ds.G, ds.B, false, ds.AcquireMs, ds.ReduceMs);
                        Metrics.NoteStatus(BackendStatus.Ok, "DDA");
                        break;

                    case Source.Wgc when wgcNew:
                        Relay(_wgc, ref _relayWgc);
                        Metrics.NoteFrame(ws.R, ws.G, ws.B, false, ws.AcquireMs, ws.ReduceMs);
                        Metrics.NoteStatus(BackendStatus.Ok, "WGC");
                        break;

                    case Source.Gdi when _gdi.IsRunning:
                        var gs = _gdi.Metrics.Snapshot();
                        if (gs.Frames > 0 && gs.Frames != lastGdiFrames)
                        {
                            lastGdiFrames = gs.Frames;
                            Relay(_gdi, ref _relayGdi);
                            Metrics.NoteFrame(gs.R, gs.G, gs.B, false, gs.AcquireMs, gs.ReduceMs);
                            Metrics.NoteStatus(BackendStatus.Ok, "GDI (запасной)");
                        }
                        break;
                }

                Thread.Sleep((int)Math.Max(1, periodMs));
            }
        }
        finally
        {
            _dda.Stop();
            _wgc.Stop();
            _gdi.Stop();
            ProbeLog.Log(Name, "итог: " + SourceSplit());
        }
    }

    /// <summary>Republishes the active child's frame as our own.</summary>
    void Relay(CaptureBackendBase child, ref long version)
    {
        if (child.TryGetImage(ref _relay, ref version, out int w, out int h, out int stride))
            PublishImage(_relay, w, h, stride);
    }

    static string Describe(Source s) => s switch
    {
        Source.Dda => "DDA",
        Source.Wgc => "WGC",
        Source.Gdi => "GDI",
        _ => Loc.P("нет", "none")
    };

    public string SourceSplit()
    {
        double total = Math.Max(1, _ddaMs + _wgcMs + _gdiMs + _deadMs);
        return $"DDA {_ddaMs * 100 / total:F0}% " +
               $"WGC {_wgcMs * 100 / total:F0}% " +
               $"GDI {_gdiMs * 100 / total:F0}% " +
               Loc.P("без источника ", "no source ") + $"{_deadMs * 100 / total:F0}%; " +
               Loc.P("переключений ", "switches ") + Switches;
    }

    public override string SummaryLine() => base.SummaryLine() + " | источники: " + SourceSplit();

    public override void Dispose()
    {
        base.Dispose();
        _dda.Dispose();
        _wgc.Dispose();
        _gdi.Dispose();
    }
}
