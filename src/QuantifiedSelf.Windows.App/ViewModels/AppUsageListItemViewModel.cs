using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed class AppUsageListItemViewModel
{
    public AppUsageListItemViewModel(AppUsageSummary summary, int rank)
    {
        ArgumentNullException.ThrowIfNull(summary);

        Rank = rank;
        ProcessName = summary.ProcessName;
        DisplayName = string.IsNullOrWhiteSpace(summary.DisplayName)
            ? string.IsNullOrWhiteSpace(summary.ProcessName) ? "Unknown" : summary.ProcessName
            : summary.DisplayName;
        ActiveDurationText = FormatDuration(summary.ActiveDurationSeconds);
        TotalDurationText = FormatDuration(summary.TotalDurationSeconds);
        IdleDurationText = FormatDuration(summary.IdleDurationSeconds);
        UnknownDurationText = FormatDuration(summary.UnknownDurationSeconds);
        SessionCount = summary.SessionCount;
        LastUsedLocalTimeText = summary.LastUsedAtUtc.HasValue
            ? summary.LastUsedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "-";
    }

    public int Rank { get; }

    public string DisplayName { get; }

    public string ProcessName { get; }

    public string ActiveDurationText { get; }

    public string TotalDurationText { get; }

    public string IdleDurationText { get; }

    public string UnknownDurationText { get; }

    public int SessionCount { get; }

    public string LastUsedLocalTimeText { get; }

    private static string FormatDuration(int totalSeconds)
    {
        var normalizedSeconds = Math.Max(0, totalSeconds);
        var duration = TimeSpan.FromSeconds(normalizedSeconds);
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes}m {duration.Seconds}s";
        }

        return $"{duration.Seconds}s";
    }
}
