namespace QuantifiedSelf.Windows.App.ViewModels;

/// <summary>
/// A single day in the 7-day trend display, used by DashboardViewModel for XAML binding.
/// </summary>
public sealed class TrendDayItem
{
    /// <summary>
    /// Short day-of-week label, e.g. "周一", "周二", or "今天".
    /// </summary>
    public string DayLabel { get; set; } = string.Empty;

    /// <summary>
    /// Formatted active duration, e.g. "1h 30m" or "0m".
    /// </summary>
    public string ActiveText { get; set; } = "0m";

    /// <summary>
    /// Short date label, e.g. "07/06".
    /// </summary>
    public string DateLabel { get; set; } = string.Empty;

    /// <summary>
    /// Legacy normalized bar ratio kept for existing tests/bindings (0.0 to 1.0).
    /// </summary>
    public double BarWidthRatio { get; set; }

    /// <summary>
    /// Bar height as a fraction of the max value (0.0 to 1.0).
    /// </summary>
    public double BarHeightRatio { get; set; }

    /// <summary>
    /// Bar height in pixels for the dashboard chart.
    /// </summary>
    public double BarHeightPixels { get; set; }

    /// <summary>
    /// Whether this day is today (for visual emphasis).
    /// </summary>
    public bool IsToday { get; set; }
}
