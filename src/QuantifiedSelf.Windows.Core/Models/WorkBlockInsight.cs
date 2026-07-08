namespace QuantifiedSelf.Windows.Core.Models;

/// <summary>
/// A single work block — a continuous stretch of active foreground samples
/// with gaps no larger than the segment-break threshold.
/// </summary>
public sealed class WorkBlockInsight
{
    /// <summary>Block start time in local time.</summary>
    public DateTime StartLocal { get; init; }

    /// <summary>Block end time in local time.</summary>
    public DateTime EndLocal { get; init; }

    /// <summary>Total duration of this block.</summary>
    public TimeSpan Duration => EndLocal - StartLocal;

    /// <summary>The dominant context classification within this block.</summary>
    public string PrimaryContext { get; init; } = string.Empty;

    /// <summary>The most frequent app (short name) within this block.</summary>
    public string PrimaryApp { get; init; } = string.Empty;

    /// <summary>Number of meaningful context switches detected within this block.</summary>
    public int ContextSwitchCount { get; init; }

    /// <summary>Average interval between context switches, or TimeSpan.Zero if none.</summary>
    public TimeSpan AverageSwitchInterval { get; init; }

    /// <summary>Top interruption sources (apps of different context) within this block, top 3.</summary>
    public IReadOnlyList<InterruptionSourceInsight> TopInterruptions { get; init; } = [];

    /// <summary>Whether this block qualifies as a valid focus session.</summary>
    public bool IsRecognizedFocusBlock { get; init; }

    /// <summary>One-sentence explanation of why this block did/didn't qualify as focus.</summary>
    public string ExplanationText { get; init; } = string.Empty;
}
