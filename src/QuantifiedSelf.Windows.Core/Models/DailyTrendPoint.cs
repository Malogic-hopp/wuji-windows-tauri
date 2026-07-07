namespace QuantifiedSelf.Windows.Core.Models;

/// <summary>
/// A single day's key activity metrics for 7-day trend display.
/// Missing days are represented with all-zero values.
/// </summary>
public sealed class DailyTrendPoint
{
    /// <summary>
    /// Local calendar date this point covers.
    /// </summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// Total active duration in seconds for this day.
    /// </summary>
    public long ActiveSeconds { get; set; }

    /// <summary>
    /// Duration of the longest focus session in seconds for this day.
    /// </summary>
    public long FocusSeconds { get; set; }

    /// <summary>
    /// Total context switch count for this day.
    /// </summary>
    public int ContextSwitchCount { get; set; }

    /// <summary>
    /// Number of sessions for this day.
    /// </summary>
    public int SessionCount { get; set; }

    /// <summary>
    /// Top app name for this day, or null when no data.
    /// </summary>
    public string? TopAppName { get; set; }
}
