namespace QuantifiedSelf.Windows.Core.Models;

public sealed class AppSession
{
    public long Id { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    public string ProcessName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? WindowTitle { get; set; }

    public int TotalDurationSeconds { get; set; }

    public int ActiveDurationSeconds { get; set; }

    public int IdleDurationSeconds { get; set; }

    public int UnknownDurationSeconds { get; set; }

    public string CloseReason { get; set; } = "Open";
}
