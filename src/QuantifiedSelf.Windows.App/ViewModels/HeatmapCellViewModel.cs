using Media = System.Windows.Media;

namespace QuantifiedSelf.Windows.App.ViewModels;

/// <summary>
/// A single cell in the 24h×7d activity heatmap, pre-computed for XAML binding.
/// </summary>
public sealed class HeatmapCellViewModel
{
    /// <summary>
    /// Normalized activity intensity (0.0–1.0) used to compute the cell color.
    /// This is ActiveSamples / maxActiveInPeriod, NOT the within-hour active/idle split.
    /// </summary>
    public double ActiveIntensity { get; set; }

    /// <summary>
    /// Pre-computed background brush for this cell.
    /// </summary>
    public Media.SolidColorBrush Background { get; set; } = Media.Brushes.Transparent;

    /// <summary>
    /// Tooltip text: date, hour, active ratio, sample count.
    /// </summary>
    public string TooltipText { get; set; } = string.Empty;

    /// <summary>
    /// Whether this row is a phase boundary (hours 0, 6, 12, 18).
    /// </summary>
    public bool IsPhaseBoundary { get; set; }

    /// <summary>
    /// Phase band label for this row, or null for non-boundary rows.
    /// Values: "睡觉 00-06", "上午 06-12", "下午 12-18", "晚上 18-24"
    /// </summary>
    public string? PhaseLabel { get; set; }

    // ── 4-stop color gradient matching the old project ──

    private static readonly (double Stop, Media.Color Color)[] Gradient =
    [
        (0.0, Media.Color.FromRgb(0xe8, 0xef, 0xf9)),
        (0.4, Media.Color.FromRgb(0x93, 0xc5, 0xfd)),
        (0.7, Media.Color.FromRgb(0x3b, 0x82, 0xf6)),
        (1.0, Media.Color.FromRgb(0x1d, 0x4e, 0xd8))
    ];

    public static Media.SolidColorBrush InterpolateColor(double ratio)
    {
        if (ratio <= Gradient[0].Stop) return new Media.SolidColorBrush(Gradient[0].Color);
        if (ratio >= Gradient[^1].Stop) return new Media.SolidColorBrush(Gradient[^1].Color);

        for (var i = 0; i < Gradient.Length - 1; i++)
        {
            var (s1, c1) = Gradient[i];
            var (s2, c2) = Gradient[i + 1];

            if (ratio < s1 || ratio > s2) continue;

            var t = (ratio - s1) / (s2 - s1);
            var r = (byte)(c1.R + (c2.R - c1.R) * t);
            var g = (byte)(c1.G + (c2.G - c1.G) * t);
            var b = (byte)(c1.B + (c2.B - c1.B) * t);

            return new Media.SolidColorBrush(Media.Color.FromRgb(r, g, b));
        }

        return new Media.SolidColorBrush(Gradient[0].Color);
    }
}
