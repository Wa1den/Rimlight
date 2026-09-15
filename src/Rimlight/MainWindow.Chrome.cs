using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Rimlight;

/// <summary>
/// The window frame: the content extended over the title bar, on a backdrop material drawn
/// by the system.
///
/// The caption buttons are left to DWM rather than drawn here. Buttons of our own would
/// have to answer WM_NCHITTEST by hand for the snap layouts flyout to appear over
/// maximise, which WPF still does not do for them (dotnet/wpf#4825); the system buttons
/// bring the flyout, the hover states and the dark theme glyphs along.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Height of the title bar row, declared in MainWindow.xaml as well. The system caption
    /// buttons are 30 tall at 100 % and stay at its top edge, whatever height the row is.
    /// </summary>
    const double TitleHeight = 48;

    /// <summary>Space left between the preview switches and the caption buttons.</summary>
    const double CaptionGap = 8;

    WindowChrome _chrome = null!;
    IntPtr _hwnd;

    /// <summary>Width of the three caption buttons; an estimate until DWM has been asked.</summary>
    double _captionWidth = 146;

    /// <summary>The backdrops in the order the list shows them.</summary>
    static readonly WindowBackdrop[] Backdrops =
    {
        WindowBackdrop.Mica, WindowBackdrop.MicaAlt, WindowBackdrop.Acrylic
    };

    static string BackdropKey(WindowBackdrop backdrop) => backdrop switch
    {
        WindowBackdrop.Mica => "main.backdrop.mica",
        WindowBackdrop.Acrylic => "main.backdrop.acrylic",
        _ => "main.backdrop.micaalt"
    };

    void SetupChrome()
    {
        _chrome = new WindowChrome
        {
            CaptionHeight = TitleHeight,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
            UseAeroCaptionButtons = true,
            CornerRadius = new CornerRadius(0)
        };
        WindowChrome.SetWindowChrome(this, _chrome);

        Background = Brushes.Transparent;
        PreviewTools.Margin = new Thickness(0, 0, _captionWidth + CaptionGap, 0);

        try { TitleLogo.Source = LogoFrame(); }
        catch { /* без логотипа заголовок всё равно работает */ }

        Ui.WatchTheme(this);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(ChromeHook);
            ApplyBackdrop();
        };

        StateChanged += (_, _) => UpdateChromeMetrics();
        DpiChanged += (_, _) => UpdateChromeMetrics();
    }

    /// <summary>
    /// The 32 px frame of the icon, scaled down to the logo size.
    ///
    /// An ico opened as a single image gives its first frame, which is usually the 16 px
    /// one, and at 150 % that is stretched to 24 and blurs. Scaling 32 down stays sharp at
    /// every scale up to 200 %.
    /// </summary>
    static BitmapSource LogoFrame()
    {
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/icon.ico"),
                                           BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        foreach (var frame in decoder.Frames)
            if (frame.PixelWidth == 32) return frame;

        return decoder.Frames[0];
    }

    /// <summary>
    /// Writes the chosen backdrop onto the window.
    ///
    /// The Fluent theme sets a backdrop of its own, Mica, when it styles the window, so the
    /// choice is written after it: once the handle exists, again at the end of every
    /// rebuild, and after every theme or colour change, queued behind whatever the theme
    /// does with the same message.
    /// </summary>
    void ApplyBackdrop()
    {
        if (_hwnd == IntPtr.Zero) return;

        int type = _cfg.Backdrop switch
        {
            WindowBackdrop.Mica => DWMSBT_MAINWINDOW,
            WindowBackdrop.Acrylic => DWMSBT_TRANSIENTWINDOW,
            _ => DWMSBT_TABBEDWINDOW
        };
        DwmSetWindowAttribute(_hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
    }

    IntPtr ChromeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_SETTINGCHANGE or WM_THEMECHANGED or WM_DWMCOLORIZATIONCOLORCHANGED)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyBackdrop));

        return IntPtr.Zero;
    }

    /// <summary>
    /// Keeps the title bar clear of the caption buttons, and the content on screen while
    /// maximised.
    ///
    /// A maximised window hangs its resize frame past the edges of the screen, 8 px at
    /// 100 %. With the frame turned into client area the content would go with it, and the
    /// title bar would lose its top 8 px under the edge, so the content is inset by the
    /// frame for as long as the window stays maximised.
    /// </summary>
    void UpdateChromeMetrics()
    {
        if (_hwnd == IntPtr.Zero || WindowState == WindowState.Minimized) return;

        var dpi = VisualTreeHelper.GetDpi(this);

        if (DwmGetWindowAttribute(_hwnd, DWMWA_CAPTION_BUTTON_BOUNDS, out var bounds, Marshal.SizeOf<RECT>()) == 0
            && bounds.Right > bounds.Left)
            _captionWidth = (bounds.Right - bounds.Left) / dpi.DpiScaleX;

        double frame = 0;
        if (WindowState == WindowState.Maximized)
        {
            uint dpiValue = (uint)Math.Round(96 * dpi.DpiScaleX);
            frame = (GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpiValue)
                     + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpiValue)) / dpi.DpiScaleX;
        }

        Root.Margin = new Thickness(frame);
        _chrome.CaptionHeight = TitleHeight + frame;
        PreviewTools.Margin = new Thickness(0, 0, _captionWidth + CaptionGap, 0);
    }

    // ---- DWM --------------------------------------------------------------

    const int WM_SETTINGCHANGE = 0x001A;
    const int WM_THEMECHANGED = 0x031A;
    const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

    const int DWMWA_CAPTION_BUTTON_BOUNDS = 5;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    const int DWMSBT_MAINWINDOW = 2;
    const int DWMSBT_TRANSIENTWINDOW = 3;
    const int DWMSBT_TABBEDWINDOW = 4;

    const int SM_CXSIZEFRAME = 32;
    const int SM_CXPADDEDBORDER = 92;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll")]
    static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
