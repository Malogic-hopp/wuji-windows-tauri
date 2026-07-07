namespace QuantifiedSelf.Windows.Core.Models;

/// <summary>
/// A contiguous focus block detected from today's active foreground samples.
/// A focus session has low context switching and no large gaps or idle interruptions.
/// </summary>
public sealed class FocusSessionSummary
{
    /// <summary>
    /// Start time of this focus session (UTC).
    /// </summary>
    public DateTime StartUtc { get; set; }

    /// <summary>
    /// End time of this focus session (UTC).
    /// </summary>
    public DateTime EndUtc { get; set; }

    /// <summary>
    /// Duration of this focus session.
    /// </summary>
    public TimeSpan Duration => EndUtc - StartUtc;

    /// <summary>
    /// The dominant process name for this focus session (most frequent app).
    /// </summary>
    public string DominantApp { get; set; } = string.Empty;

    /// <summary>
    /// Number of context switches (app or title changes) detected within this session.
    /// </summary>
    public int SwitchCount { get; set; }

    /// <summary>
    /// Whether this session qualifies as fragmented (switch count exceeds the threshold).
    /// </summary>
    public bool IsFragmented { get; set; }
}
