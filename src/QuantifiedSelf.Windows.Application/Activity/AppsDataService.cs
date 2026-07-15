using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Data;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.ApplicationLayer.Activity;

public sealed class AppsDataService : IAppsDataService
{
    private readonly IAppUsageQueryPort _queryPort;
    private readonly TimeProvider _timeProvider;

    public AppsDataService(
        IAppUsageQueryPort queryPort,
        TimeProvider? timeProvider = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<AppUsageSummary>> GetTodayAppUsageAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        return _queryPort.GetAppUsageForLocalDayAsync(
            DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime),
            limit,
            cancellationToken);
    }
}
