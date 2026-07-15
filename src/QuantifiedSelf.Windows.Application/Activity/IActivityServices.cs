using QuantifiedSelf.Windows.ApplicationLayer.Contracts.Activity;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.ApplicationLayer.Activity;

public interface IOverviewDataService
{
    Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppUsageSummary>> GetTopAppsTodayAsync(
        int limit = 5,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppSession>> GetRecentSessionsAsync(
        int limit = 5,
        CancellationToken cancellationToken = default);
}

public interface IDiagnosticsDataService
{
    Task<IReadOnlyList<AgentEvent>> GetRecentEventsAsync(
        int limit = 20,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentEvent>> GetRecentErrorsAsync(
        int limit = 10,
        CancellationToken cancellationToken = default);

    string GetCurrentJournalPath(DateTime? utcNow = null);
}

public interface ISamplesDataService
{
    Task<IReadOnlyList<ForegroundSample>> GetRecentSamplesAsync(
        int limit = 200,
        CancellationToken cancellationToken = default);
}

public interface ISessionsDataService
{
    Task<IReadOnlyList<AppSession>> GetRecentSessionsAsync(
        int limit = 200,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppSession>> GetTodaySessionsAsync(
        int limit = 200,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppSession>> GetLast24HoursSessionsAsync(
        int limit = 200,
        CancellationToken cancellationToken = default);
}

public interface IAppsDataService
{
    Task<IReadOnlyList<AppUsageSummary>> GetTodayAppUsageAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);
}

public interface IDailyStatsService
{
    Task<DailyActivitySummary> GetTodaySummaryAsync(
        int topAppsLimit = 5,
        int topWindowsLimit = 10,
        CancellationToken cancellationToken = default);

    Task<DailyActivitySummary> GetSummaryForDateAsync(
        DateOnly localDate,
        int topAppsLimit = 5,
        int topWindowsLimit = 10,
        CancellationToken cancellationToken = default);
}

public interface IWeeklyTrendService
{
    Task<WeeklyTrendResult> GetWeeklyTrendAsync(CancellationToken cancellationToken = default);
}

public interface IFocusInterruptionInsightService
{
    Task<FocusInterruptionInsight> GetInsightAsync(
        DateOnly date,
        CancellationToken cancellationToken = default);
}

public interface IHourActivityHeatmapService
{
    Task<HourActivityHeatmapResult> GetHeatmapAsync(CancellationToken cancellationToken = default);
}
