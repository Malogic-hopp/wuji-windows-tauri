namespace QuantifiedSelf.Windows.Core.Models;

/// <summary>
/// A directional context-to-context transition pair with count and ratio.
/// </summary>
public sealed class ContextTransitionInsight
{
    /// <summary>The context the user was leaving.</summary>
    public string FromContext { get; init; } = string.Empty;

    /// <summary>The context the user switched to.</summary>
    public string ToContext { get; init; } = string.Empty;

    /// <summary>How many times this specific transition occurred.</summary>
    public int Count { get; init; }

    /// <summary>Ratio of this transition among all meaningful context switches (0.0–1.0).</summary>
    public double Ratio { get; init; }

    /// <summary>Pre-formatted display text, e.g. "Development → Communication · 51 (14.0%)".</summary>
    public string DisplayText { get; init; } = string.Empty;
}
