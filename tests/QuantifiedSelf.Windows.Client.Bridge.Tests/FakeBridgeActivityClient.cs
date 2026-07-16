using QuantifiedSelf.Windows.ApplicationLayer.Activity;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Client.Bridge.Tests;

internal sealed class FakeBridgeActivityClient : IActivityClient, IOverviewDataService
{
    public DashboardSummary Summary { get; set; } = new();

    public IReadOnlyList<AppUsageSummary> TopApps { get; set; } = [];

    public IReadOnlyList<AppSession> RecentSessions { get; set; } = [];

    public Func<CancellationToken, Task<DashboardSummary>>? SummaryHandler { get; set; }

    public Func<CancellationToken, Task<IReadOnlyList<AppUsageSummary>>>? TopAppsHandler { get; set; }

    public Func<CancellationToken, Task<IReadOnlyList<AppSession>>>? RecentSessionsHandler { get; set; }

    public int SummaryCount { get; private set; }

    public int TopAppsCount { get; private set; }

    public int RecentSessionsCount { get; private set; }

    IOverviewDataService IActivityClient.Overview => this;

    public ISamplesDataService Samples => throw new InvalidOperationException("Bridge must use Activity.Overview only.");

    public ISessionsDataService Sessions => throw new InvalidOperationException("Bridge must use Activity.Overview only.");

    public IAppsDataService Apps => throw new InvalidOperationException("Bridge must use Activity.Overview only.");

    public IDailyStatsService DailyStats => throw new InvalidOperationException("Bridge must use Activity.Overview only.");

    public IWeeklyTrendService WeeklyTrend => throw new InvalidOperationException("Bridge must use Activity.Overview only.");

    public IHourActivityHeatmapService Heatmap => throw new InvalidOperationException("Bridge must use Activity.Overview only.");

    public IFocusInterruptionInsightService Insights => throw new InvalidOperationException("Bridge must use Activity.Overview only.");

    public Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken cancellationToken = default)
    {
        SummaryCount++;
        return SummaryHandler is null
            ? Task.FromResult(Summary)
            : SummaryHandler(cancellationToken);
    }

    public Task<IReadOnlyList<AppUsageSummary>> GetTopAppsTodayAsync(
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        Assert.Equal(5, limit);
        TopAppsCount++;
        return TopAppsHandler is null
            ? Task.FromResult(TopApps)
            : TopAppsHandler(cancellationToken);
    }

    public Task<IReadOnlyList<AppSession>> GetRecentSessionsAsync(
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        Assert.Equal(5, limit);
        RecentSessionsCount++;
        return RecentSessionsHandler is null
            ? Task.FromResult(RecentSessions)
            : RecentSessionsHandler(cancellationToken);
    }
}
