using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Ambilight.Capture;
using Ambilight.Capture.Backends;

namespace CaptureProbe;

public partial class ProbeWindow : Window
{
    readonly List<BackendPanel> _panels = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer _summaryTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    List<MonitorInfo> _monitors = new();
    string _lastForeground = "";

    public ProbeWindow()
    {
        InitializeComponent();

        _monitors = Native.EnumerateMonitors();
        foreach (var m in _monitors) MonitorBox.Items.Add(m.ToString());

        int primary = _monitors.FindIndex(m => m.IsPrimary);
        MonitorBox.SelectedIndex = primary >= 0 ? primary : 0;

        LogPath.Text = "Лог: " + ProbeLog.FilePath;
        ProbeLog.Log("probe", $"мониторов найдено: {_monitors.Count}");
        foreach (var m in _monitors)
            ProbeLog.Log("probe", $"  {m.DeviceName} {m.Width}x{m.Height} @ {m.Left},{m.Top}{(m.IsPrimary ? " основной" : "")}");

        AddPanel(new HybridBackend(), HybridBackend.SetupNote);
        AddPanel(new WgcBackend(WgcTarget.ForegroundWindow), "следует за активным окном");
        AddPanel(new WgcBackend(WgcTarget.Monitor));
        AddPanel(new DdaBackend());
        AddPanel(new GdiBackend(), GdiBackend.PacingNote);
        AddPanel(new SpoutBackend(), SpoutBackend.SetupNote);

        StartAll.Click += (_, _) => { var m = SelectedMonitor(); if (m != null) foreach (var p in _panels) p.StartWith(m); };
        StopAll.Click += (_, _) => { foreach (var p in _panels) p.StopBackend(); };

        // decisive check: an averaged colour cannot tell the game from the desktop behind it
        SnapAll.Click += (_, _) =>
        {
            foreach (var p in _panels) p.Backend.RequestSnapshot();
            ProbeLog.Log("probe", "запрошены снимки со всех бэкендов -> " + Snapshot.Directory);
        };

        MonitorBox.SelectionChanged += (_, _) =>
        {
            // a backend is bound to the monitor it started on; restart those that were running
            var running = _panels.Where(p => p.Backend.IsRunning).ToList();
            if (running.Count == 0) return;
            var m = SelectedMonitor();
            if (m == null) return;
            foreach (var p in running) { p.StopBackend(); p.StartWith(m); }
        };

        _timer.Tick += (_, _) =>
        {
            foreach (var p in _panels) p.Refresh();
            TrackForegroundWindow();
        };
        _timer.Start();

        // periodic snapshots, so a long session still leaves data points behind
        // even if the app is killed rather than closed
        _summaryTimer.Tick += (_, _) =>
        {
            // without this the summaries cannot be attributed to a game after the fact
            ProbeLog.Log("probe", "активное окно: " + DescribeForeground());
            foreach (var p in _panels)
                if (p.Backend.IsRunning)
                    ProbeLog.Log(p.Backend.Name, "сводка: " + ((CaptureBackendBase)p.Backend).SummaryLine());
        };
        _summaryTimer.Start();

        // liveness is the common case, so all three come up running; stop the other
        // two when taking honest performance numbers for one
        // The hybrid owns its own WGC and GDI instances, so starting every panel at once
        // would run two of each and skew the numbers. Start only the hybrid; the rest are
        // one click away.
        Loaded += (_, _) =>
        {
            var m = SelectedMonitor();
            if (m == null) return;

            // AMBILIGHT_PROBE_STARTALL=1 starts every panel, for measuring one backend
            // in isolation without clicking through the UI
            if (Environment.GetEnvironmentVariable("AMBILIGHT_PROBE_STARTALL") == "1")
                foreach (var p in _panels) p.StartWith(m);
            else
                _panels[0].StartWith(m);
        };

        Closing += (_, _) =>
        {
            foreach (var p in _panels)
            {
                ProbeLog.Log(p.Backend.Name, "итог: " + ((CaptureBackendBase)p.Backend).SummaryLine());
                p.StopBackend();
            }
            ProbeLog.Log("probe", "сессия завершена");
        };
    }

    static string DescribeForeground()
    {
        IntPtr hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "(нет)";

        string title = Native.GetWindowTitle(hwnd);
        string process = "?";
        try
        {
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch { /* the process may already be gone */ }

        Native.GetWindowRect(hwnd, out var r);
        return $"\"{title}\" [{process}.exe] {r.Right - r.Left}x{r.Bottom - r.Top}";
    }

    /// <summary>Logs foreground changes as they happen, so slow spells can be pinned to a game.</summary>
    void TrackForegroundWindow()
    {
        string current = DescribeForeground();
        if (current == _lastForeground) return;
        _lastForeground = current;
        ProbeLog.Log("probe", "переключение на " + current);
    }

    void AddPanel(ICaptureBackend backend, string? note = null)
    {
        var panel = new BackendPanel(backend, SelectedMonitor, note);
        _panels.Add(panel);
        PanelHost.Children.Add(panel);
    }

    MonitorInfo? SelectedMonitor()
    {
        int i = MonitorBox.SelectedIndex;
        return i >= 0 && i < _monitors.Count ? _monitors[i] : null;
    }
}
