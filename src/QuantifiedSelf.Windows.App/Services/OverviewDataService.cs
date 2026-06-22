using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Display;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Database;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class OverviewDataService
{
    private readonly OverviewQueryService _queryService;
    private readonly AppUsageQueryService _appUsageQueryService;

    public OverviewDataService(WindowsAgentPaths paths)
    {
        _queryService = new OverviewQueryService(paths.DatabasePath);
        _appUsageQueryService = new AppUsageQueryService(paths.DatabasePath);
    }

    public Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken cancellationToken = default)
    {
        return _queryService.GetTodaySummaryAsync(cancellationToken);
    }

    public Task<IReadOnlyList<AppUsageSummary>> GetTopAppsTodayAsync(int limit = 5, CancellationToken cancellationToken = default)
    {
        return _appUsageQueryService.GetAppUsageForLocalDayAsync(
            DateOnly.FromDateTime(DateTime.Now),
            limit,
            cancellationToken);
    }

    public Task<IReadOnlyList<AppSession>> GetRecentSessionsAsync(int limit = 5, CancellationToken cancellationToken = default)
    {
        return MapDisplayNamesAsync(_queryService.GetRecentSessionsAsync(limit, cancellationToken));
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
