namespace QuantifiedSelf.Windows.Core.Models;

/// <summary>
/// An app (or app group) that interrupted a work context.
/// </summary>
public sealed class InterruptionSourceInsight
{
    /// <summary>Short display name of the interrupting app.</summary>
    public string AppName { get; init; } = string.Empty;

    /// <summary>The context this app pulled the user into.</summary>
    public string Context { get; init; } = string.Empty;

    /// <summary>How many times this app appeared as an interruption.</summary>
    public int Count { get; init; }

    /// <summary>Pre-formatted display text, e.g. "WeChat · 57 times".</summary>
    public string DisplayText { get; init; } = string.Empty;
}
