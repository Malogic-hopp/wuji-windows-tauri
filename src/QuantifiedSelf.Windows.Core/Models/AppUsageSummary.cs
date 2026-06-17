namespace QuantifiedSelf.Windows.Core.Models;

public sealed class AppUsageSummary
{
    public string ProcessName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public int TotalDurationSeconds { get; set; }

    public int ActiveDurationSeconds { get; set; }

    public int IdleDurationSeconds { get; set; }

    public int UnknownDurationSeconds { get; set; }

    public int SessionCount { get; set; }

    public DateTime? LastUsedAtUtc { get; set; }
}
