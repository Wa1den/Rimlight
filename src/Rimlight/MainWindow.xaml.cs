using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Rimlight.Capture;
using Rimlight.Leds;
using Rimlight.Power;
using Rimlight.Text;

namespace Rimlight;

public partial class MainWindow : Window
{
    readonly RimlightConfig _cfg = RimlightConfig.Load();
    readonly RimlightEngine _engine = new();
    readonly PowerWatcher _power;
    readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(50) };

    readonly List<MonitorInfo> _monitors = Native.EnumerateMonitors();
    readonly List<Rectangle> _previewShapes = new();
    readonly List<Border> _previewLabels = new();
    System.Windows.Forms.NotifyIcon? _tray;
    byte[] _previewColors = Array.Empty<byte>();

    readonly List<UIElement> _pages = new();

    readonly TextBlock[] _statLabels = new TextBlock[9];
    readonly TextBlock[] _statValues = new TextBlock[9];

    /// <summary>Window width to come back to when the preview is switched on again.</summary>
    double _wideWidth;

    ComboBox _monitorBox = null!, _portBox = null!, _cornerBox = null!, _modeBox = null!, _langBox = null!;
    TextBlock _totalText = null!;
    StackPanel _offsetHost = null!;
    StackPanel _powerHost = null!;
    TextBlock _countNote = null!;
    TextBlock _cropStatus = null!;
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
    RimlightConfig _saved = null!;

    /// <summary>
    /// With "minimise to tray" on, the window's close button hides to the tray instead of
    /// quitting - the usual behaviour for background utilities. Exiting for real happens
    /// through the tray menu, which sets this first.
    /// </summary>
    bool _reallyClosing;

    public MainWindow()
    {
        InitializeComponent();

        ProbeLog.Configure(RimlightConfig.LogPath, _cfg.WriteLog);
        if (RimlightConfig.MigrationNote != null) ProbeLog.Log("config", RimlightConfig.MigrationNote);
        Loc.Configure(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(RimlightConfig.Path)!, "lang"));
        Loc.Load(_cfg.Language);

        Icon = LoadIcon();
        _saved = _cfg.Clone();

        // Settings written before the switch to the EDID model know only the device name.
        // Filling the model in now rather than at the next Apply: a cable can be moved
        // between ports before the user has any reason to press it. Written onto the
        // applied copy as well, which is what reaches disk on exit.
        if (_cfg.MonitorModel.Length == 0)
        {
            var known = ScreenChoice.Find(_monitors, _cfg.MonitorDeviceName, "");
            if (known != null && known.Model.Length > 0)
                _cfg.MonitorModel = _saved.MonitorModel = known.Model;
        }

        // The watcher only reports; deciding what counts as "nobody is looking" stays here,
        // because it is this application's settings that say so.
        _power = new PowerWatcher();
        _power.Changed += (_, state) =>
        {
            string? reason =
                state.Suspended && _cfg.OffOnSuspend ? Loc.P("сон", "sleep") :
                state.Locked && _cfg.OffOnLock ? Loc.P("блокировка", "locked") :
                state.DisplayOff && _cfg.OffOnDisplayOff ? Loc.P("экран выключен", "display off") :
                null;

            if (reason != null) _engine.Pause(reason);
            else _engine.Resume();
        };

        RestoreWindowGeometry();

        for (int i = 0; i < _statLabels.Length; i++)
        {
            StatsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Text("", dim: true, size: 12);
            label.Margin = new Thickness(0, 0, 18, 3);
            Grid.SetRow(label, i);
            var value = Text("", size: 12);
            value.Margin = new Thickness(0, 0, 0, 3);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            StatsGrid.Children.Add(label);
            StatsGrid.Children.Add(value);
            _statLabels[i] = label;
            _statValues[i] = value;
        }

        PreviewToggle.Checked += (_, _) => { if (_rebuildingUi) return; _cfg.ShowPreview = true; MarkDirty(); ApplyPreviewLayout(); };
        PreviewToggle.Unchecked += (_, _) => { if (_rebuildingUi) return; _cfg.ShowPreview = false; MarkDirty(); ApplyPreviewLayout(); };

        Nav.SelectionChanged += (_, _) =>
        {
            int i = Nav.SelectedIndex;
            if (i < 0 || i >= _pages.Count) return;
            PageHost.Content = _pages[i];
        };
        if (TryFindResource("AccentButtonStyle") is Style accent) ApplyButton.Style = accent;

        BuildSettings();

        // after BuildSettings: the compact width is added up from the section rail,
        // and the rail is empty until the sections are in it
        ApplyPreviewLayout();

        Loaded += (_, _) =>
        {
            _power.Attach(this);
            SetupTray();
            Restart();

            // the startup pass had to estimate the window frame; now it can be measured
            if (!_cfg.ShowPreview) ApplyPreviewLayout();

            if (_cfg.StartMinimized) WindowState = WindowState.Minimized;

            // after the engine, and not awaited: a slow answer from GitHub must not hold
            // up the strip lighting
            if (_cfg.CheckUpdates) _ = AnnounceUpdateAsync();
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

        // a Windows shutdown must not be cancelled into the tray
        Application.Current.SessionEnding += (_, _) => _reallyClosing = true;

        Closing += (_, e) =>
        {
            if (!_reallyClosing && _cfg.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }

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
        _wideWidth = Math.Max(MinWidth, _cfg.WindowWidth);
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

    /// <summary>
    /// The preview is the whole right side, so hiding it also narrows the window: the
    /// compact width is fixed and the wide one is remembered for the way back. Only the
    /// wide width is ever persisted.
    /// </summary>
    const double WideMinWidth = 1317;

    /// <summary>Width of the settings column, the same with the preview and without it.
    /// Declared in MainWindow.xaml as well, and the two have to stay in step.</summary>
    const double PageWidth = 430;

    /// <summary>
    /// Shows or hides the preview, and gives the window a width to match.
    ///
    /// Without the preview the width is fixed rather than sized to content: SizeToContent
    /// leaves the Width property holding a stale number, so returning to the wide size took
    /// two assignments to work at all, and it recomputes the height along with the width.
    /// A width of its own also means the window cannot be dragged wider into empty space.
    /// </summary>
    void ApplyPreviewLayout()
    {
        if (_cfg.ShowPreview)
        {
            RightColumn.Visibility = Visibility.Visible;
            MaxWidth = double.PositiveInfinity;
            MinWidth = WideMinWidth;
            if (IsLoaded && WindowState == WindowState.Normal)
                Width = Math.Max(WideMinWidth, _wideWidth);
            return;
        }

        // capture only on the actual transition out of wide mode: a repeat call while
        // already compact (Apply, Cancel, a language rebuild) would capture the
        // compact width and the window would come back too narrow
        if (RightColumn.Visibility == Visibility.Visible
            && IsLoaded && WindowState == WindowState.Normal && ActualWidth >= WideMinWidth)
            _wideWidth = ActualWidth;

        double narrow = NarrowWidth();

        RightColumn.Visibility = Visibility.Collapsed;
        MinWidth = 0;
        Width = narrow;
        MinWidth = MaxWidth = narrow;
    }

    /// <summary>
    /// What is left of the window once the preview is gone: the section rail, the settings
    /// page and the window frame. Added up rather than asked of the layout, because the
    /// point is a width that does not depend on what is written in the window.
    ///
    /// The rail is measured, not read: a language change rebuilds its captions, and before
    /// the window is shown nothing has been laid out at all. DesiredSize covers its margins
    /// either way. The page's own right margin is left out - there is no preview beside it
    /// to keep clear of, and the window frame leaves a gap there anyway.
    /// </summary>
    double NarrowWidth()
    {
        if (IsLoaded) UpdateLayout();
        else if (Rail.DesiredSize.Width <= 0)
            Rail.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // Before the window is shown there is no frame to measure, so the startup value is
        // an estimate; Loaded runs this again and replaces it with the real one.
        double frame = Content is FrameworkElement root && root.ActualWidth > 0
            ? ActualWidth - root.ActualWidth
            : 16;

        return Rail.DesiredSize.Width + PageWidth + frame;
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
            // in compact mode the actual width is the auto-fit one; keep the wide width
            _cfg.WindowWidth = _cfg.ShowPreview ? r.Width : _wideWidth;
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
            Text = "Rimlight",
            // present for the whole session, not just while minimised - otherwise the only
            // way back from the tray is to already know the app is running
            Visible = _cfg.MinimizeToTray
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("tray.show"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(Loc.T("main.exit"), null, (_, _) => { _reallyClosing = true; Close(); });
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
        int selected = Math.Max(0, Nav.SelectedIndex);   // survive a rebuild on the open section

        _rebuildingUi = true;
        Nav.Items.Clear();
        _pages.Clear();
        PreviewToggle.Content = Loc.T("nav.preview");
        PreviewToggle.IsChecked = _cfg.ShowPreview;    // guarded by _rebuildingUi

        AddTab(Loc.T("tab.main"), "", panel =>
        {
            _langBox = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
            foreach (var code in Loc.Available) _langBox.Items.Add(Loc.DisplayName(code));
            _langBox.SelectedIndex = Math.Max(0, Array.IndexOf(Loc.Available, Loc.Language));
            _langBox.SelectionChanged += (_, _) =>
            {
                if (_rebuildingUi) return;
                _cfg.Language = Loc.Available[Math.Max(0, _langBox.SelectedIndex)];
                _dirty = true;
                Loc.Configure(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(RimlightConfig.Path)!, "lang"));
        Loc.Load(_cfg.Language);
                BuildSettings();          // the whole panel is built in code, so rebuild it
            };
            panel.Children.Add(Labeled(Loc.T("main.language"), _langBox, Loc.T("main.language.note")));

            // the registry is the real state; mirror it so a stale stored flag cannot make
            // Cancel silently switch autostart back on or off
            _cfg.Autostart = Autostart.IsEnabled();
            panel.Children.Add(Check(Loc.T("main.autostart"), _cfg.Autostart, v =>
            {
                _cfg.Autostart = v;
                Autostart.Set(v);
            }));

            // starting minimised only makes sense together with the tray, so the option
            // follows the tray checkbox
            CheckBox startMin = null!;
            panel.Children.Add(Check(Loc.T("main.tray"), _cfg.MinimizeToTray, v =>
            {
                _cfg.MinimizeToTray = v;
                if (_tray != null) _tray.Visible = v;
                startMin.IsEnabled = v;
                if (!v) startMin.IsChecked = false;
            }));
            startMin = (CheckBox)Check(Loc.T("main.startmin"), _cfg.StartMinimized, v => _cfg.StartMinimized = v);
            startMin.IsEnabled = _cfg.MinimizeToTray;
            panel.Children.Add(startMin);

            panel.Children.Add(Check(Loc.T("main.boost"), _cfg.PreviewBoost, v => _cfg.PreviewBoost = v,
                Loc.T("main.boost.note")));
            var diagHead = Text(Loc.T("main.diag.head"));
            diagHead.FontWeight = FontWeights.Bold;
            diagHead.Margin = new Thickness(0, 14, 0, 6);
            panel.Children.Add(diagHead);

            // detail follows the block it belongs to, the way "start minimised" follows
            // the tray checkbox
            CheckBox detailed = null!;
            panel.Children.Add(Check(Loc.T("main.stats"), _cfg.ShowStats, v =>
            {
                _cfg.ShowStats = v;
                detailed.IsEnabled = v;
                if (!v) detailed.IsChecked = false;
            }, Loc.T("main.stats.note")));

            panel.Children.Add(Check(Loc.T("main.stats.detailed"), _cfg.DetailedStats,
                v => _cfg.DetailedStats = v, Loc.T("main.stats.detailed.note"), out detailed));
            detailed.IsEnabled = _cfg.ShowStats;

            panel.Children.Add(Check(Loc.T("main.log"), _cfg.WriteLog, v =>
            {
                _cfg.WriteLog = v;
                ProbeLog.Configure(RimlightConfig.LogPath, v);
            }, Loc.T("main.log.note")));

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            var exportBtn = new Button { Content = Loc.T("main.export"), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 0) };
            exportBtn.Click += (_, _) => ExportConfig();
            var importBtn = new Button { Content = Loc.T("main.import"), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 0) };
            importBtn.Click += (_, _) => ImportConfig();
            var resetBtn = new Button { Content = Loc.T("main.reset"), Padding = new Thickness(8, 4, 8, 4) };
            resetBtn.Click += (_, _) => ResetConfig();
            row.Children.Add(exportBtn);
            row.Children.Add(importBtn);
            row.Children.Add(resetBtn);
            row.Children.Add(HelpIcon(Loc.T("main.reset.note")));
            panel.Children.Add(row);

            var pathText = Text("", dim: true);
            var link = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run(RimlightConfig.Path));
            StyleLink(link);
            link.Click += (_, _) => OpenSettingsFolder();
            pathText.Inlines.Add(Loc.T("main.paths").Replace("{0}", "").TrimEnd());
            pathText.Inlines.Add(" ");
            pathText.Inlines.Add(link);
            pathText.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(pathText);
        });

        AddTab(Loc.T("tab.device"), "", panel =>
        {
            _monitorBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
            foreach (var m in _monitors) _monitorBox.Items.Add(m.ToString());
            var chosen = ScreenChoice.Find(_monitors, _cfg.MonitorDeviceName, _cfg.MonitorModel);
            _monitorBox.SelectedIndex = chosen == null ? 0 : Math.Max(0, _monitors.IndexOf(chosen));
            // Without this the screen was applied live and then lost: only the applied
            // copy of the settings reaches disk, and nothing marked the choice as an edit.
            _monitorBox.SelectionChanged += (_, _) => MarkDirty();
            panel.Children.Add(Labeled(Loc.T("device.monitor"), _monitorBox));

            _portBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8), IsEditable = true, Text = _cfg.PortName };
            foreach (var p in SerialPort.GetPortNames()) _portBox.Items.Add(p);
            panel.Children.Add(Labeled(Loc.T("device.port"), _portBox));

            panel.Children.Add(IntBox(Loc.T("device.baud"), _cfg.BaudRate, v => _cfg.BaudRate = v,
                Loc.T("device.baud.note")));

            var apply = new Button { Content = Loc.T("device.apply"), Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(8, 5, 8, 5) };
            apply.Click += (_, _) => Restart();
            panel.Children.Add(apply);
        });

        AddTab(Loc.T("tab.layout"), "", panel =>
        {
            panel.Children.Add(IntBox(Loc.T("layout.top"), _cfg.TopCount, v => { _cfg.TopCount = v; CountChanged(); }));
            panel.Children.Add(IntBox(Loc.T("layout.bottom"), _cfg.BottomCount, v => { _cfg.BottomCount = v; CountChanged(); }));
            panel.Children.Add(IntBox(Loc.T("layout.left"), _cfg.LeftCount, v => { _cfg.LeftCount = v; CountChanged(); }));
            panel.Children.Add(IntBox(Loc.T("layout.right"), _cfg.RightCount, v => { _cfg.RightCount = v; CountChanged(); }));

            _cornerBox = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
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
                v => { _cfg.EdgeMarginPercent = v; _engine.RequestRelayout(); }, help: Loc.T("layout.note")));
            panel.Children.Add(Slider(Loc.T("layout.marginV"), _cfg.EdgeMarginPercentV, 0, 15, 0.1,
                v => { _cfg.EdgeMarginPercentV = v; _engine.RequestRelayout(); }, help: Loc.T("layout.note")));
            panel.Children.Add(Slider(Loc.T("layout.depth"), _cfg.DepthPercent, 1, 25, 0.5,
                v => { _cfg.DepthPercent = v; _engine.RequestRelayout(); }, help: Loc.T("layout.note")));

            var totalRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            _totalText = Text("");
            _totalText.FontWeight = FontWeights.Bold;
            totalRow.Children.Add(_totalText);
            var totalHelp = HelpIcon("");
            _countNote = (TextBlock)totalHelp.ToolTip;    // firmware warning, text set in UpdateTotal
            totalRow.Children.Add(totalHelp);
            panel.Children.Add(totalRow);
            UpdateTotal();

            // belongs next to the counters it talks about, not in the status panel where it
            // read as a permanent alarm
            var overlayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            _overlayButton = new Button
            {
                Content = Loc.T(_overlay != null ? "layout.overlay.hide" : "layout.overlay.show"),
                Padding = new Thickness(8, 5, 8, 5)
            };
            _overlayButton.Click += (_, _) => ToggleOverlay();
            overlayRow.Children.Add(_overlayButton);
            overlayRow.Children.Add(HelpIcon(Loc.T("layout.overlay.note")));
            panel.Children.Add(overlayRow);
        });

        AddTab(Loc.T("tab.crop"), "", panel =>
        {
            panel.Children.Add(Note(Loc.T("crop.head")));

            // everything below the main switch follows it, the way "start minimised"
            // follows the tray checkbox
            var tuning = new List<UIElement>();
            void Tune(UIElement e) { tuning.Add(e); panel.Children.Add(e); }

            panel.Children.Add(Check(Loc.T("crop.enable"), _cfg.AdaptiveCrop, v =>
            {
                _cfg.AdaptiveCrop = v;
                foreach (var e in tuning) e.IsEnabled = v;
            }, Loc.T("crop.enable.note")));

            _cropStatus = Text("", dim: true);
            _cropStatus.Margin = new Thickness(0, 2, 0, 10);
            panel.Children.Add(_cropStatus);

            Tune(Check(Loc.T("crop.vertical"), _cfg.CropVertical,
                v => _cfg.CropVertical = v, Loc.T("crop.vertical.note")));
            Tune(Check(Loc.T("crop.horizontal"), _cfg.CropHorizontal,
                v => _cfg.CropHorizontal = v, Loc.T("crop.horizontal.note")));

            // the mapping changes without the bars having moved, so the zones have to be
            // rebuilt by hand - the detector itself has nothing new to report
            Tune(Check(Loc.T("crop.stretch"), _cfg.CropStretch,
                v => { _cfg.CropStretch = v; _engine.RequestRelayout(); }, Loc.T("crop.stretch.note")));

            Tune(Slider(Loc.T("crop.min"), _cfg.CropMinPercent, 0, 10, 0.5,
                v => _cfg.CropMinPercent = v, v => v.ToString("0.#"), Loc.T("crop.min.note")));
            Tune(Slider(Loc.T("crop.max"), _cfg.CropMaxPercent, 5, 40, 1,
                v => _cfg.CropMaxPercent = v, v => v.ToString("0"), Loc.T("crop.max.note")));
            Tune(Slider(Loc.T("crop.level"), _cfg.CropBlackLevel, 0, 48, 1,
                v => _cfg.CropBlackLevel = (int)v, v => v.ToString("0"), Loc.T("crop.level.note")));
            Tune(Slider(Loc.T("crop.overlook"), _cfg.CropOverlookPercent, 0, 10, 0.5,
                v => _cfg.CropOverlookPercent = v, v => v.ToString("0.#"), Loc.T("crop.overlook.note")));
            Tune(Slider(Loc.T("crop.hold"), _cfg.CropHoldMs / 1000.0, 0.1, 3.0, 0.05,
                v => _cfg.CropHoldMs = v * 1000.0, v => v.ToString("0.00"), Loc.T("crop.hold.note")));
            Tune(Slider(Loc.T("crop.inset"), _cfg.CropInsetPercent, 0, 3, 0.1,
                v => _cfg.CropInsetPercent = v,
                v => v <= 0 ? Loc.T("off") : v.ToString("0.0"), Loc.T("crop.inset.note")));

            foreach (var e in tuning) e.IsEnabled = _cfg.AdaptiveCrop;
        });

        // Brightness is kept apart from colour: how much light the strip makes is a
        // different question from what colour it makes, and these are the settings reached
        // for when the room is dark rather than when the wall is the wrong shade. The keys
        // keep their "color." prefix - renaming them would silently drop the matching
        // lines out of anyone's own translation file.
        AddTab(Loc.T("tab.brightness"), "", panel =>
        {
            panel.Children.Add(Slider(Loc.T("color.brightness"), _cfg.MaxBrightness, 0, 1, 0.01,
                v => _cfg.MaxBrightness = v, help: Loc.T("color.brightness.note")));

            // cubic response: the useful range is the bottom few percent, and a linear
            // slider spends nearly all its travel on values that just black the strip out
            panel.Children.Add(Slider(Loc.T("color.minluma"), Math.Pow(_cfg.MinLuma / 0.3, 1.0 / 3.0), 0, 1, 0.005,
                v => _cfg.MinLuma = Math.Pow(v, 3) * 0.3,
                v => v <= 0 ? Loc.T("off") : (Math.Pow(v, 3) * 0.3).ToString("0.0000"),
                Loc.T("color.minluma.note")));

            panel.Children.Add(Slider(Loc.T("color.shadow"), _cfg.ShadowNeutral, 0, 0.4, 0.01,
                v => _cfg.ShadowNeutral = v,
                v => v <= 0 ? Loc.T("off") : v.ToString("0.00"),
                Loc.T("color.shadow.note")));

            panel.Children.Add(Slider(Loc.T("color.backlight"), _cfg.MinBacklight, 0, 0.25, 0.005,
                v => _cfg.MinBacklight = v,
                v => v <= 0 ? Loc.T("off") : (v * 255).ToString("0"),
                Loc.T("color.backlight.note")));
        });

        AddTab(Loc.T("tab.color"), "", panel =>
        {
            panel.Children.Add(Slider(Loc.T("color.saturation"), _cfg.Saturation, 0, 2.5, 0.05, v => _cfg.Saturation = v));
            panel.Children.Add(Slider(Loc.T("color.gamma"), _cfg.Gamma, 1.0, 3.5, 0.05, v => _cfg.Gamma = v));
            panel.Children.Add(Slider(Loc.T("color.temperature"), _cfg.TemperatureK, 2000, 10000, 100, v => _cfg.TemperatureK = (int)v));
            panel.Children.Add(Slider(Loc.T("color.gainR"), _cfg.GainR, 0, 2, 0.01, v => _cfg.GainR = v));
            panel.Children.Add(Slider(Loc.T("color.gainG"), _cfg.GainG, 0, 2, 0.01, v => _cfg.GainG = v));
            panel.Children.Add(Slider(Loc.T("color.gainB"), _cfg.GainB, 0, 2, 0.01, v => _cfg.GainB = v));

            panel.Children.Add(Check(Loc.T("color.dither"), _cfg.Dithering, v => _cfg.Dithering = v,
                Loc.T("color.dither.note")));

            panel.Children.Add(Slider(Loc.T("color.rise"), _cfg.SmoothingRise, 0.02, 1, 0.01,
                v => _cfg.SmoothingRise = v, help: Loc.T("color.rise.note")));
            panel.Children.Add(Slider(Loc.T("color.fall"), _cfg.SmoothingFall, 0.02, 1, 0.01,
                v => _cfg.SmoothingFall = v, help: Loc.T("color.fall.note")));
        });

        AddTab(Loc.T("tab.capture"), "", panel =>
        {
            _modeBox = new ComboBox { Margin = new Thickness(0, 2, 0, 4) };
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
            panel.Children.Add(Labeled(Loc.T("capture.method"), _modeBox, Loc.T("capture.method.note")));

            // The top of the scale is "no limit" rather than a number, and it is where the
            // slider starts out. Above about 135 fps there is nothing left to cap anyway -
            // the controller's own cycle is already the floor - and the cap costs latency,
            // because a frame arriving inside its window is dropped rather than held.
            const double fpsFree = 145;
            panel.Children.Add(Slider(Loc.T("capture.fps"), _cfg.MaxFps <= 0 ? fpsFree : _cfg.MaxFps,
                10, fpsFree, 1,
                v => _cfg.MaxFps = v >= fpsFree ? 0 : (int)v,
                v => v >= fpsFree ? Loc.T("capture.fps.free") : v.ToString("0"),
                Loc.T("capture.fps.note")));

            panel.Children.Add(Check(Loc.T("capture.onchange"), _cfg.SendOnlyOnChange, v => _cfg.SendOnlyOnChange = v,
                Loc.T("capture.onchange.note")));

            // Takes effect on the next output tick without restarting the engine, so no
            // port reopen and no bootloader pause for a checkbox.
            panel.Children.Add(Check(Loc.T("capture.publish"), _cfg.PublishFrames, v => _cfg.PublishFrames = v,
                Loc.T("capture.publish.note")));
        });

        AddTab(Loc.T("tab.power"), "", panel =>
        {
            var head = Text(Loc.T("power.head"));
            head.FontWeight = FontWeights.Bold;
            head.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(head);

            panel.Children.Add(Check(Loc.T("power.exit"), _cfg.OffOnExit, v => _cfg.OffOnExit = v));
            panel.Children.Add(Check(Loc.T("power.display"), _cfg.OffOnDisplayOff, v => _cfg.OffOnDisplayOff = v));
            panel.Children.Add(Check(Loc.T("power.lock"), _cfg.OffOnLock, v => _cfg.OffOnLock = v));
            panel.Children.Add(Check(Loc.T("power.suspend"), _cfg.OffOnSuspend, v => _cfg.OffOnSuspend = v));

            var supply = Text(Loc.T("power.supply"));
            supply.FontWeight = FontWeights.Bold;
            supply.Margin = new Thickness(0, 14, 0, 6);
            panel.Children.Add(supply);

            // the travel depends on how many LEDs there are, so it is rebuilt with the
            // count rather than only at startup - the same arrangement as the offset
            _powerHost = new StackPanel();
            panel.Children.Add(_powerHost);
            RebuildPowerSlider();
        });

        AddTab(Loc.T("tab.about"), "", panel =>
        {
            var head = Text(Loc.T("about.head"));
            head.FontWeight = FontWeights.Bold;
            head.Margin = new Thickness(0, 0, 0, 2);
            panel.Children.Add(head);

            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            var verText = Text(string.Format(Loc.T("about.version"),
                $"{ver?.Major ?? 1}.{ver?.Minor ?? 0}.{ver?.Build ?? 0}"), dim: true);
            verText.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Add(verText);

            panel.Children.Add(Check(Loc.T("about.updates"), _cfg.CheckUpdates,
                v => _cfg.CheckUpdates = v, Loc.T("about.updates.note")));

            panel.Children.Add(Note(Loc.T("about.text")));
            panel.Children.Add(Note(Loc.T("about.text2")));

            panel.Children.Add(LinkLine(Loc.T("about.repo"),
                "https://github.com/Wa1den/Rimlight"));
            panel.Children.Add(LinkLine(Loc.T("about.firmware"),
                "https://github.com/AlexGyver/Arduino_Ambilight"));
        });

        _rebuildingUi = false;
        Nav.SelectedIndex = Math.Min(selected, Nav.Items.Count - 1);
        ApplyPreviewLayout();    // rebuild paths include Cancel and Import, which may flip it
    }

    /// <summary>
    /// What the detector currently sees, live. Without it the settings are guesswork: the
    /// numbers only mean something against the material actually on screen, and the strip
    /// alone does not say whether a bar was found or merely suspected.
    /// </summary>
    void UpdateCropStatus()
    {
        if (_cropStatus == null) return;

        if (!_cfg.AdaptiveCrop)
        {
            _cropStatus.Text = Loc.T("crop.status.off");
            return;
        }

        var r = _engine.Crop;
        bool v = r.Y0 > 0.001, h = r.X0 > 0.001;

        // whole sentences per case rather than one assembled from pieces: word order is
        // not the same in every language, and a translator cannot change it in a join
        _cropStatus.Text =
            v && h ? string.Format(Loc.T("crop.status.both"), r.Y0 * 100, r.X0 * 100) :
            v ? string.Format(Loc.T("crop.status.v"), r.Y0 * 100) :
            h ? string.Format(Loc.T("crop.status.h"), r.X0 * 100) :
            Loc.T("crop.status.none");
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
        RebuildPowerSlider();
    }

    /// <summary>
    /// The ceiling is set in amperes, and how many amperes the strip can possibly draw is
    /// the LED count times what one LED costs - so the whole travel of the slider moves
    /// when the count does, and the top of it means "no ceiling" only because past that
    /// figure there is nothing left to limit.
    /// </summary>
    void RebuildPowerSlider()
    {
        if (_powerHost == null) return;

        double full = Math.Max(0.6, Math.Ceiling(_cfg.FullWhiteAmps * 10) / 10.0);
        double value = _cfg.PowerLimitAmps <= 0 ? full : Math.Clamp(_cfg.PowerLimitAmps, 0.5, full);

        _powerHost.Children.Clear();
        _powerHost.Children.Add(Slider(Loc.T("color.power"), value, 0.5, full, 0.1,
            v => _cfg.PowerLimitAmps = v >= full ? 0 : v,
            v => v >= full
                ? Loc.T("off")
                : string.Format(Loc.T("color.power.value"), v, full),
            string.Format(Loc.T("color.power.note"), _cfg.TotalLeds, _cfg.FullWhiteAmps)));
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
        {
            // модель из EDID - основной признак, имя устройства только различает
            // два одинаковых экрана
            var chosen = _monitors[_monitorBox.SelectedIndex];
            _cfg.MonitorDeviceName = chosen.DeviceName;
            _cfg.MonitorModel = chosen.Model;
        }

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
        _cfg.CopyFrom(_saved);
        _dirty = false;

        Loc.Configure(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(RimlightConfig.Path)!, "lang"));
        Loc.Load(_cfg.Language);
        ProbeLog.Configure(RimlightConfig.LogPath, _cfg.WriteLog);
        Autostart.Set(_cfg.Autostart);

        BuildSettings();      // restores the open section itself

        _engine.RequestRelayout();
        _engine.RestartCapture();
    }

    // ---- import / export ----------------------------------------------------

    /// <summary>A caption followed by a clickable address.</summary>
    TextBlock LinkLine(string caption, string url)
    {
        var t = Text("", dim: true);
        t.Margin = new Thickness(0, 6, 0, 0);
        t.Inlines.Add(caption + " ");

        var link = new System.Windows.Documents.Hyperlink(
            new System.Windows.Documents.Run(url));
        StyleLink(link);
        link.Click += (_, _) => OpenUrl(url);
        t.Inlines.Add(link);
        return t;
    }

    static void OpenUrl(string url)
    {
        try
        {
            // UseShellExecute hands it to the default browser; without it .NET tries to
            // execute the address as a program
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ProbeLog.Log("ссылка", "не удалось открыть " + url + ": " + ex.Message);
        }
    }

    static void OpenSettingsFolder()
    {
        try
        {
            // /select highlights the file itself rather than just opening the folder
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{RimlightConfig.Path}\"",
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
            _cfg.CopyFrom(RimlightConfig.LoadFrom(dlg.FileName));
            _cfg.Save();
            _saved = _cfg.Clone();

            Loc.Configure(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(RimlightConfig.Path)!, "lang"));
        Loc.Load(_cfg.Language);
            ProbeLog.Configure(RimlightConfig.LogPath, _cfg.WriteLog);
            BuildSettings();
            Restart();

            MessageBox.Show(Loc.T("dialog.loaded"));
        }
        catch (Exception ex) { MessageBox.Show(Loc.T("dialog.loadFail") + ex.Message); }
    }

    /// <summary>
    /// Applied live like any other edit rather than written straight to disk: a reset is a
    /// large change to look at, and the Cancel button has to be able to take it back.
    /// </summary>
    void ResetConfig()
    {
        if (MessageBox.Show(Loc.T("dialog.reset"), Loc.T("main.reset"),
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _cfg.ResetToDefaults();

        ProbeLog.Configure(RimlightConfig.LogPath, _cfg.WriteLog);
        Autostart.Set(_cfg.Autostart);

        BuildSettings();
        ApplyPreviewLayout();
        MarkDirty();

        // the zones themselves are untouched, but the crop settings above them are not
        _engine.RequestRelayout();
    }

    // ---- updates ------------------------------------------------------------

    string? _updateUrl;

    /// <summary>
    /// Says once, on the way in, that a newer release exists. Silent otherwise - including
    /// when the check itself failed, which is not news the user asked for.
    ///
    /// Started from the dispatcher and never configured away from it, so the continuation
    /// after the request comes back on the UI thread and can touch the window directly.
    /// </summary>
    async System.Threading.Tasks.Task AnnounceUpdateAsync()
    {
        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                      ?? new Version(1, 0, 0);

        var found = await UpdateCheck.FindNewerAsync(current);
        if (found == null) return;

        _updateUrl = found.Value.Url;
        string text = string.Format(Loc.T("update.available"), found.Value.Version.ToString(3));
        ProbeLog.Log(Loc.P("обновление", "update"), text);

        // With the tray in use the window may not even be on screen, so the notice has to
        // leave the window; without it there is nothing in the tray to speak from.
        if (_cfg.MinimizeToTray && _tray != null)
        {
            _tray.BalloonTipClicked -= OnUpdateBalloonClicked;
            _tray.BalloonTipClicked += OnUpdateBalloonClicked;
            _tray.ShowBalloonTip(10000, "Rimlight", text, System.Windows.Forms.ToolTipIcon.Info);
            return;
        }

        UpdateText.Inlines.Clear();
        UpdateText.Inlines.Add(text + " ");

        var link = new System.Windows.Documents.Hyperlink(
            new System.Windows.Documents.Run(Loc.T("update.open")));
        StyleLink(link);
        link.Click += (_, _) => OpenUpdatePage();
        UpdateText.Inlines.Add(link);

        UpdateCard.Visibility = Visibility.Visible;
    }

    void OnUpdateBalloonClicked(object? sender, EventArgs e) => OpenUpdatePage();

    void OpenUpdatePage()
    {
        if (_updateUrl != null) OpenUrl(_updateUrl);
    }

    // ---- preview ------------------------------------------------------------

    void RebuildPreview()
    {
        PreviewCanvas.Children.Clear();
        _previewShapes.Clear();
        _previewLabels.Clear();

        // a soft theme-coloured outline keeps the grid readable while the cells are dark;
        // the first LED is rung in the accent colour instead of shouting in white
        var accent = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.White;

        var zones = _engine.Zones;
        for (int i = 0; i < zones.Length; i++)
        {
            bool first = i == 0;
            var r = new Rectangle
            {
                Fill = Brushes.Black,
                RadiusX = 3,
                RadiusY = 3,
                Stroke = first ? accent : Res("PanelStroke"),
                StrokeThickness = first ? 2 : 1
            };
            PreviewCanvas.Children.Add(r);
            _previewShapes.Add(r);
        }

        // numbers every ten, plus the first: enough to read direction at a glance
        for (int i = 0; i < zones.Length; i++)
        {
            if (i != 0 && (i + 1) % 10 != 0) continue;
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(5, 1, 5, 1),
                Tag = i,
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 10,
                    Foreground = Brushes.White
                }
            };
            PreviewCanvas.Children.Add(chip);
            _previewLabels.Add(chip);
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
                r.Width = Math.Max(2, (z.X1 - z.X0) * cw - 2);
                r.Height = band;
                Canvas.SetLeft(r, z.X0 * cw);
                Canvas.SetTop(r, z.Side == Side.Top ? 0 : ch - band);
            }
            else
            {
                r.Width = band;
                r.Height = Math.Max(2, (z.Y1 - z.Y0) * ch - 2);
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

        _statLabels[0].Text = Loc.T("stats.monitor") + ":";
        _statLabels[1].Text = Loc.T("stats.method") + ":";
        _statLabels[2].Text = Loc.T("stats.capture") + ":";
        _statLabels[3].Text = Loc.T("stats.output") + ":";
        _statLabels[4].Text = Loc.T("stats.port") + ":";
        // everything past the port row is diagnostic. The rows are Auto height, so
        // collapsing the text collapses the row with it.
        var detail = _cfg.DetailedStats ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 5; i < _statLabels.Length; i++)
        {
            _statLabels[i].Visibility = detail;
            _statValues[i].Visibility = detail;
        }

        if (_cfg.DetailedStats)
        {
            _statLabels[5].Text = Loc.T("stats.latency") + ":";
            _statLabels[6].Text = Loc.T("stats.stages") + ":";
            _statLabels[7].Text = Loc.T("stats.sources") + ":";
            _statLabels[8].Text = Loc.T("stats.current") + ":";
        }

        _statValues[0].Text = $"{_engine.Monitor?.DisplayName ?? "?"}; {_engine.Monitor?.Width}x{_engine.Monitor?.Height}";
        _statValues[1].Text = activeNow;
        _statValues[2].Text = $"{(snap?.FpsAvg5s ?? 0):F1} fps; p50 {(snap?.P50Ms ?? 0):F1} ms; p99 {(snap?.P99Ms ?? 0):F1} ms";
        _statValues[3].Text = $"{_engine.OutputFps:F1} fps; {Loc.T("stats.sent")} {_engine.FramesSent}; {Loc.T("stats.skipped")} {_engine.FramesSkipped}";
        _statValues[4].Text = $"{_engine.DeviceStatus}; {Loc.T("stats.reconnects")} {_engine.Reconnects}";
        // end to end: from the moment the compositor put the picture on screen to the
        // moment its colours went out of the port
        if (_cfg.DetailedStats)
        {
            _statValues[5].Text = $"{_engine.FrameAgeMs:F1} " + Loc.T("stats.ms") +
                                  $"; p99 {_engine.FrameAgeP99Ms:F1}" +
                                  $"; {Loc.T("stats.worst")} {_engine.FrameAgeMaxMs:F1}" +
                                  $"; {Loc.T("stats.dropped")} {Loc.T("stats.drop.queue")} {_engine.FramesQueueFull}" +
                                  $", {Loc.T("stats.drop.rate")} {_engine.FramesTooSoon}";
            _statValues[6].Text = $"{Loc.T("stats.stage.grab")} {_engine.StageGrabMs:F1}" +
                                  $"; {Loc.T("stats.stage.reduce")} {_engine.StageReduceMs:F1}" +
                                  $"; {Loc.T("stats.stage.relay")} {_engine.StageRelayMs:F1}" +
                                  $"; {Loc.T("stats.stage.out")} {_engine.StageOutMs:F1}";
            _statValues[7].Text = capLine;

            // against the ceiling in the same line, because the only question this answers
            // is whether the ceiling is doing anything
            double amps = _cfg.TotalLeds * (RimlightConfig.AmpsPerLedIdle +
                                            RimlightConfig.AmpsPerLedWhite * _engine.MeanDuty);
            _statValues[8].Text = _cfg.PowerLimitAmps > 0
                ? string.Format(Loc.T("stats.current.limited"), amps, _cfg.FullWhiteAmps, _cfg.PowerLimitAmps)
                : string.Format(Loc.T("stats.current.free"), amps, _cfg.FullWhiteAmps);
        }

        // the toggle applies live; the block only exists while the preview column does
        StatsCard.Visibility = _cfg.ShowStats ? Visibility.Visible : Visibility.Collapsed;

        UpdateCropStatus();

        var warnings = new List<string>();
        if (_engine.IsPaused) warnings.Add(string.Format(Loc.T("warn.paused"), _engine.PauseReason));
        if (_engine.DeviceHasError) warnings.Add(Loc.T("warn.port"));

        WarnText.Text = string.Join("\n", warnings);
        WarnCard.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

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

    TextBlock Text(string text, bool dim = false, double size = 14) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Res(dim ? "FgDim" : "Fg"),
        TextWrapping = TextWrapping.Wrap
    };

    TextBlock Note(string text)
    {
        var t = Text(text, dim: true);
        t.Margin = new Thickness(0, 0, 0, 8);
        return t;
    }

    /// <summary>
    /// The stock WPF hyperlink is web-blue with a red hover, both hardcoded in its default
    /// style and neither readable on the dark theme. A local foreground wins over the
    /// style triggers, so links stay in the theme's accent text colour.
    /// </summary>
    static void StyleLink(System.Windows.Documents.Hyperlink link) =>
        link.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,
            "AccentTextFillColorPrimaryBrush");

    /// <summary>A question-mark glyph revealing the explanation on hover.</summary>
    TextBlock HelpIcon(string text)
    {
        var icon = new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)FindResource("Icons"),
            FontSize = 12,
            Foreground = Res("FgDim"),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Help,
            // the popup inherits the icon font, which has no letters - be explicit
            ToolTip = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12
            }
        };
        ToolTipService.SetInitialShowDelay(icon, 200);
        ToolTipService.SetShowDuration(icon, 60000);
        return icon;
    }

    void AddTab(string title, string glyph, Action<StackPanel> fill)
    {
        var panel = new StackPanel();
        fill(panel);

        var body = new Border
        {
            Style = (Style)FindResource("Card"),
            Child = panel
        };

        _pages.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = (FontFamily)FindResource("Icons"),
            FontSize = 16,
            Foreground = Res("Fg"),
            VerticalAlignment = VerticalAlignment.Center
        });
        var label = Text(title);
        label.Margin = new Thickness(12, 0, 0, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        Nav.Items.Add(new ListBoxItem { Content = row });
    }

    StackPanel Labeled(string label, UIElement control, string? help = null)
    {
        var sp = new StackPanel();
        var caption = Text(label, dim: true);
        if (help == null) sp.Children.Add(caption);
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(caption);
            row.Children.Add(HelpIcon(help));
            sp.Children.Add(row);
        }
        sp.Children.Add(control);
        return sp;
    }

    StackPanel Slider(string label, double value, double min, double max, double tick,
                      Action<double> onChange, Func<double, string>? format = null,
                      string? help = null)
    {
        var text = Text("");
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
        if (help == null) sp.Children.Add(text);
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(text);
            row.Children.Add(HelpIcon(help));
            sp.Children.Add(row);
        }
        sp.Children.Add(slider);
        return sp;
    }

    StackPanel IntBox(string label, int value, Action<int> onChange, string? help = null)
    {
        var box = new TextBox { Text = value.ToString(), Margin = new Thickness(0, 2, 0, 8) };
        box.TextChanged += (_, _) =>
        {
            if (_rebuildingUi) return;
            if (int.TryParse(box.Text, out int v) && v >= 0) { onChange(v); MarkDirty(); }
        };
        return Labeled(label, box, help);
    }

    /// <summary>
    /// Same, handing back the box itself - for options that enable one another, where the
    /// help text turns the return value into the row rather than the checkbox.
    /// </summary>
    UIElement Check(string label, bool value, Action<bool> onChange, string? help, out CheckBox box)
    {
        var element = Check(label, value, onChange, help);
        box = element as CheckBox ?? (CheckBox)((StackPanel)element).Children[0];
        return element;
    }

    UIElement Check(string label, bool value, Action<bool> onChange, string? help = null)
    {
        var cb = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Foreground = Res("Fg"),
            Margin = new Thickness(0, 3, 0, 3),
            // the Fluent style reserves 120px; a short label would push its help icon
            // far to the right of the text
            MinWidth = 0
        };
        cb.Checked += (_, _) => { if (!_rebuildingUi) { onChange(true); MarkDirty(); } };
        cb.Unchecked += (_, _) => { if (!_rebuildingUi) { onChange(false); MarkDirty(); } };
        if (help == null) return cb;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(cb);
        row.Children.Add(HelpIcon(help));
        return row;
    }
}
