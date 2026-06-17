using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Database;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class OverviewDataService
{
    private readonly OverviewQueryService _queryService;

    public OverviewDataService(WindowsAgentPaths paths)
    {
        _queryService = new OverviewQueryService(paths.DatabasePath);
    }

    public Task<DashboardSummary> GetDashboardSummaryAsync(CancellationToken cancellationToken = default)
    {
        return _queryService.GetTodaySummaryAsync(cancellationToken);
    }

    public Task<IReadOnlyList<AppUsageSummary>> GetTopAppsTodayAsync(int limit = 5, CancellationToken cancellationToken = default)
    {
        return _queryService.GetTopAppsTodayAsync(limit, cancellationToken);
    }

    public Task<IReadOnlyList<AppSession>> GetRecentSessionsAsync(int limit = 5, CancellationToken cancellationToken = default)
    {
        return _queryService.GetRecentSessionsAsync(limit, cancellationToken);
    }
}
