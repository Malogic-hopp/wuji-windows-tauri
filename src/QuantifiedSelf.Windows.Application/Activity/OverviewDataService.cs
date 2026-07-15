using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Data;
using QuantifiedSelf.Windows.Core.Display;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.ApplicationLayer.Activity;

public sealed class OverviewDataService : IOverviewDataService
{
    private readonly IOverviewQueryPort _overviewQueryPort;
    private readonly IAppUsageQueryPort _appUsageQueryPort;
    private readonly TimeProvider _timeProvider;

    public OverviewDataService(
        IOverviewQueryPort overviewQueryPort,
        IAppUsageQueryPort appUsageQueryPort,
        TimeProvider? timeProvider = null)
    {
        _overviewQueryPort = overviewQueryPort ?? throw new ArgumentNullException(nameof(overviewQueryPort));
        _appUsageQueryPort = appUsageQueryPort ?? throw new ArgumentNullException(nameof(appUsageQueryPort));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken cancellationToken = default)
    {
        return _overviewQueryPort.GetDashboardSummaryAsync(cancellationToken);
    }

    public Task<IReadOnlyList<AppUsageSummary>> GetTopAppsTodayAsync(int limit = 5, CancellationToken cancellationToken = default)
    {
        return _appUsageQueryPort.GetAppUsageForLocalDayAsync(
            DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime),
            limit,
            cancellationToken);
    }

    public Task<IReadOnlyList<AppSession>> GetRecentSessionsAsync(int limit = 5, CancellationToken cancellationToken = default)
    {
        return MapDisplayNamesAsync(_overviewQueryPort.GetRecentSessionsAsync(limit, cancellationToken));
    }

    private static async Task<IReadOnlyList<T>> MapDisplayNamesAsync<T>(Task<IReadOnlyList<T>> sourceTask)
        where T : class
    {
        var items = await sourceTask;

        foreach (var item in items)
        {
            switch (item)
            {
                case AppUsageSummary usageSummary:
                    usageSummary.DisplayName = ProductDisplayNameResolver.Resolve(usageSummary.ProcessName);
                    break;
                case AppSession session:
                    session.DisplayName = ProductDisplayNameResolver.Resolve(session.ProcessName);
                    break;
            }
        }

        return items;
    }
}
