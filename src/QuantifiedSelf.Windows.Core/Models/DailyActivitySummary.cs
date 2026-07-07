namespace QuantifiedSelf.Windows.Core.Models;

/// <summary>
/// Read-only summary of today's activity, aggregated from app_sessions and foreground_samples.
/// All duration values are overlap-scaled to the local calendar day.
/// </summary>
public sealed class DailyActivitySummary
{
    /// <summary>
    /// Local calendar date this summary covers.
    /// </summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// Total tracked duration in seconds (active + idle + unknown), overlap-scaled to today.
    /// </summary>
    public long TotalDurationSeconds { get; set; }

    /// <summary>
    /// Total active duration in seconds, overlap-scaled to today.
    /// </summary>
    public long TotalActiveDurationSeconds { get; set; }

    /// <summary>
    /// Total idle duration in seconds, overlap-scaled to today.
    /// </summary>
    public long TotalIdleDurationSeconds { get; set; }

    /// <summary>
    /// Number of foreground samples recorded today.
    /// </summary>
    public long SampleCount { get; set; }

    /// <summary>
    /// Number of app sessions overlapping today.
    /// </summary>
    public int SessionCount { get; set; }

    /// <summary>
    /// Earliest session or sample time seen today (UTC), or null when no data.
    /// </summary>
    public DateTime? FirstSeenAtUtc { get; set; }

    /// <summary>
    /// Latest session or sample time seen today (UTC), or null when no data.
    /// </summary>
    public DateTime? LastSeenAtUtc { get; set; }

    /// <summary>
    /// Top apps by active duration today (stable ordering: active desc, total desc, name asc).
    /// </summary>
    public IReadOnlyList<AppUsageSummary> TopApps { get; set; } = [];

    /// <summary>
    /// Top window titles by sample count today (stable ordering: count desc, title asc).
    /// </summary>
    public IReadOnlyList<DailyWindowUsageSummary> TopWindows { get; set; } = [];

    /// <summary>
    /// Total number of meaningful task-context switches today.
    /// Tool hops inside the same work context are not counted here.
    /// </summary>
    public int ContextSwitchCount { get; set; }

    /// <summary>
    /// Total number of raw foreground switches (app or title changes between consecutive active samples) today.
    /// </summary>
    public int RawContextSwitchCount { get; set; }

    /// <summary>
    /// The longest uninterrupted focus session today, or null when no focus sessions detected.
    /// </summary>
    public FocusSessionSummary? LongestFocusSession { get; set; }

    /// <summary>
    /// Number of focus sessions (meeting min-duration and max-switch thresholds) today.
    /// </summary>
    public int FocusSessionCount { get; set; }

    /// <summary>
    /// Total active seconds spent in high-switch (fragmented) segments today.
    /// </summary>
    public long FragmentedTimeSeconds { get; set; }
}
