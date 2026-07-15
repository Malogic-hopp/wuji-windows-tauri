using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Data;

public interface IOverviewQueryPort
{
    Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppSession>> GetRecentSessionsAsync(
        int limit,
        CancellationToken cancellationToken = default);
}

public interface IDiagnosticsQueryPort
{
    Task<IReadOnlyList<AgentEvent>> GetRecentEventsAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentEvent>> GetRecentErrorsAsync(
        int limit,
        CancellationToken cancellationToken = default);

    string GetJournalPath(DateTime utcTimestamp);
}

public interface ISampleQueryPort
{
    Task<IReadOnlyList<ForegroundSample>> GetRecentSamplesAsync(
        int limit,
        CancellationToken cancellationToken = default);
}

public interface ISessionQueryPort
{
    Task<IReadOnlyList<AppSession>> GetRecentSessionsAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppSession>> GetSessionsForLocalDayAsync(
        DateOnly localDate,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppSession>> GetSessionsOverlappingRangeAsync(
        DateTime startUtc,
        DateTime endUtc,
        int limit,
        CancellationToken cancellationToken = default);
}

public interface IAppUsageQueryPort
{
    Task<IReadOnlyList<AppUsageSummary>> GetAppUsageForLocalDayAsync(
        DateOnly localDate,
        int limit,
        CancellationToken cancellationToken = default);
}

public interface IDailyStatsQueryPort
{
    Task<long> GetSampleCountForLocalDayAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default);

    Task<(DateTime? FirstSeenUtc, DateTime? LastSeenUtc)> GetSampleTimeRangeForLocalDayAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DailyWindowUsageSummary>> GetTopWindowsForLocalDayAsync(
        DateOnly localDate,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AppSession>> GetSessionsOverlappingLocalDayAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ForegroundSample>> GetSamplesForLocalDayAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ForegroundSample>> GetSamplesForDateRangeAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);
}
