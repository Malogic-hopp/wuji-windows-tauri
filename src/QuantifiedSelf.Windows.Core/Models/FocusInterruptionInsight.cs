namespace QuantifiedSelf.Windows.Core.Models;

/// <summary>
/// Full focus-interruption insight for a single calendar day.
/// Contains work blocks, interruption sources, context transitions,
/// and human-readable summary/action text.
/// </summary>
public sealed class FocusInterruptionInsight
{
    /// <summary>Local calendar date this insight covers.</summary>
    public DateOnly Date { get; init; }

    /// <summary>Number of Active-state foreground samples for this date.</summary>
    public int ActiveSampleCount { get; init; }

    /// <summary>Estimated total active time computed from sample gaps.</summary>
    public TimeSpan EstimatedActiveTime { get; init; }

    /// <summary>Total raw foreground switches (app or title changes between consecutive active samples).</summary>
    public int RawToolHopCount { get; init; }

    /// <summary>Total meaningful context-level switches (context classification change).</summary>
    public int MeaningfulContextSwitchCount { get; init; }

    /// <summary>Work blocks that meet the minimum duration threshold, top 4 by duration.</summary>
    public IReadOnlyList<WorkBlockInsight> WorkBlocks { get; init; } = [];

    /// <summary>Top interruption sources (apps that pull user away from primary work contexts), top 6.</summary>
    public IReadOnlyList<InterruptionSourceInsight> TopInterruptionSources { get; init; } = [];

    /// <summary>Top context-to-context transition pairs, top 8.</summary>
    public IReadOnlyList<ContextTransitionInsight> TopContextTransitions { get; init; } = [];

    /// <summary>A one-paragraph natural-language summary of today's focus pattern.</summary>
    public string SummaryText { get; init; } = string.Empty;

    /// <summary>Actionable, gentle suggestion text (1–3 items, newline separated).</summary>
    public string ActionText { get; init; } = string.Empty;

    /// <summary>Top-level stats card text for the UI.</summary>
    public string LongestWorkBlockText { get; init; } = string.Empty;

    /// <summary>Top interruption source display text.</summary>
    public string TopInterruptionText { get; init; } = string.Empty;
}
