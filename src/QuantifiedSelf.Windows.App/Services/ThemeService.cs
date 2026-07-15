using System.Windows;
using System.Windows.Media;
using WpfSystemColors = System.Windows.SystemColors;

namespace QuantifiedSelf.Windows.App.Services;

/// <summary>
/// Swaps the semantic brush dictionary used by the preview UI.
/// High contrast maps every semantic role to Windows system brushes.
/// </summary>
public static class ThemeService
{
    public enum Theme { Light, Dark, HighContrast }

    private const string LightBrushesUri = "Themes/Brushes.xaml";
    private const string DarkBrushesUri = "Themes/Brushes.Dark.xaml";
    private const string ThemeMarkerKey = "WujiThemeDictionary";

    private static Theme _currentTheme = Theme.Light;

    public static Theme CurrentTheme => _currentTheme;

    public static Theme Parse(string? value) => value?.Trim() switch
    {
        "Dark" => Theme.Dark,
        "HighContrast" or "High Contrast" => Theme.HighContrast,
        _ => Theme.Light
    };

    public static string ToSettingValue(Theme theme) => theme switch
    {
        Theme.Dark => "Dark",
        Theme.HighContrast => "HighContrast",
        _ => "Light"
    };

    public static void ApplyTheme(Theme theme)
    {
        var app = Application.Current;
        if (app is null)
        {
            _currentTheme = theme;
            return;
        }

        var merged = app.Resources.MergedDictionaries;
        var oldIndex = FindThemeDictionaryIndex(merged);
        var replacement = theme switch
        {
            Theme.Dark => CreateBrushDictionary(DarkBrushesUri),
            Theme.HighContrast => CreateHighContrastDictionary(),
            _ => CreateBrushDictionary(LightBrushesUri)
        };

        if (oldIndex >= 0)
        {
            merged[oldIndex] = replacement;
        }
        else
        {
            merged.Insert(Math.Min(1, merged.Count), replacement);
        }

        _currentTheme = theme;
    }

    private static ResourceDictionary CreateBrushDictionary(string uri) =>
        new() { Source = new Uri(uri, UriKind.Relative) };

    private static int FindThemeDictionaryIndex(IList<ResourceDictionary> dictionaries)
    {
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var dictionary = dictionaries[i];
            var source = dictionary.Source?.ToString() ?? string.Empty;
            if (source.EndsWith("Themes/Brushes.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Themes/Brushes.Dark.xaml", StringComparison.OrdinalIgnoreCase)
                || dictionary.Contains(ThemeMarkerKey))
            {
                return i;
            }
        }

        return -1;
    }

    private static ResourceDictionary CreateHighContrastDictionary()
    {
        var dictionary = new ResourceDictionary { [ThemeMarkerKey] = true };
        Add(dictionary, WpfSystemColors.WindowBrush,
            "PageBackgroundBrush", "PageBackgroundAltBrush", "SurfaceBrush", "SurfaceAltBrush", "SurfaceMutedBrush",
            "ShellBackgroundBrush", "ShellSurfaceAltBrush", "Heatmap0Brush");
        Add(dictionary, WpfSystemColors.WindowTextBrush,
            "TextPrimaryBrush", "TextSecondaryBrush", "TextMutedBrush", "TextPlaceholderBrush", "TextBrush",
            "ShellTextBrush", "ShellMutedTextBrush", "ShellAccentTextBrush", "AccentDarkBrush",
            "SuccessBrush", "WarningBrush", "DangerBrush", "IdleBrush", "InfoBrush");
        Add(dictionary, WpfSystemColors.ActiveBorderBrush,
            "BorderBrush", "BorderStrongBrush", "ShellBorderBrush", "ShellNavSelectedBorderBrush");
        Add(dictionary, WpfSystemColors.HighlightBrush,
            "AccentBrush", "AccentHoverBrush", "ShellNavSelectedBgBrush", "Heatmap4Brush",
            "ContextDevelopmentBrush", "ContextResearchBrush", "ContextWritingBrush", "ContextCommunicationBrush",
            "ContextBrowsingBrush", "ContextEntertainmentBrush", "ContextProductivityBrush", "ContextSystemBrush",
            "ContextOtherBrush", "ContextIdleBrush");
        Add(dictionary, WpfSystemColors.ControlBrush,
            "AccentSoftBrush", "WarningSoftBrush", "DangerSoftBrush", "ShellNavHoverBrush",
            "Heatmap1Brush", "Heatmap2Brush", "Heatmap3Brush");
        return dictionary;
    }

    private static void Add(ResourceDictionary dictionary, System.Windows.Media.Brush brush, params string[] keys)
    {
        foreach (var key in keys)
        {
            dictionary[key] = brush;
        }
    }
}
