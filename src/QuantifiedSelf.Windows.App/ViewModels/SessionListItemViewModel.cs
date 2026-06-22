using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed class SessionListItemViewModel
{
    public SessionListItemViewModel(AppSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        SessionId = session.Id;
        StartedAtUtc = session.StartedAtUtc;
        StartedLocalTime = session.StartedAtUtc.ToLocalTime();
        EndedLocalTimeText = session.EndedAtUtc.HasValue
            ? session.EndedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
            : "正在进行";
        ProcessName = session.ProcessName;
        DisplayName = string.IsNullOrWhiteSpace(session.DisplayName)
            ? string.IsNullOrWhiteSpace(session.ProcessName) ? "Unknown" : session.ProcessName
            : session.DisplayName;
        TotalDurationText = FormatDuration(session.TotalDurationSeconds);
        ActiveDurationText = FormatDuration(session.ActiveDurationSeconds);
        IdleDurationText = FormatDuration(session.IdleDurationSeconds);
        UnknownDurationText = FormatDuration(session.UnknownDurationSeconds);
        CloseReason = string.IsNullOrWhiteSpace(session.CloseReason) ? "Other" : session.CloseReason;
        CloseReasonFilter = MapCloseReasonFilter(CloseReason);
    }

    public long SessionId { get; }

    public DateTime StartedAtUtc { get; }

    public DateTime StartedLocalTime { get; }

    public string EndedLocalTimeText { get; }

    public string ProcessName { get; }

    public string DisplayName { get; }

    public string TotalDurationText { get; }

    public string ActiveDurationText { get; }

    public string IdleDurationText { get; }

    public string UnknownDurationText { get; }

    public string CloseReason { get; }

    public string CloseReasonFilter { get; }

    private static string MapCloseReasonFilter(string closeReason)
    {
        return closeReason switch
        {
            "Open" => "Open",
            "ProcessChanged" => "ProcessChanged",
            "Paused" => "Paused",
            "Stopped" => "Stopped",
            _ => "Other"
        };
    }

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
