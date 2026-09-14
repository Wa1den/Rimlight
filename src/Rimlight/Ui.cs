using System.Windows;
using System.Windows.Media;

namespace Rimlight;

/// <summary>Theme plumbing shared by the windows of the program.</summary>
public static class Ui
{
    /// <summary>The aliases declared in App.xaml, each with the theme colour it stands for.</summary>
    static readonly (string Alias, string Colour)[] Aliases =
    {
        ("Fg", "TextFillColorPrimary"),
        ("FgDim", "TextFillColorSecondary"),
        ("Warn", "SystemFillColorCaution"),
        ("Panel", "CardBackgroundFillColorDefault"),
        ("PanelStroke", "CardStrokeColorDefault"),
        ("CellStroke", "ControlStrokeColorSecondary")
    };

    /// <summary>
    /// A property the window points at a theme colour, so that a theme switch reaches the
    /// aliases.
    ///
    /// The DynamicResource on an alias colour is resolved once and never again: the brushes
    /// live in the application dictionary, outside any element tree, and the switch only
    /// updates references inside the tree. Measured in CaseLight by switching ThemeMode at
    /// run time: the token went to #E4000000 while the alias stayed #FFFFFFFF, and every
    /// caption drawn with it stayed white on the light theme. A reference held by the
    /// window is updated, and its change copies the new colours into the shared brushes.
    /// </summary>
    public static readonly DependencyProperty ThemeProbeProperty = DependencyProperty.RegisterAttached(
        "ThemeProbe", typeof(object), typeof(Ui), new PropertyMetadata(null, (_, _) => RefreshAliases()));

    public static void WatchTheme(FrameworkElement root) =>
        root.SetResourceReference(ThemeProbeProperty, "TextFillColorPrimary");

    static void RefreshAliases()
    {
        var app = Application.Current;
        if (app == null) return;

        foreach (var (alias, colour) in Aliases)
            if (app.Resources[alias] is SolidColorBrush { IsFrozen: false } brush
                && app.TryFindResource(colour) is Color c)
                brush.Color = c;
    }
}
