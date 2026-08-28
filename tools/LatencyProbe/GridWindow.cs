using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Rimlight.Capture;
using Rimlight.Leds;

namespace LatencyProbe;

/// <summary>
/// The screen half of the measurement: the LED zones drawn where they really are, filled
/// with one colour at a time, and a clock in the middle of them.
///
/// The same window as the application's layout overlay in spirit, but opaque rather than
/// transparent. Transparency forces WPF to render the window in software and hand the whole
/// surface to the compositor, which is fine for pointing at cells and wrong here - it puts
/// an unknown and variable delay in front of the very edge being timed. Black background,
/// no transparency, hardware path.
/// </summary>
public sealed class GridWindow : Window
{
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_SHOWWINDOW = 0x0040;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    readonly Canvas _canvas = new();
    readonly List<Rectangle> _cells = new();
    readonly List<TextBlock> _labels = new();
    readonly MonitorInfo _monitor;

    readonly TextBlock _clock = new();
    readonly TextBlock _frames = new();
    readonly TextBlock _step = new();
    readonly TextBlock _rate = new();

    LedZone[] _zones = Array.Empty<LedZone>();
    BenchRun? _run;
    int _shownStep = -1;

    // Repaint rate, measured rather than assumed: it is the best resolution the clock in
    // the video can have, and it is not always the refresh rate of the monitor.
    int _ticks;
    double _ticksSince;

    static readonly Brush IdleFill = new SolidColorBrush(Color.FromRgb(58, 58, 66));
    static readonly Brush IdleStroke = new SolidColorBrush(Color.FromRgb(96, 96, 108));

    /// <summary>Fires on the frame that first shows a new colour, with the step index and
    /// the reading of the clock drawn next to it.</summary>
    public event Action<int, double>? StepShown;

    public GridWindow(MonitorInfo monitor)
    {
        _monitor = monitor;

        Title = "Сетка";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Black;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        Cursor = Cursors.None;

        var readout = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        Digits(_clock, 112, FontWeights.Bold, Brushes.White);
        Digits(_frames, 34, FontWeights.SemiBold, new SolidColorBrush(Color.FromRgb(180, 180, 190)));
        Digits(_step, 26, FontWeights.Normal, new SolidColorBrush(Color.FromRgb(150, 150, 160)));
        Digits(_rate, 15, FontWeights.Normal, new SolidColorBrush(Color.FromRgb(110, 110, 120)));

        readout.Children.Add(_clock);
        readout.Children.Add(_frames);
        readout.Children.Add(_step);
        readout.Children.Add(_rate);

        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(readout);
        Content = root;

        // Placed in physical pixels through Win32 rather than WPF's device-independent
        // units, so it lands correctly on a monitor with any scaling factor.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, HWND_TOPMOST, _monitor.Left, _monitor.Top,
                         _monitor.Width, _monitor.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        };

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        SizeChanged += (_, _) => Arrange();
        Closed += (_, _) => CompositionTarget.Rendering -= OnRendering;

        ShowIdle();
    }

    static void Digits(TextBlock t, double size, FontWeight weight, Brush brush)
    {
        // A monospaced face keeps the milliseconds in the same place from frame to frame -
        // proportional digits shift the whole number sideways, which is harder to read off
        // a video one frame at a time.
        t.FontFamily = new FontFamily("Consolas, Courier New");
        t.FontSize = size;
        t.FontWeight = weight;
        t.Foreground = brush;
        t.TextAlignment = TextAlignment.Center;
        t.IsHitTestVisible = false;
    }

    public void SetZones(LedZone[] zones)
    {
        _zones = zones;

        _canvas.Children.Clear();
        _cells.Clear();
        _labels.Clear();

        for (int i = 0; i < zones.Length; i++)
        {
            var cell = new Rectangle { Fill = IdleFill, Stroke = IdleStroke, StrokeThickness = 1 };
            _canvas.Children.Add(cell);
            _cells.Add(cell);

            var label = new TextBlock
            {
                Text = (i + 1).ToString(),
                Foreground = Brushes.White,
                FontSize = 11,
                IsHitTestVisible = false
            };
            _canvas.Children.Add(label);
            _labels.Add(label);
        }

        Arrange();
        if (_run == null || !_run.Running) ShowIdle();
    }

    public void Begin(BenchRun run)
    {
        _run = run;
        _shownStep = -1;
        _ticks = 0;
        _ticksSince = 0;
        _rate.Text = "";

        // Numbering is hidden for the run: a black digit inside a red cell drags that
        // zone's average down, and the average is what the strip is fed.
        foreach (var label in _labels) label.Visibility = Visibility.Collapsed;

        CompositionTarget.Rendering -= OnRendering;
        CompositionTarget.Rendering += OnRendering;
    }

    public void End()
    {
        CompositionTarget.Rendering -= OnRendering;
        _run = null;
        ShowIdle();
    }

    void ShowIdle()
    {
        foreach (var cell in _cells) { cell.Fill = IdleFill; cell.Stroke = IdleStroke; }
        foreach (var label in _labels) label.Visibility = Visibility.Visible;

        _clock.Text = "000.000";
        _frames.Text = "кадр 0";
        _step.Text = _cells.Count > 0 ? $"{_cells.Count} диодов · «Старт» для запуска" : "нет зон";
        _rate.Text = "Esc — убрать сетку";
    }

    /// <summary>
    /// One reading per frame drives everything drawn in that frame. The colour and the
    /// digits then belong to the same moment by construction, which is the property the
    /// whole method rests on: the video is read by finding the frame where the cells change
    /// and taking the number printed next to them.
    /// </summary>
    void OnRendering(object? sender, EventArgs e)
    {
        var run = _run;
        if (run == null || !run.Running) return;

        double ms = run.Read();
        int step = run.StepAt(ms);

        if (step != _shownStep)
        {
            _shownStep = step;
            var (name, color) = BenchRun.ColorOf(step);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            foreach (var cell in _cells) { cell.Fill = brush; cell.Stroke = brush; }
            _step.Text = $"шаг {step + 1} · {name}";
            StepShown?.Invoke(step, ms);
        }

        // Zero-padded, unlike the same number in the log: a fixed width keeps the digits
        // in one place when the count crosses ten seconds, which is what makes them
        // readable while scrubbing a video one frame at a time.
        _clock.Text = (ms / 1000.0).ToString("000.000", CultureInfo.InvariantCulture);
        _frames.Text = "кадр " + BenchRun.Frame60(ms);

        _ticks++;
        if (ms - _ticksSince >= 1000)
        {
            _rate.Text = $"обновление экрана: {_ticks * 1000.0 / (ms - _ticksSince):0} к/с";
            _ticks = 0;
            _ticksSince = ms;
        }
    }

    /// <summary>
    /// Zones at their true proportions - this window really is the screen, so the sampling
    /// bands are drawn exactly where the engine reads them.
    /// </summary>
    void Arrange()
    {
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        if (w < 10 || h < 10) return;

        for (int i = 0; i < _cells.Count && i < _zones.Length; i++)
        {
            var z = _zones[i];
            var cell = _cells[i];

            double cw = Math.Max(1, (z.X1 - z.X0) * w);
            double ch = Math.Max(1, (z.Y1 - z.Y0) * h);

            cell.Width = cw;
            cell.Height = ch;
            Canvas.SetLeft(cell, z.X0 * w);
            Canvas.SetTop(cell, z.Y0 * h);

            var label = _labels[i];
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, z.X0 * w + (cw - label.DesiredSize.Width) / 2);
            Canvas.SetTop(label, z.Y0 * h + (ch - label.DesiredSize.Height) / 2);
        }
    }
}
