using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Rimlight;
using Rimlight.Capture;
using Rimlight.Leds;

namespace LatencyProbe;

/// <summary>
/// The control window. Belongs on a second screen: the grid takes over the one with the
/// strip on it, and nothing here should end up in the shot.
/// </summary>
public partial class BenchWindow : Window
{
    readonly RimlightConfig _cfg = RimlightConfig.Load();
    readonly DispatcherTimer _readout = new() { Interval = TimeSpan.FromMilliseconds(16) };

    List<MonitorInfo> _monitors = new();
    GridWindow? _grid;
    BenchRun? _run;

    public BenchWindow()
    {
        InitializeComponent();

        // Its own log file, next to the executable: mixing these lines into the capture
        // probe's probe.log would make both harder to read after a session.
        ProbeLog.Configure(Path.Combine(AppContext.BaseDirectory, "latency.log"), true);
        LogPathText.Text = "Журнал: " + ProbeLog.FilePath;

        _monitors = Native.EnumerateMonitors();
        foreach (var m in _monitors) MonitorBox.Items.Add(m.ToString());

        // The screen the strip is actually mounted on is the one named in the settings, so
        // that is where the grid should come up without being told.
        var chosen = ScreenChoice.Find(_monitors, _cfg.MonitorDeviceName, _cfg.MonitorModel);
        MonitorBox.SelectedIndex = chosen == null ? 0 : _monitors.IndexOf(chosen);

        GridButton.Click += (_, _) => ToggleGrid();
        StartButton.Click += (_, _) => Start();
        StopButton.Click += (_, _) => Stop("остановлено вручную");

        MonitorBox.SelectionChanged += (_, _) =>
        {
            // zones are built for one screen's proportions; moving screens rebuilds them
            DescribeConfig();
            if (_grid == null) return;
            Stop("смена монитора");
            CloseGrid();
            ShowGrid();
        };

        _readout.Tick += (_, _) => RefreshReadout();
        _readout.Start();

        Closed += (_, _) => CloseGrid();
        DescribeConfig();
    }

    MonitorInfo? SelectedMonitor() =>
        MonitorBox.SelectedIndex >= 0 && MonitorBox.SelectedIndex < _monitors.Count
            ? _monitors[MonitorBox.SelectedIndex]
            : null;

    LedZone[] BuildZones(MonitorInfo m) => LedLayout.Build(_cfg, m.Width, m.Height);

    void DescribeConfig()
    {
        var m = SelectedMonitor();
        string size = m == null ? "?" : $"{m.Width}x{m.Height}";

        ConfigText.Text =
            $"Настройки: {RimlightConfig.Path}\n" +
            $"Сетка {size}: {_cfg.TotalLeds} диодов — верх {_cfg.TopCount}, низ {_cfg.BottomCount}, " +
            $"лево {_cfg.LeftCount}, право {_cfg.RightCount}; " +
            $"поля {_cfg.EdgeMarginPercent:0.#}% / {_cfg.EdgeMarginPercentV:0.#}%, " +
            $"глубина {_cfg.DepthPercent:0.#}%, старт {_cfg.StartCorner}, смещение {_cfg.IndexOffset}";

        // Everything below is Rimlight's own doing and lands inside the measured interval.
        // Better said here than discovered afterwards in a video that came out slower than
        // the pipeline really is.
        var warn = new List<string>();
        if (_cfg.SmoothingRise < 1.0 || _cfg.SmoothingFall < 1.0)
            warn.Add($"сглаживание подъём {_cfg.SmoothingRise:0.##} / спад {_cfg.SmoothingFall:0.##} — " +
                     "входит в замер; для чистой задержки конвейера поставьте 1.0 / 1.0");
        if (_cfg.MaxFps > 0)
            warn.Add($"предел {_cfg.MaxFps} к/с — это ещё до {1000.0 / _cfg.MaxFps:0.#} мс ожидания кадра");
        if (_cfg.SendOnlyOnChange)
            warn.Add("«отправлять только при изменении» включено — на чёрном шаге лента замолкает");

        WarnText.Text = warn.Count == 0 ? "" : "Учтите: " + string.Join("; ", warn) + ".";
    }

    void ToggleGrid()
    {
        if (_grid != null) { CloseGrid(); return; }
        ShowGrid();
    }

    void ShowGrid()
    {
        var monitor = SelectedMonitor();
        if (monitor == null) return;

        _grid = new GridWindow(monitor);
        _grid.StepShown += OnStepShown;
        _grid.Closed += (_, _) =>
        {
            // The colour sequence rides on the grid's render loop, so without the grid
            // there is no run - and no measurement either.
            _grid = null;
            GridButton.Content = "Вывести сетку";
            if (_run != null) Stop("сетка закрыта");
        };

        _grid.Show();
        _grid.SetZones(BuildZones(monitor));
        GridButton.Content = "Убрать сетку";

        ProbeLog.Log("сетка", $"выведена на {monitor.DeviceName} {monitor.Width}x{monitor.Height}, " +
                              $"{_cfg.TotalLeds} зон");
    }

    void CloseGrid() => _grid?.Close();

    double IntervalMs()
    {
        string text = IntervalBox.Text.Trim().Replace(',', '.');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            seconds = 3;

        seconds = Math.Clamp(seconds, 0.2, 120);
        IntervalBox.Text = seconds.ToString("0.###", CultureInfo.InvariantCulture);
        return seconds * 1000.0;
    }

    void Start()
    {
        if (_run != null) return;
        if (_grid == null) ShowGrid();          // starting without the grid measures nothing
        if (_grid == null) return;

        Events.Items.Clear();

        double interval = IntervalMs();
        _run = new BenchRun(interval);
        _grid.Begin(_run);
        _run.Start();

        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        MonitorBox.IsEnabled = false;
        IntervalBox.IsEnabled = false;

        ProbeLog.Log("замер", $"старт, интервал {interval / 1000.0:0.###} с, " +
                              $"цвета: {string.Join(", ", Array.ConvertAll(BenchRun.Palette, p => p.Name))}");
    }

    void Stop(string why)
    {
        if (_run == null) return;

        double ms = _run.Read();
        _run.Stop();
        _run = null;
        _grid?.End();

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        MonitorBox.IsEnabled = true;
        IntervalBox.IsEnabled = true;

        StepText.Text = "остановлено";
        ProbeLog.Log("замер", $"стоп ({why}) на {BenchRun.Clock(ms)} с, шагов {Events.Items.Count}");
    }

    /// <summary>
    /// The moment the screen actually changed, as the frame that carried the change reports
    /// it. This is the number to subtract from the strip's time read off the video.
    /// </summary>
    void OnStepShown(int step, double ms)
    {
        var (name, _) = BenchRun.ColorOf(step);
        string line = string.Format(CultureInfo.InvariantCulture,
            "#{0,-3} {1,-8} {2,8} с   кадр {3}", step + 1, name, BenchRun.Clock(ms), BenchRun.Frame60(ms));

        Events.Items.Insert(0, line);
        ProbeLog.Log("шаг", line);
    }

    void RefreshReadout()
    {
        var run = _run;
        if (run == null) return;

        double ms = run.Read();
        ClockText.Text = BenchRun.Clock(ms);
        FrameText.Text = "кадр " + BenchRun.Frame60(ms);

        var (name, _) = BenchRun.ColorOf(run.StepAt(ms));
        double left = run.IntervalMs - ms % run.IntervalMs;
        StepText.Text = $"шаг {run.StepAt(ms) + 1} · {name} · следующий через {left / 1000.0:0.0} с";
    }
}
