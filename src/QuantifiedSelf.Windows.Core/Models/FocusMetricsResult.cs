namespace QuantifiedSelf.Windows.Core.Models;

/// <summary>
/// Computed focus and attention-quality metrics for a single day.
/// All metrics are derived from foreground_samples only — no Agent writes required.
/// </summary>
public sealed class FocusMetricsResult
{
    /// <summary>
    /// Total number of meaningful task-context switches today.
    /// Tool hops inside the same work context are not counted here.
    /// </summary>
    public int ContextSwitchCount { get; set; }

    /// <summary>
    /// Total number of raw foreground switches (process_name or window_title changes) today.
    /// </summary>
    public int RawContextSwitchCount { get; set; }

    /// <summary>
    /// The longest uninterrupted focus session today, or null when no focus sessions detected.
    /// </summary>
    public FocusSessionSummary? LongestFocusSession { get; set; }

    /// <summary>
    /// Total number of focus sessions (meeting min duration and max switch thresholds) today.
    /// </summary>
    public int FocusSessionCount { get; set; }

    /// <summary>
    /// Total active seconds spent in high-switch (fragmented) segments today.
    /// </summary>
    public long FragmentedTimeSeconds { get; set; }

    /// <summary>
    /// All detected focus sessions today, ordered by start time.
    /// </summary>
    public IReadOnlyList<FocusSessionSummary> FocusSessions { get; set; } = [];
}
