namespace QuantifiedSelf.Windows.Core.Models;

public sealed class DashboardSummary
{
    public DateTime DateUtc { get; set; } = DateTime.UtcNow.Date;

    public int TotalDurationSeconds { get; set; }

    public int ActiveDurationSeconds { get; set; }

    public int IdleDurationSeconds { get; set; }

    public int UnknownDurationSeconds { get; set; }

    public int SessionCount { get; set; }
}
