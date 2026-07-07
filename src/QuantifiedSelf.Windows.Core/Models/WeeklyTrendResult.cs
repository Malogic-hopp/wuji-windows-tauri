namespace QuantifiedSelf.Windows.Core.Models;

/// <summary>
/// 7-day trend data with daily points and today-vs-average comparisons.
/// All comparison text is in Chinese and safe for direct UI display.
/// </summary>
public sealed class WeeklyTrendResult
{
    /// <summary>
    /// Seven daily trend points, ordered from oldest (day 0) to newest (today, day 6).
    /// Missing days are filled with zero values.
    /// </summary>
    public IReadOnlyList<DailyTrendPoint> Days { get; set; } = [];

    /// <summary>
    /// 7-day average active seconds (includes all 7 days).
    /// </summary>
    public double AverageActiveSeconds { get; set; }

    /// <summary>
    /// 7-day average focus session duration in seconds.
    /// </summary>
    public double AverageFocusSeconds { get; set; }

    /// <summary>
    /// 7-day average context switch count.
    /// </summary>
    public double AverageSwitchCount { get; set; }

    /// <summary>
    /// Human-readable comparison of today's active time vs the 7-day average.
    /// </summary>
    public string ActiveComparisonText { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable comparison of yesterday's active time vs prior completed days.
    /// </summary>
    public string YesterdayActiveComparisonText { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable comparison of this week-to-date vs the same period last week.
    /// </summary>
    public string WeekActiveComparisonText { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable comparison of today's focus vs the 7-day average.
    /// </summary>
    public string FocusComparisonText { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable comparison of today's switch count vs the 7-day average.
    /// </summary>
    public string SwitchComparisonText { get; set; } = string.Empty;
}
