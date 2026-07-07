namespace QuantifiedSelf.Windows.Core.Models;

/// <summary>
/// Per-window-title aggregation from foreground_samples for a single day.
/// Window titles are privacy-filtered via DiagnosticMessageSanitizer before display.
/// </summary>
public sealed class DailyWindowUsageSummary
{
    /// <summary>
    /// Raw window title (may contain sensitive content). For internal use only.
    /// </summary>
    public string WindowTitle { get; set; } = string.Empty;

    /// <summary>
    /// Privacy-sanitized window title safe for display.
    /// </summary>
    public string SafeWindowTitle { get; set; } = string.Empty;

    /// <summary>
    /// Process name associated with this window.
    /// </summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// Number of foreground samples where this window was active today.
    /// </summary>
    public int SampleCount { get; set; }
}
