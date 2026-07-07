namespace QuantifiedSelf.Windows.Core.Models;

/// <summary>
/// A single local-rule-based insight suggestion derived from today's activity
/// and 7-day trend data. All text is gentle, specific, and actionable.
/// Never uses judgmental or shaming language.
/// </summary>
public sealed class InsightSuggestion
{
    /// <summary>
    /// Visual tone: "Positive", "Neutral", or "Warning".
    /// </summary>
    public string Severity { get; set; } = "Neutral";

    /// <summary>
    /// Rule category: "Focus", "Switch", "AppUsage", or "Schedule".
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Short summary title, e.g. "今天切换较频繁".
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// The main insight message, one sentence.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Data evidence backing this suggestion, e.g. "过去 30 分钟切换了 8 次".
    /// </summary>
    public string EvidenceText { get; set; } = string.Empty;

    /// <summary>
    /// Concrete, actionable next step, e.g. "可以尝试安排一个 25 分钟单任务块。"
    /// </summary>
    public string ActionText { get; set; } = string.Empty;
}
