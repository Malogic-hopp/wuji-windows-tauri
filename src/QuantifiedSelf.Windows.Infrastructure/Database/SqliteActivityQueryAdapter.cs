using System.IO;
using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Data;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Paths;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

public sealed class SqliteActivityQueryAdapter :
    IOverviewQueryPort,
    IDiagnosticsQueryPort,
    ISampleQueryPort,
    ISessionQueryPort,
    IAppUsageQueryPort,
    IDailyStatsQueryPort
{
    private readonly WindowsAgentPaths _paths;
    private readonly OverviewQueryService _overviewQueryService;
    private readonly DiagnosticsQueryService _diagnosticsQueryService;
    private readonly SampleQueryService _sampleQueryService;
    private readonly SessionQueryService _sessionQueryService;
    private readonly AppUsageQueryService _appUsageQueryService;
    private readonly DailyStatsQueryService _dailyStatsQueryService;

    public SqliteActivityQueryAdapter(WindowsAgentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _overviewQueryService = new OverviewQueryService(paths.DatabasePath);
        _diagnosticsQueryService = new DiagnosticsQueryService(paths.DatabasePath);
        _sampleQueryService = new SampleQueryService(paths.DatabasePath);
        _sessionQueryService = new SessionQueryService(paths.DatabasePath);
        _appUsageQueryService = new AppUsageQueryService(paths.DatabasePath);
        _dailyStatsQueryService = new DailyStatsQueryService(paths.DatabasePath);
    }

    public Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken cancellationToken = default) =>
        _overviewQueryService.GetTodaySummaryAsync(cancellationToken);

    Task<IReadOnlyList<AppSession>> IOverviewQueryPort.GetRecentSessionsAsync(
        int limit,
        CancellationToken cancellationToken) =>
        _overviewQueryService.GetRecentSessionsAsync(limit, cancellationToken);

    public Task<IReadOnlyList<AgentEvent>> GetRecentEventsAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        _diagnosticsQueryService.GetRecentEventsAsync(limit, cancellationToken);

    public Task<IReadOnlyList<AgentEvent>> GetRecentErrorsAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        _diagnosticsQueryService.GetRecentErrorsAsync(limit, cancellationToken);

    public string GetJournalPath(DateTime utcTimestamp) =>
        Path.Combine(_paths.LogsDir, $"agent_events_{utcTimestamp:yyyyMMdd}.jsonl");

    public Task<IReadOnlyList<ForegroundSample>> GetRecentSamplesAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        _sampleQueryService.GetRecentSamplesAsync(limit, cancellationToken);

    public Task<IReadOnlyList<AppSession>> GetRecentSessionsAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        _sessionQueryService.GetRecentSessionsAsync(limit, cancellationToken);

    public Task<IReadOnlyList<AppSession>> GetSessionsForLocalDayAsync(
        DateOnly localDate,
        int limit,
        CancellationToken cancellationToken = default) =>
        _sessionQueryService.GetSessionsForLocalDayAsync(localDate, limit, cancellationToken);

    public Task<IReadOnlyList<AppSession>> GetSessionsOverlappingRangeAsync(
        DateTime startUtc,
        DateTime endUtc,
        int limit,
        CancellationToken cancellationToken = default) =>
        _sessionQueryService.GetSessionsOverlappingRangeAsync(startUtc, endUtc, limit, cancellationToken);

    public Task<IReadOnlyList<AppUsageSummary>> GetAppUsageForLocalDayAsync(
        DateOnly localDate,
        int limit,
        CancellationToken cancellationToken = default) =>
        _appUsageQueryService.GetAppUsageForLocalDayAsync(localDate, limit, cancellationToken);

    public Task<long> GetSampleCountForLocalDayAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default) =>
        _dailyStatsQueryService.GetSampleCountForLocalDayAsync(localDate, cancellationToken);

    public Task<(DateTime? FirstSeenUtc, DateTime? LastSeenUtc)> GetSampleTimeRangeForLocalDayAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default) =>
        _dailyStatsQueryService.GetSampleTimeRangeForLocalDayAsync(localDate, cancellationToken);

    public Task<IReadOnlyList<DailyWindowUsageSummary>> GetTopWindowsForLocalDayAsync(
        DateOnly localDate,
        int limit,
        CancellationToken cancellationToken = default) =>
        _dailyStatsQueryService.GetTopWindowsForLocalDayAsync(localDate, limit, cancellationToken);

    public Task<IReadOnlyList<AppSession>> GetSessionsOverlappingLocalDayAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default) =>
        _dailyStatsQueryService.GetSessionsOverlappingLocalDayAsync(localDate, cancellationToken);

    public Task<IReadOnlyList<ForegroundSample>> GetSamplesForLocalDayAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default) =>
        _dailyStatsQueryService.GetSamplesForLocalDayAsync(localDate, cancellationToken);

    public Task<IReadOnlyList<ForegroundSample>> GetSamplesForDateRangeAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default) =>
        _dailyStatsQueryService.GetSamplesForDateRangeAsync(startDate, endDate, cancellationToken);
}
