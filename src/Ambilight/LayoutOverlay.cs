using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Ambilight.Capture;
using Ambilight.Leds;

namespace Ambilight;

/// <summary>
/// Draws the LED zones over the real screen, so the layout can be matched against the
/// physical strip instead of guessed at. Clicking a cell marks it, and the engine lights
/// the matching LED green - which is what actually tells you whether cell 37 on screen is
/// LED 37 on the wall.
/// </summary>
public sealed class LayoutOverlay : Window
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

    LedZone[] _zones = Array.Empty<LedZone>();
    int _selected = -1;

    /// <summary>Fires with the clicked LED index, or -1 when the selection is cleared.</summary>
    public event Action<int>? SelectionChanged;

    static readonly Brush CellFill = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
    static readonly Brush CellFillSelected = new SolidColorBrush(Color.FromArgb(245, 40, 200, 90));
    static readonly Brush CellStroke = new SolidColorBrush(Color.FromArgb(255, 40, 40, 46));

    public LayoutOverlay(MonitorInfo monitor)
    {
        _monitor = monitor;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = null;              // null, not Transparent: empty areas stay click-through
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = _canvas;

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
    }

    public void SetZones(LedZone[] zones)
    {
        _zones = zones;
        if (_selected >= zones.Length) _selected = -1;

        _canvas.Children.Clear();
        _cells.Clear();
        _labels.Clear();

        for (int i = 0; i < zones.Length; i++)
        {
            int index = i;
            var cell = new Rectangle
            {
                Fill = CellFill,
                Stroke = CellStroke,
                StrokeThickness = 1,
                Cursor = Cursors.Hand
            };
            cell.MouseLeftButtonDown += (_, _) => Select(index == _selected ? -1 : index);

            _canvas.Children.Add(cell);
            _cells.Add(cell);

            var label = new TextBlock
            {
                Text = (i + 1).ToString(),
                Foreground = Brushes.Black,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                IsHitTestVisible = false
            };
            _canvas.Children.Add(label);
            _labels.Add(label);
        }

        Arrange();
        Paint();
    }

    void Select(int index)
    {
        _selected = index;
        Paint();
        ProbeLog.Log("схема", index >= 0 ? $"клик по ячейке {index + 1}" : "снятие выделения");
        SelectionChanged?.Invoke(index);
    }

    void Paint()
    {
        // Only the picked cell changes. Repainting all of them dimmed made the app lag
        // about a second behind each click: this is a full-screen transparent window, which
        // WPF renders in software and pushes whole, so touching every cell means redrawing
        // 3440x1440 instead of one small rectangle.
        for (int i = 0; i < _cells.Count; i++)
            _cells[i].Fill = i == _selected ? CellFillSelected : CellFill;
    }

    /// <summary>
    /// Zones are laid out at their true proportions here - unlike the small preview, this
    /// window really is the screen, so the sampling bands are drawn exactly where they are.
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
            double chh = Math.Max(1, (z.Y1 - z.Y0) * h);

            cell.Width = cw;
            cell.Height = chh;
            Canvas.SetLeft(cell, z.X0 * w);
            Canvas.SetTop(cell, z.Y0 * h);

            var label = _labels[i];
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, z.X0 * w + (cw - label.DesiredSize.Width) / 2);
            Canvas.SetTop(label, z.Y0 * h + (chh - label.DesiredSize.Height) / 2);
        }
    }
}
