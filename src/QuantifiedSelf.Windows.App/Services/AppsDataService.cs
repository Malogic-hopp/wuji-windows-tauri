using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Database;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class AppsDataService
{
    private readonly AppUsageQueryService _queryService;

    public AppsDataService(WindowsAgentPaths paths)
    {
        _queryService = new AppUsageQueryService(paths.DatabasePath);
    }

    public Task<IReadOnlyList<AppUsageSummary>> GetTodayAppUsageAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return _queryService.GetAppUsageForLocalDayAsync(
            DateOnly.FromDateTime(DateTime.Now),
            limit,
            cancellationToken);
    }
}
