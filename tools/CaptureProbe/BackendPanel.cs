using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rimlight.Capture;
using Rimlight.Capture.Backends;

namespace CaptureProbe;

/// <summary>One backend's live readout: colour swatch, numbers, per-second strip, jitter graph.</summary>
public sealed class BackendPanel : Border
{
    const int StripCells = BackendMetrics.HistorySeconds;   // 180 s
    const int SparkWidth = 256;
    const int SparkHeight = 56;
    const double SparkFloorMs = 25.0;   // smallest ceiling, so a fast backend still shows detail

    readonly ICaptureBackend _backend;
    readonly Func<MonitorInfo?> _monitor;

    readonly TextBlock _statusText;
    readonly TextBlock _metricsText;
    readonly Border _swatch;
    readonly Button _toggle;
    readonly TextBlock _scaleText;

    readonly WriteableBitmap _stripBmp;
    readonly byte[] _stripPixels = new byte[StripCells * 4];
    readonly WriteableBitmap _sparkBmp;
    readonly byte[] _sparkPixels = new byte[SparkWidth * SparkHeight * 4];
    readonly double[] _intervals = new double[SparkWidth];

    public ICaptureBackend Backend => _backend;

    public BackendPanel(ICaptureBackend backend, Func<MonitorInfo?> monitor, string? note = null)
    {
        _backend = backend;
        _monitor = monitor;

        Background = (Brush)Application.Current.Resources["Panel"];
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(10);
        Margin = new Thickness(0, 0, 0, 10);

        var fg = (Brush)Application.Current.Resources["Fg"];
        var dim = (Brush)Application.Current.Resources["FgDim"];

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- header ----
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = backend.Name,
            Foreground = fg,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 105
        });

        _toggle = new Button { Content = "Старт", Padding = new Thickness(10, 3, 10, 3) };
        _toggle.Click += (_, _) => Toggle();
        header.Children.Add(_toggle);

        _statusText = new TextBlock
        {
            Foreground = dim,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        header.Children.Add(_statusText);

        if (note != null)
        {
            header.Children.Add(new TextBlock
            {
                Text = "· " + note,
                Foreground = dim,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            });
        }

        Grid.SetRow(header, 0);
        root.Children.Add(header);

        // ---- swatch + numbers ----
        var body = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _swatch = new Border
        {
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(4),
            Height = SparkHeight,
            BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 78)),
            BorderThickness = new Thickness(1)
        };
        Grid.SetColumn(_swatch, 0);
        body.Children.Add(_swatch);

        _metricsText = new TextBlock
        {
            Foreground = fg,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(_metricsText, 1);
        body.Children.Add(_metricsText);

        _sparkBmp = new WriteableBitmap(SparkWidth, SparkHeight, 96, 96, PixelFormats.Bgra32, null);
        var sparkImg = new Image
        {
            Source = _sparkBmp,
            Width = SparkWidth,
            Height = SparkHeight,
            Stretch = Stretch.Fill
        };
        RenderOptions.SetBitmapScalingMode(sparkImg, BitmapScalingMode.NearestNeighbor);

        _scaleText = new TextBlock
        {
            Foreground = dim,
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 1, 2, 0)
        };

        var sparkBox = new StackPanel();
        sparkBox.Children.Add(sparkImg);
        sparkBox.Children.Add(_scaleText);
        Grid.SetColumn(sparkBox, 2);
        body.Children.Add(sparkBox);

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // ---- per-second history strip ----
        _stripBmp = new WriteableBitmap(StripCells, 1, 96, 96, PixelFormats.Bgra32, null);
        var stripImg = new Image
        {
            Source = _stripBmp,
            Height = 18,
            Stretch = Stretch.Fill,
            Margin = new Thickness(0, 8, 0, 0)
        };
        RenderOptions.SetBitmapScalingMode(stripImg, BitmapScalingMode.NearestNeighbor);
        Grid.SetRow(stripImg, 2);
        root.Children.Add(stripImg);

        Child = root;
    }

    void Toggle()
    {
        if (_backend.IsRunning)
        {
            _backend.Stop();
        }
        else
        {
            var m = _monitor();
            if (m == null) return;
            _backend.Start(m);
        }
    }

    public void StartWith(MonitorInfo m)
    {
        if (!_backend.IsRunning) _backend.Start(m);
    }

    public void StopBackend()
    {
        if (_backend.IsRunning) _backend.Stop();
    }

    static (byte r, byte g, byte b) StatusColor(BackendStatus s) => s switch
    {
        BackendStatus.Ok => (0, 180, 80),
        BackendStatus.Timeout => (220, 170, 0),
        BackendStatus.Error => (220, 60, 60),
        BackendStatus.Black => (0, 0, 0),
        BackendStatus.Starting => (70, 120, 220),
        _ => (60, 60, 65)
    };

    public void Refresh()
    {
        _backend.Metrics.Tick();
        var s = _backend.Metrics.Snapshot();

        _toggle.Content = _backend.IsRunning ? "Стоп" : "Старт";
        _statusText.Text = s.StatusText;

        var (sr, sg, sb) = StatusColor(s.Status);
        _statusText.Foreground = new SolidColorBrush(Color.FromRgb(sr, sg, sb));

        _swatch.Background = new SolidColorBrush(Color.FromRgb(s.R, s.G, s.B));

        _metricsText.Text =
            $"FPS {s.FpsInstant,5:F0}   среднее 5с {s.FpsAvg5s,6:F1}\n" +
            $"интервал p50 {s.P50Ms,6:F1} мс   p99 {s.P99Ms,6:F1} мс\n" +
            $"получение {s.AcquireMs,5:F2} мс   свод {s.ReduceMs,5:F2} мс\n" +
            $"кадров {s.Frames}  таймаутов {s.Timeouts}  ошибок {s.Errors}  чёрных {s.BlackFrames}  тёмных {s.DarkSpikes}  пропущено {s.Skipped}";

        RenderStrip();
        RenderSpark();
    }

    void RenderStrip()
    {
        var hist = _backend.Metrics.HistorySnapshot();
        for (int i = 0; i < StripCells; i++)
        {
            var (r, g, b) = StatusColor(hist[i]);
            int p = i * 4;
            _stripPixels[p + 0] = b;
            _stripPixels[p + 1] = g;
            _stripPixels[p + 2] = r;
            _stripPixels[p + 3] = 255;
        }
        _stripBmp.WritePixels(new Int32Rect(0, 0, StripCells, 1), _stripPixels, StripCells * 4, 0);
    }

    void RenderSpark()
    {
        Array.Clear(_sparkPixels);

        // background + reference line at 16.7 ms (one frame at 60 Hz)
        for (int y = 0; y < SparkHeight; y++)
        {
            for (int x = 0; x < SparkWidth; x++)
            {
                int p = (y * SparkWidth + x) * 4;
                _sparkPixels[p + 0] = 34;
                _sparkPixels[p + 1] = 32;
                _sparkPixels[p + 2] = 30;
                _sparkPixels[p + 3] = 255;
            }
        }

        // Auto-scale: a fixed 50 ms ceiling painted every GDI bar red and told us nothing.
        // Scale to the observed maximum instead, never below SparkFloorMs.
        int n = _backend.Metrics.CopyIntervals(_intervals);
        double maxMs = SparkFloorMs;
        for (int i = 0; i < n; i++) if (_intervals[i] > maxMs) maxMs = _intervals[i];
        _scaleText.Text = $"{maxMs:F0} мс";

        int refY = SparkHeight - 1 - (int)(16.7 / maxMs * (SparkHeight - 1));
        if (refY >= 0 && refY < SparkHeight)
        {
            for (int x = 0; x < SparkWidth; x += 2)
            {
                int p = (refY * SparkWidth + x) * 4;
                _sparkPixels[p + 0] = 90; _sparkPixels[p + 1] = 90; _sparkPixels[p + 2] = 90;
            }
        }

        for (int i = 0; i < n; i++)
        {
            int x = SparkWidth - n + i;
            if (x < 0) continue;
            double ms = Math.Min(_intervals[i], maxMs);
            int h = (int)(ms / maxMs * (SparkHeight - 1));
            // anything over twice the 60 Hz budget turns red - those are visible jerks
            bool over = _intervals[i] > 33.4;
            for (int y = SparkHeight - 1; y >= SparkHeight - 1 - h; y--)
            {
                int p = (y * SparkWidth + x) * 4;
                _sparkPixels[p + 0] = over ? (byte)60 : (byte)200;
                _sparkPixels[p + 1] = over ? (byte)60 : (byte)160;
                _sparkPixels[p + 2] = over ? (byte)220 : (byte)80;
                _sparkPixels[p + 3] = 255;
            }
        }

        _sparkBmp.WritePixels(new Int32Rect(0, 0, SparkWidth, SparkHeight), _sparkPixels, SparkWidth * 4, 0);
    }
}
