using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Data;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.ApplicationLayer.Activity;

public sealed class SessionsDataService : ISessionsDataService
{
    private readonly ISessionQueryPort _queryPort;
    private readonly TimeProvider _timeProvider;

    public SessionsDataService(
        ISessionQueryPort queryPort,
        TimeProvider? timeProvider = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<AppSession>> GetRecentSessionsAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return _queryPort.GetRecentSessionsAsync(limit, cancellationToken);
    }

    public Task<IReadOnlyList<AppSession>> GetTodaySessionsAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return _queryPort.GetSessionsForLocalDayAsync(
            DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime),
            limit,
            cancellationToken);
    }

    public Task<IReadOnlyList<AppSession>> GetLast24HoursSessionsAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var endUtc = _timeProvider.GetUtcNow().UtcDateTime;
        return _queryPort.GetSessionsOverlappingRangeAsync(
            endUtc.AddHours(-24),
            endUtc,
            limit,
            cancellationToken);
    }
}
