using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Rimlight.Capture;
using Rimlight.Leds;

namespace Rimlight;

/// <summary>
/// Draws the LED zones over the real screen, so the layout can be matched against the
/// physical strip instead of guessed at. Clicking a cell marks it, and the engine lights
/// the matching LED green - which is what actually tells you whether cell 37 on screen is
/// LED 37 on the wall.
///
/// The same window carries the calibration patches: every zone is filled with one test
/// colour and stretched out to the screen edge, so the band next to the wall and the light
/// on the wall can be compared side by side. The middle stays click-through, which keeps
/// the settings window usable on a single monitor while the patch is up.
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
    Brush? _patch;

    /// <summary>Test colour the zones are filled with, or null for the numbered map.</summary>
    public Color? Patch => (_patch as SolidColorBrush)?.Color;

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

    public void SetPatch(Color? color)
    {
        _patch = color is { } c ? new SolidColorBrush(c) : null;

        foreach (var cell in _cells)
        {
            cell.Stroke = _patch == null ? CellStroke : null;
            cell.IsHitTestVisible = _patch == null;
        }
        foreach (var label in _labels)
            label.Visibility = _patch == null ? Visibility.Visible : Visibility.Collapsed;

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
            _cells[i].Fill = _patch ?? (i == _selected ? CellFillSelected : CellFill);
    }

    /// <summary>
    /// Zones are laid out at their true proportions here - unlike the small preview, this
    /// window really is the screen, so the sampling bands are drawn exactly where they are.
    /// </summary>
    void Arrange()
    {
        double w = _canvas.ActualWidth, h = _canvas.ActualHeight;
        if (w < 10 || h < 10) return;

        // крайние зоны верха и низа заливки дотягиваются до углов экрана
        double cornerLeft = 1, cornerRight = 0;
        foreach (var z in _zones)
            if (z.Side is Side.Top or Side.Bottom)
            {
                cornerLeft = Math.Min(cornerLeft, z.X0);
                cornerRight = Math.Max(cornerRight, z.X1);
            }

        for (int i = 0; i < _cells.Count && i < _zones.Length; i++)
        {
            var z = _zones[i];
            var cell = _cells[i];
            if (_patch != null)
            {
                z = ToEdge(z);
                if (z.Side is Side.Top or Side.Bottom)
                {
                    // с допуском: верх и низ считаются в разные стороны, и края расходятся
                    // в последнем знаке
                    if (z.X0 <= cornerLeft + 1e-6) z = z with { X0 = 0 };
                    if (z.X1 >= cornerRight - 1e-6) z = z with { X1 = 1 };
                }
            }

            double cw = Math.Max(1, (z.X1 - z.X0) * w);
            double chh = Math.Max(1, (z.Y1 - z.Y0) * h);

            // заливка без зазоров между соседними зонами
            if (_patch != null) { cw += 1; chh += 1; }

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

    /// <summary>
    /// Extends a zone to the screen edge it sits against. The edge margin otherwise leaves
    /// a strip of desktop between the patch and the bezel, right where the eye compares it
    /// with the wall.
    /// </summary>
    static LedZone ToEdge(LedZone z) => z.Side switch
    {
        Side.Left => z with { X0 = 0 },
        Side.Right => z with { X1 = 1 },
        Side.Top => z with { Y0 = 0 },
        _ => z with { Y1 = 1 }
    };
}
