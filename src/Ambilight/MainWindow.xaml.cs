using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Ambilight.Capture;
using Ambilight.Leds;
using Ambilight.Power;

namespace Ambilight;

public partial class MainWindow : Window
{
    readonly AmbilightConfig _cfg = AmbilightConfig.Load();
    readonly AmbilightEngine _engine = new();
    readonly PowerWatcher _power;
    readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(50) };

    readonly List<MonitorInfo> _monitors = Native.EnumerateMonitors();
    readonly List<Rectangle> _previewShapes = new();
    readonly List<TextBlock> _previewLabels = new();
    System.Windows.Forms.NotifyIcon? _tray;
    byte[] _previewColors = Array.Empty<byte>();

    ComboBox _monitorBox = null!, _portBox = null!, _cornerBox = null!, _modeBox = null!, _langBox = null!;
    TextBlock _totalText = null!;
    StackPanel _offsetHost = null!;
    TextBlock _countNote = null!;
    LayoutOverlay? _overlay;
    Button _overlayButton = null!;
    int _overlayLayoutVersion = -1;

    /// <summary>
    /// Editing a count field fires per keystroke, and each rebuild reopens the port because
    /// the LED total lives in the frame header. Settling first means one rebuild per edit
    /// instead of one per character.
    /// </summary>
    readonly DispatcherTimer _relayoutDebounce = new() { Interval = TimeSpan.FromMilliseconds(600) };
    int _previewLayoutVersion = -1;
    bool _dirty;
    bool _rebuildingUi;

    /// <summary>
    /// The last applied configuration. Edits act on _cfg immediately so the strip responds
    /// live, but nothing reaches disk until Apply; Cancel copies this back.
    /// </summary>
    AmbilightConfig _saved = null!;

    public MainWindow()
    {
        InitializeComponent();

        ProbeLog.Configure(AmbilightConfig.LogPath, _cfg.WriteLog);
        Loc.Load(_cfg.Language);

        Icon = LoadIcon();
        _saved = _cfg.Clone();

        // The watcher only reports; deciding what counts as "nobody is looking" stays here,
        // because it is this application's settings that say so.
        _power = new PowerWatcher();
        _power.Changed += (_, state) =>
        {
            string? reason =
                state.Suspended && _cfg.OffOnSuspend ? "сон" :
                state.Locked && _cfg.OffOnLock ? "блокировка" :
                state.DisplayOff && _cfg.OffOnDisplayOff ? "экран выключен" :
                null;

            if (reason != null) _engine.Pause(reason);
            else _engine.Resume();
        };

        RestoreWindowGeometry();
        BuildSettings();

        Loaded += (_, _) =>
        {
            _power.Attach(this);
            SetupTray();
            Restart();
        };

        // the overlay is shown without activation, so its own key handler only fires while
        // it happens to hold focus; catch Escape here as well
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _overlay != null)
            {
                _overlay.Close();
                e.Handled = true;
            }
        };

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && _cfg.MinimizeToTray)
                Hide();
        };

        _ui.Tick += (_, _) => RefreshUi();
        _ui.Start();

        _relayoutDebounce.Tick += (_, _) =>
        {
            _relayoutDebounce.Stop();
            _engine.RequestRelayout();
        };

        ApplyButton.Click += (_, _) => ApplyChanges();
        CancelButton.Click += (_, _) => CancelChanges();

        Closing += (_, _) =>
        {
            // Window geometry is not a setting the user is editing, so it persists on its
            // own - written onto the last applied config so pending edits stay discarded.
            SaveWindowGeometry();
            _saved.WindowWidth = _cfg.WindowWidth;
            _saved.WindowHeight = _cfg.WindowHeight;
            _saved.WindowLeft = _cfg.WindowLeft;
            _saved.WindowTop = _cfg.WindowTop;
            _saved.WindowMaximized = _cfg.WindowMaximized;
            _saved.Save();
            _overlay?.Close();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _power.Dispose();
            _engine.Dispose();
        };
    }

    // ---- window geometry ----------------------------------------------------

    void RestoreWindowGeometry()
    {
        Width = Math.Max(MinWidth, _cfg.WindowWidth);
        Height = Math.Max(MinHeight, _cfg.WindowHeight);

        if (_cfg.WindowLeft is double left && _cfg.WindowTop is double top)
        {
            // only honour a saved position that still lands on an attached monitor
            var vs = SystemParameters.VirtualScreenWidth;
            var vt = SystemParameters.VirtualScreenHeight;
            if (left > -Width && left < vs && top > -Height && top < vt)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (_cfg.WindowMaximized) WindowState = WindowState.Maximized;
    }

    void SaveWindowGeometry()
    {
        _cfg.WindowMaximized = WindowState == WindowState.Maximized;

        // RestoreBounds holds the pre-maximise rectangle; ActualWidth would save the
        // maximised size and the window would never come back to its normal shape
        var r = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        if (r.Width > 100 && r.Height > 100)
        {
            _cfg.WindowWidth = r.Width;
            _cfg.WindowHeight = r.Height;
            _cfg.WindowLeft = r.Left;
            _cfg.WindowTop = r.Top;
        }
    }

    // ---- tray ---------------------------------------------------------------

    /// <summary>
    /// The icon is embedded rather than shipped beside the exe: a content file cannot be
    /// resolved from a single-file publish, which crashed the published build outright.
    /// </summary>
    static System.Windows.Media.Imaging.BitmapFrame? LoadIcon()
    {
        try
        {
            var s = Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico"))?.Stream;
            return s == null ? null : System.Windows.Media.Imaging.BitmapFrame.Create(s);
        }
        catch { return null; }
    }

    static System.Drawing.Icon TrayIcon()
    {
        try
        {
            var s = Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico"))?.Stream;
            return s == null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(s);
        }
        catch { return System.Drawing.SystemIcons.Application; }
    }

    void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = TrayIcon(),
            Text = "Ambilight",
            // present for the whole session, not just while minimised - otherwise the only
            // way back from the tray is to already know the app is running
            Visible = _cfg.MinimizeToTray
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("tray.show"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(Loc.T("main.exit"), null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // ---- settings UI --------------------------------------------------------

    void BuildSettings()
    {
        _rebuildingUi = true;
        Tabs.Items.Clear();
        PreviewCaption.Text = Loc.T("preview.caption");

        AddTab(Loc.T("tab.main"), panel =>
        {
            _langBox = new ComboBox { Margin = new Thickness(0, 2, 0, 4), Foreground = Brushes.Black };
            foreach (var code in Loc.Available) _langBox.Items.Add(Loc.DisplayName(code));
            _langBox.SelectedIndex = Math.Max(0, Array.IndexOf(Loc.Available, Loc.Language));
            _langBox.SelectionChanged += (_, _) =>
            {
                if (_rebuildingUi) return;
                _cfg.Language = Loc.Available[Math.Max(0, _langBox.SelectedIndex)];
                _dirty = true;
                Loc.Load(_cfg.Language);
                BuildSettings();          // the whole panel is built in code, so rebuild it
            };
            panel.Children.Add(Labeled(Loc.T("main.language"), _langBox));
            panel.Children.Add(Note(Loc.T("main.language.note")));

            panel.Children.Add(Check(Loc.T("main.boost"), _cfg.PreviewBoost, v => _cfg.PreviewBoost = v));
            panel.Children.Add(Note(Loc.T("main.boost.note")));

            panel.Children.Add(Check(Loc.T("main.tray"), _cfg.MinimizeToTray, v =>
            {
                _cfg.MinimizeToTray = v;
                if (_tray != null) _tray.Visible = v;
            }));

            // the registry is the real state; mirror it so a stale stored flag cannot make
            // Cancel silently switch autostart back on or off
            _cfg.Autostart = Autostart.IsEnabled();
            panel.Children.Add(Check(Loc.T("main.autostart"), _cfg.Autostart, v =>
            {
                _cfg.Autostart = v;
                Autostart.Set(v);
            }));

            panel.Children.Add(Check(Loc.T("main.log"), _cfg.WriteLog, v =>
            {
                _cfg.WriteLog = v;
                ProbeLog.Configure(AmbilightConfig.LogPath, v);
            }));
            panel.Children.Add(Note(Loc.T("main.log.note")));

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var exportBtn = new Button { Content = Loc.T("main.export"), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 0) };
            exportBtn.Click += (_, _) => ExportConfig();
            var importBtn = new Button { Content = Loc.T("main.import"), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 0) };
            importBtn.Click += (_, _) => ImportConfig();
            var exitBtn = new Button { Content = Loc.T("main.exit"), Padding = new Thickness(12, 4, 12, 4) };
            exitBtn.Click += (_, _) => Close();
            row.Children.Add(exportBtn);
            row.Children.Add(importBtn);
            row.Children.Add(exitBtn);
            panel.Children.Add(row);

            var pathText = Text("", dim: true, size: 11);
            var link = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run(AmbilightConfig.Path))
            { Foreground = Res("Fg") };
            link.Click += (_, _) => OpenSettingsFolder();
            pathText.Inlines.Add(Loc.T("main.paths").Replace("{0}", "").TrimEnd());
            pathText.Inlines.Add(" ");
            pathText.Inlines.Add(link);
            pathText.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(pathText);
        });

        AddTab(Loc.T("tab.device"), panel =>
        {
            _monitorBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8), Foreground = Brushes.Black };
            foreach (var m in _monitors) _monitorBox.Items.Add(m.ToString());
            int idx = _monitors.FindIndex(m => m.DeviceName == _cfg.MonitorDeviceName);
            if (idx < 0) idx = _monitors.FindIndex(m => m.IsPrimary);
            _monitorBox.SelectedIndex = Math.Max(0, idx);
            panel.Children.Add(Labeled(Loc.T("device.monitor"), _monitorBox));

            _portBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8), IsEditable = true, Text = _cfg.PortName, Foreground = Brushes.Black };
            foreach (var p in SerialPort.GetPortNames()) _portBox.Items.Add(p);
            panel.Children.Add(Labeled(Loc.T("device.port"), _portBox));

            panel.Children.Add(IntBox(Loc.T("device.baud"), _cfg.BaudRate, v => _cfg.BaudRate = v));

            var apply = new Button { Content = Loc.T("device.apply"), Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(8, 5, 8, 5) };
            apply.Click += (_, _) => Restart();
            panel.Children.Add(apply);
        });

        AddTab(Loc.T("tab.layout"), panel =>
        {
            panel.Children.Add(IntBox(Loc.T("layout.top"), _cfg.TopCount, v => { _cfg.TopCount = v; CountChanged(); }));
            panel.Children.Add(IntBox(Loc.T("layout.bottom"), _cfg.BottomCount, v => { _cfg.BottomCount = v; CountChanged(); }));
            panel.Children.Add(IntBox(Loc.T("layout.left"), _cfg.LeftCount, v => { _cfg.LeftCount = v; CountChanged(); }));
            panel.Children.Add(IntBox(Loc.T("layout.right"), _cfg.RightCount, v => { _cfg.RightCount = v; CountChanged(); }));

            _cornerBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8), Foreground = Brushes.Black };
            _cornerBox.Items.Add(Loc.T("layout.corner.br"));
            _cornerBox.Items.Add(Loc.T("layout.corner.bl"));
            _cornerBox.Items.Add(Loc.T("layout.corner.tl"));
            _cornerBox.Items.Add(Loc.T("layout.corner.tr"));
            _cornerBox.SelectedIndex = (int)_cfg.StartCorner;
            _cornerBox.SelectionChanged += (_, _) =>
            {
                if (_rebuildingUi) return;
                _cfg.StartCorner = (Corner)Math.Max(0, _cornerBox.SelectedIndex);
                _dirty = true;
                _engine.RequestRelayout();
            };
            panel.Children.Add(Labeled(Loc.T("layout.corner"), _cornerBox));

            panel.Children.Add(Check(Loc.T("layout.ccw"), _cfg.CounterClockwise,
                v => { _cfg.CounterClockwise = v; _engine.RequestRelayout(); }));

            _offsetHost = new StackPanel();
            panel.Children.Add(_offsetHost);
            RebuildOffsetSlider();

            panel.Children.Add(Slider(Loc.T("layout.margin"), _cfg.EdgeMarginPercent, 0, 15, 0.1,
                v => { _cfg.EdgeMarginPercent = v; _engine.RequestRelayout(); }));
            panel.Children.Add(Slider(Loc.T("layout.marginV"), _cfg.EdgeMarginPercentV, 0, 15, 0.1,
                v => { _cfg.EdgeMarginPercentV = v; _engine.RequestRelayout(); }));
            panel.Children.Add(Slider(Loc.T("layout.depth"), _cfg.DepthPercent, 1, 25, 0.5,
                v => { _cfg.DepthPercent = v; _engine.RequestRelayout(); }));

            panel.Children.Add(Note(Loc.T("layout.note")));

            _totalText = Text("", size: 13);
            _totalText.Margin = new Thickness(0, 4, 0, 0);
            _totalText.FontWeight = FontWeights.Bold;
            panel.Children.Add(_totalText);
            UpdateTotal();

            // belongs next to the counters it talks about, not in the status panel where it
            // read as a permanent alarm
            _overlayButton = new Button
            {
                Content = Loc.T(_overlay != null ? "layout.overlay.hide" : "layout.overlay.show"),
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(8, 5, 8, 5)
            };
            _overlayButton.Click += (_, _) => ToggleOverlay();
            panel.Children.Add(_overlayButton);
            panel.Children.Add(Note(Loc.T("layout.overlay.note")));

            var countNote = Text(string.Format(Loc.T("warn.count"), _cfg.TotalLeds), size: 11);
            countNote.Foreground = Res("Warn");
            countNote.Margin = new Thickness(0, 6, 0, 0);
            _countNote = countNote;
            panel.Children.Add(countNote);
        });

        AddTab(Loc.T("tab.color"), panel =>
        {
            panel.Children.Add(Slider(Loc.T("color.brightness"), _cfg.MaxBrightness, 0, 1, 0.01, v => _cfg.MaxBrightness = v));

            // cubic response: the useful range is the bottom few percent, and a linear
            // slider spends nearly all its travel on values that just black the strip out
            panel.Children.Add(Slider(Loc.T("color.minluma"), Math.Pow(_cfg.MinLuma / 0.3, 1.0 / 3.0), 0, 1, 0.005,
                v => _cfg.MinLuma = Math.Pow(v, 3) * 0.3,
                v => (Math.Pow(v, 3) * 0.3).ToString("0.0000")));

            panel.Children.Add(Slider(Loc.T("color.saturation"), _cfg.Saturation, 0, 2.5, 0.05, v => _cfg.Saturation = v));
            panel.Children.Add(Slider(Loc.T("color.gamma"), _cfg.Gamma, 1.0, 3.5, 0.05, v => _cfg.Gamma = v));
            panel.Children.Add(Slider(Loc.T("color.temperature"), _cfg.TemperatureK, 2000, 10000, 100, v => _cfg.TemperatureK = (int)v));
            panel.Children.Add(Slider(Loc.T("color.gainR"), _cfg.GainR, 0, 2, 0.01, v => _cfg.GainR = v));
            panel.Children.Add(Slider(Loc.T("color.gainG"), _cfg.GainG, 0, 2, 0.01, v => _cfg.GainG = v));
            panel.Children.Add(Slider(Loc.T("color.gainB"), _cfg.GainB, 0, 2, 0.01, v => _cfg.GainB = v));

            panel.Children.Add(Check(Loc.T("color.dither"), _cfg.Dithering, v => _cfg.Dithering = v));
            panel.Children.Add(Note(Loc.T("color.dither.note")));

            panel.Children.Add(Slider(Loc.T("color.rise"), _cfg.SmoothingRise, 0.02, 1, 0.01, v => _cfg.SmoothingRise = v));
            panel.Children.Add(Slider(Loc.T("color.fall"), _cfg.SmoothingFall, 0.02, 1, 0.01, v => _cfg.SmoothingFall = v));
        });

        AddTab(Loc.T("tab.capture"), panel =>
        {
            _modeBox = new ComboBox { Margin = new Thickness(0, 2, 0, 4), Foreground = Brushes.Black };
            _modeBox.Items.Add(Loc.T("capture.auto"));
            _modeBox.Items.Add(Loc.T("capture.dda"));
            _modeBox.Items.Add(Loc.T("capture.wgc"));
            _modeBox.Items.Add(Loc.T("capture.gdi"));
            _modeBox.SelectedIndex = (int)_cfg.CaptureMode;
            _modeBox.SelectionChanged += (_, _) =>
            {
                if (_rebuildingUi) return;
                _cfg.CaptureMode = (CaptureMode)Math.Max(0, _modeBox.SelectedIndex);
                _dirty = true;
                _engine.RestartCapture();     // applies at once; the port is left alone
            };
            panel.Children.Add(Labeled(Loc.T("capture.method"), _modeBox));
            panel.Children.Add(Note(Loc.T("capture.method.note")));

            panel.Children.Add(Slider(Loc.T("capture.fps"), _cfg.MaxFps, 10, 144, 1, v => _cfg.MaxFps = (int)v));

            panel.Children.Add(Check(Loc.T("capture.onchange"), _cfg.SendOnlyOnChange, v => _cfg.SendOnlyOnChange = v));
            var keep = Note(Loc.T("capture.onchange.note"));
            keep.Foreground = Res("Warn");
            panel.Children.Add(keep);

            // Takes effect on the next output tick without restarting the engine, so no
            // port reopen and no bootloader pause for a checkbox.
            panel.Children.Add(Check(Loc.T("capture.publish"), _cfg.PublishFrames, v => _cfg.PublishFrames = v));
            panel.Children.Add(Note(Loc.T("capture.publish.note")));
        });

        AddTab(Loc.T("tab.power"), panel =>
        {
            var head = Text(Loc.T("power.head"), size: 13);
            head.FontWeight = FontWeights.Bold;
            head.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(head);

            panel.Children.Add(Check(Loc.T("power.exit"), _cfg.OffOnExit, v => _cfg.OffOnExit = v));
            panel.Children.Add(Check(Loc.T("power.display"), _cfg.OffOnDisplayOff, v => _cfg.OffOnDisplayOff = v));
            panel.Children.Add(Check(Loc.T("power.lock"), _cfg.OffOnLock, v => _cfg.OffOnLock = v));
            panel.Children.Add(Check(Loc.T("power.suspend"), _cfg.OffOnSuspend, v => _cfg.OffOnSuspend = v));
        });

        _rebuildingUi = false;
    }

    void CountChanged()
    {
        UpdateTotal();
        _relayoutDebounce.Stop();
        _relayoutDebounce.Start();
    }

    void UpdateTotal()
    {
        if (_totalText == null) return;
        _totalText.Text = string.Format(Loc.T("layout.total"), _cfg.TotalLeds);
        if (_countNote != null) _countNote.Text = string.Format(Loc.T("warn.count"), _cfg.TotalLeds);
        RebuildOffsetSlider();
    }

    /// <summary>
    /// A start corner alone cannot say where the first LED physically sits - heading up
    /// from a corner puts it on the side, heading sideways puts it on the bottom. The
    /// offset covers both, and applies live so it can be dialled in against the strip.
    /// </summary>
    void RebuildOffsetSlider()
    {
        if (_offsetHost == null) return;

        int n = Math.Max(1, _cfg.TotalLeds);
        _cfg.IndexOffset = Math.Clamp(_cfg.IndexOffset, -n, n);

        _offsetHost.Children.Clear();
        _offsetHost.Children.Add(Slider(Loc.T("layout.offset"), _cfg.IndexOffset, -n, n, 1,
            v => { _cfg.IndexOffset = (int)v; _engine.RequestRelayout(); }));
    }

    void Restart()
    {
        if (_monitorBox.SelectedIndex >= 0 && _monitorBox.SelectedIndex < _monitors.Count)
            _cfg.MonitorDeviceName = _monitors[_monitorBox.SelectedIndex].DeviceName;

        _cfg.PortName = string.IsNullOrWhiteSpace(_portBox.Text) ? "COM4" : _portBox.Text.Trim();

        _engine.Start(_cfg);
        RebuildPreview();
    }

    void ToggleOverlay()
    {
        if (_overlay != null) { _overlay.Close(); return; }      // Closed handler tidies up

        var monitor = _engine.Monitor;
        if (monitor == null) return;

        // No special mode is needed: the overlay sits on the monitor being captured, so its
        // white cells land in the very zones being sampled and reach the strip through the
        // normal path. That also makes the check worth more - it proves the geometry and
        // the whole pipeline, not just the numbering.
        _overlay = new LayoutOverlay(monitor);
        _overlay.Closed += (_, _) =>
        {
            _overlay = null;
            _overlayLayoutVersion = -1;
            if (_overlayButton != null) _overlayButton.Content = Loc.T("layout.overlay.show");
        };

        _overlay.Show();
        _overlay.SetZones(_engine.Zones);
        _overlayLayoutVersion = _engine.LayoutVersion;
        _overlayButton.Content = Loc.T("layout.overlay.hide");
    }

    void MarkDirty()
    {
        if (_rebuildingUi) return;
        _dirty = true;
    }

    void ApplyChanges()
    {
        _cfg.Save();
        _saved = _cfg.Clone();
        _dirty = false;
    }

    void CancelChanges()
    {
        int tab = Tabs.SelectedIndex;      // rebuilding the tabs would drop the user back to the first

        _cfg.CopyFrom(_saved);
        _dirty = false;

        Loc.Load(_cfg.Language);
        ProbeLog.Configure(AmbilightConfig.LogPath, _cfg.WriteLog);
        Autostart.Set(_cfg.Autostart);

        BuildSettings();
        if (tab >= 0 && tab < Tabs.Items.Count) Tabs.SelectedIndex = tab;

        _engine.RequestRelayout();
        _engine.RestartCapture();
    }

    // ---- import / export ----------------------------------------------------

    static void OpenSettingsFolder()
    {
        try
        {
            // /select highlights the file itself rather than just opening the folder
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{AmbilightConfig.Path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ProbeLog.Log("настройки", "не удалось открыть папку: " + ex.Message);
        }
    }

    void ExportConfig()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Loc.T("dialog.filter"),
            FileName = "ambilight-config.json"
        };
        if (dlg.ShowDialog() != true) return;

        try { _cfg.SaveTo(dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(Loc.T("dialog.saveFail") + ex.Message); }
    }

    void ImportConfig()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("dialog.filter") };
        if (dlg.ShowDialog() != true) return;

        try
        {
            // copy onto the existing instance - the engine holds this very object
            _cfg.CopyFrom(AmbilightConfig.LoadFrom(dlg.FileName));
            _cfg.Save();
            _saved = _cfg.Clone();

            Loc.Load(_cfg.Language);
            ProbeLog.Configure(AmbilightConfig.LogPath, _cfg.WriteLog);
            BuildSettings();
            Restart();

            MessageBox.Show(Loc.T("dialog.loaded"));
        }
        catch (Exception ex) { MessageBox.Show(Loc.T("dialog.loadFail") + ex.Message); }
    }

    // ---- preview ------------------------------------------------------------

    void RebuildPreview()
    {
        PreviewCanvas.Children.Clear();
        _previewShapes.Clear();
        _previewLabels.Clear();

        var zones = _engine.Zones;
        for (int i = 0; i < zones.Length; i++)
        {
            bool first = i == 0;
            var r = new Rectangle
            {
                Fill = Brushes.Black,
                // outline every cell, so the grid reads evenly instead of only where
                // neighbouring colours happen to differ
                Stroke = first ? Brushes.White : new SolidColorBrush(Color.FromRgb(90, 90, 100)),
                StrokeThickness = first ? 2 : 1
            };
            PreviewCanvas.Children.Add(r);
            _previewShapes.Add(r);
        }

        // numbers every ten, plus the first: enough to read direction at a glance
        for (int i = 0; i < zones.Length; i++)
        {
            if (i != 0 && (i + 1) % 10 != 0) continue;
            var t = new TextBlock
            {
                Text = (i + 1).ToString(),
                FontSize = 10,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                Padding = new Thickness(2, 0, 2, 0),
                Tag = i
            };
            PreviewCanvas.Children.Add(t);
            _previewLabels.Add(t);
        }

        _previewColors = new byte[zones.Length * 3];
        LayoutPreview();
    }

    /// <summary>Thickness of the preview bands, in pixels, independent of window size.</summary>
    const double PreviewBandPx = 46;

    void LayoutPreview()
    {
        double cw = PreviewCanvas.ActualWidth, ch = PreviewCanvas.ActualHeight;
        if (cw < 10 || ch < 10) return;

        // One fixed thickness for all four sides. On the real screen the depth is the same
        // number of pixels all round, so deriving it from each stretched axis made the side
        // bands look thicker or thinner than the top ones. Tying it to the canvas size did
        // not help either: the bands then grew with the window, which just looked wrong -
        // the preview shows strip order and colour, not the sampling depth to scale.
        const double band = PreviewBandPx;

        var zones = _engine.Zones;
        for (int i = 0; i < _previewShapes.Count && i < zones.Length; i++)
        {
            var z = zones[i];
            var r = _previewShapes[i];

            if (z.Side is Side.Top or Side.Bottom)
            {
                r.Width = Math.Max(2, (z.X1 - z.X0) * cw - 1);
                r.Height = band;
                Canvas.SetLeft(r, z.X0 * cw);
                Canvas.SetTop(r, z.Side == Side.Top ? 0 : ch - band);
            }
            else
            {
                r.Width = band;
                r.Height = Math.Max(2, (z.Y1 - z.Y0) * ch - 1);
                Canvas.SetLeft(r, z.Side == Side.Left ? 0 : cw - band);
                Canvas.SetTop(r, z.Y0 * ch);
            }
        }

        foreach (var t in _previewLabels)
        {
            int i = (int)t.Tag;
            if (i >= zones.Length) continue;
            var z = zones[i];
            var r = _previewShapes[i];

            double cx = Canvas.GetLeft(r) + r.Width / 2;
            double cy = Canvas.GetTop(r) + r.Height / 2;

            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, Math.Clamp(cx - t.DesiredSize.Width / 2, 0, Math.Max(0, cw - t.DesiredSize.Width)));
            Canvas.SetTop(t, Math.Clamp(cy - t.DesiredSize.Height / 2, 0, Math.Max(0, ch - t.DesiredSize.Height)));
        }
    }

    /// <summary>
    /// Preview-only brightening. Strip and monitor are different media: a value of 30 is
    /// plainly visible on a WS2812 in a dim room and nearly black on screen.
    /// </summary>
    static byte Boost(byte v) => (byte)Math.Clamp(Math.Pow(v / 255.0, 0.55) * 255.0, 0, 255);

    void RefreshUi()
    {
        if (_engine.LayoutVersion != _previewLayoutVersion)
        {
            _previewLayoutVersion = _engine.LayoutVersion;
            RebuildPreview();
        }

        if (_overlay != null && _engine.LayoutVersion != _overlayLayoutVersion)
        {
            _overlayLayoutVersion = _engine.LayoutVersion;
            _overlay.SetZones(_engine.Zones);
        }

        LayoutPreview();

        if (_previewColors.Length > 0)
        {
            _engine.CopyPreview(_previewColors);
            for (int i = 0; i < _previewShapes.Count; i++)
            {
                int o = i * 3;
                if (o + 2 >= _previewColors.Length) break;
                var c = _cfg.PreviewBoost
                    ? Color.FromRgb(Boost(_previewColors[o]), Boost(_previewColors[o + 1]), Boost(_previewColors[o + 2]))
                    : Color.FromRgb(_previewColors[o], _previewColors[o + 1], _previewColors[o + 2]);
                if (_previewShapes[i].Fill is SolidColorBrush sb && sb.Color == c) continue;
                _previewShapes[i].Fill = new SolidColorBrush(c);
            }
        }

        var cap = _engine.Capture;

        // rolls the 100 ms rate buckets. Without this every frame lands in one bucket and
        // the "per 5 s" figure becomes a running total - it read 1000 fps and climbing.
        cap?.Metrics.Tick();

        string capLine = cap == null ? Loc.T("stats.notrunning") : cap.SourceSplit();
        string activeNow = cap?.ActiveSource ?? "-";
        if (_cfg.CaptureMode == CaptureMode.Auto) activeNow += $" ({Loc.T("capture.autoSuffix")})";
        var snap = cap?.Metrics.Snapshot();

        StatsText.Text =
            $"{Loc.T("stats.monitor"),-9} {_engine.Monitor?.DisplayName ?? "?"}  {_engine.Monitor?.Width}x{_engine.Monitor?.Height}\n" +
            $"{Loc.T("stats.method"),-9} {activeNow}\n" +
            $"{Loc.T("stats.capture"),-9} {(snap?.FpsAvg5s ?? 0):F1}   p50 {(snap?.P50Ms ?? 0):F1} ms   p99 {(snap?.P99Ms ?? 0):F1} ms\n" +
            $"{Loc.T("stats.output"),-9} {_engine.OutputFps:F1}   {Loc.T("stats.sent")} {_engine.FramesSent}   {Loc.T("stats.skipped")} {_engine.FramesSkipped}\n" +
            $"{Loc.T("stats.port"),-9} {_engine.DeviceStatus}   {Loc.T("stats.reconnects")} {_engine.Reconnects}\n" +
            $"{Loc.T("stats.sources"),-9} {capLine}";

        var warnings = new List<string>();
        if (_engine.IsPaused) warnings.Add(string.Format(Loc.T("warn.paused"), _engine.PauseReason));
        if (_engine.DeviceStatus.Contains("ошибка") || _engine.DeviceStatus.Contains("denied"))
            warnings.Add(Loc.T("warn.port"));

        WarnText.Text = string.Join("\n", warnings);
        WarnText.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        DirtyBar.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;
        DirtyText.Text = Loc.T("unsaved");
        ApplyButton.Content = Loc.T("apply");
        CancelButton.Content = Loc.T("cancel");
    }

    // ---- small control helpers ---------------------------------------------

    /// <summary>
    /// Window.Resources only searches the window's own dictionary, so the brushes declared
    /// in App.xaml come back null through the indexer and text falls back to system black
    /// on our dark panels. FindResource walks up to the application.
    /// </summary>
    Brush Res(string key) => (Brush)FindResource(key);

    TextBlock Text(string text, bool dim = false, double size = 12) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Res(dim ? "FgDim" : "Fg"),
        TextWrapping = TextWrapping.Wrap
    };

    TextBlock Note(string text)
    {
        var t = Text(text, dim: true, size: 11);
        t.Margin = new Thickness(0, 0, 0, 8);
        return t;
    }

    void AddTab(string title, Action<StackPanel> fill)
    {
        var panel = new StackPanel();
        fill(panel);

        var body = new Border
        {
            Background = Res("Panel"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14),
            Child = panel
        };

        Tabs.Items.Add(new TabItem
        {
            Header = title,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(6),
                Content = body
            }
        });
    }

    StackPanel Labeled(string label, UIElement control)
    {
        var sp = new StackPanel();
        sp.Children.Add(Text(label, dim: true, size: 11));
        sp.Children.Add(control);
        return sp;
    }

    StackPanel Slider(string label, double value, double min, double max, double tick,
                      Action<double> onChange, Func<double, string>? format = null)
    {
        var text = Text("", size: 12);
        var slider = new System.Windows.Controls.Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickFrequency = tick,
            IsSnapToTickEnabled = true,
            Margin = new Thickness(0, 0, 0, 6)
        };

        void Show() => text.Text = label + ": " +
            (format != null ? format(slider.Value) : slider.Value.ToString("0.###"));
        Show();
        slider.ValueChanged += (_, _) =>
        {
            if (_rebuildingUi) return;
            onChange(slider.Value);
            Show();
            MarkDirty();
        };

        var sp = new StackPanel();
        sp.Children.Add(text);
        sp.Children.Add(slider);
        return sp;
    }

    StackPanel IntBox(string label, int value, Action<int> onChange)
    {
        var box = new TextBox { Text = value.ToString(), Margin = new Thickness(0, 2, 0, 8), Foreground = Brushes.Black };
        box.TextChanged += (_, _) =>
        {
            if (_rebuildingUi) return;
            if (int.TryParse(box.Text, out int v) && v >= 0) { onChange(v); MarkDirty(); }
        };
        return Labeled(label, box);
    }

    CheckBox Check(string label, bool value, Action<bool> onChange)
    {
        var cb = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Foreground = Res("Fg"),
            Margin = new Thickness(0, 3, 0, 3)
        };
        cb.Checked += (_, _) => { if (!_rebuildingUi) { onChange(true); MarkDirty(); } };
        cb.Unchecked += (_, _) => { if (!_rebuildingUi) { onChange(false); MarkDirty(); } };
        return cb;
    }
}
