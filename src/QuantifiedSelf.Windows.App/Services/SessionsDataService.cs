using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Database;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class SessionsDataService
{
    private readonly SessionQueryService _queryService;

    public SessionsDataService(WindowsAgentPaths paths)
    {
        _queryService = new SessionQueryService(paths.DatabasePath);
    }

    public Task<IReadOnlyList<AppSession>> GetRecentSessionsAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return _queryService.GetRecentSessionsAsync(limit, cancellationToken);
    }

    public Task<IReadOnlyList<AppSession>> GetTodaySessionsAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return _queryService.GetSessionsForLocalDayAsync(
            DateOnly.FromDateTime(DateTime.Now),
            limit,
            cancellationToken);
    }

    public Task<IReadOnlyList<AppSession>> GetLast24HoursSessionsAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var endUtc = DateTime.UtcNow;
        return _queryService.GetSessionsOverlappingRangeAsync(
            endUtc.AddHours(-24),
            endUtc,
            limit,
            cancellationToken);
    }
}
