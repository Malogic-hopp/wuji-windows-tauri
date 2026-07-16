using System.Globalization;
using QuantifiedSelf.Windows.Client.Bridge.Generated;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Client.Bridge;

internal static class BridgeActivityMapper
{
    private const int MaxDisplayNameLength = 128;

    public static ActivityOverviewResult ToOverview(
        DashboardSummary summary,
        IReadOnlyList<AppUsageSummary> topApps,
        IReadOnlyList<AppSession> recentSessions)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(topApps);
        ArgumentNullException.ThrowIfNull(recentSessions);

        return new ActivityOverviewResult
        {
            Summary = new ActivityOverviewSummary
            {
                DateUtc = FormatUtc(summary.DateUtc),
                TotalDurationSeconds = NonNegative(summary.TotalDurationSeconds),
                ActiveDurationSeconds = NonNegative(summary.ActiveDurationSeconds),
                IdleDurationSeconds = NonNegative(summary.IdleDurationSeconds),
                UnknownDurationSeconds = NonNegative(summary.UnknownDurationSeconds),
                SessionCount = NonNegative(summary.SessionCount)
            },
            TopApps = topApps.Select(ToApp).ToArray(),
            RecentSessions = recentSessions.Select(ToSession).ToArray()
        };
    }

    private static ActivityOverviewApp ToApp(AppUsageSummary source)
    {
        return new ActivityOverviewApp
        {
            DisplayName = ToSafeDisplayName(source.DisplayName),
            TotalDurationSeconds = NonNegative(source.TotalDurationSeconds),
            ActiveDurationSeconds = NonNegative(source.ActiveDurationSeconds),
            IdleDurationSeconds = NonNegative(source.IdleDurationSeconds),
            UnknownDurationSeconds = NonNegative(source.UnknownDurationSeconds),
            SessionCount = NonNegative(source.SessionCount),
            LastUsedAtUtc = FormatUtc(source.LastUsedAtUtc)
        };
    }

    private static ActivityOverviewSession ToSession(AppSession source)
    {
        return new ActivityOverviewSession
        {
            DisplayName = ToSafeDisplayName(source.DisplayName),
            StartedAtUtc = FormatUtc(source.StartedAtUtc),
            EndedAtUtc = FormatUtc(source.EndedAtUtc),
            TotalDurationSeconds = NonNegative(source.TotalDurationSeconds),
            ActiveDurationSeconds = NonNegative(source.ActiveDurationSeconds),
            IdleDurationSeconds = NonNegative(source.IdleDurationSeconds),
            UnknownDurationSeconds = NonNegative(source.UnknownDurationSeconds)
        };
    }

    private static string ToSafeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "未知应用";
        }

        var withoutControlCharacters = new string(
            value.Trim().Where(character => !char.IsControl(character)).ToArray());
        var normalized = withoutControlCharacters.Replace('\\', '/');
        var finalSeparator = normalized.LastIndexOf('/');
        var displayName = finalSeparator >= 0 ? normalized[(finalSeparator + 1)..] : normalized;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "未知应用";
        }

        return displayName.Length <= MaxDisplayNameLength
            ? displayName
            : displayName[..MaxDisplayNameLength];
    }

    private static string FormatUtc(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? FormatUtc(DateTime? value) =>
        value is null ? null : FormatUtc(value.Value);

    private static int NonNegative(int value) => Math.Max(0, value);
}
