using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Data;
using QuantifiedSelf.Windows.Core.Events;

namespace QuantifiedSelf.Windows.ApplicationLayer.Activity;

public sealed class DiagnosticsDataService : IDiagnosticsDataService
{
    private readonly IDiagnosticsQueryPort _queryPort;
    private readonly TimeProvider _timeProvider;

    public DiagnosticsDataService(
        IDiagnosticsQueryPort queryPort,
        TimeProvider? timeProvider = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<IReadOnlyList<AgentEvent>> GetRecentEventsAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        return _queryPort.GetRecentEventsAsync(limit, cancellationToken);
    }

    public Task<IReadOnlyList<AgentEvent>> GetRecentErrorsAsync(int limit = 10, CancellationToken cancellationToken = default)
    {
        return _queryPort.GetRecentErrorsAsync(limit, cancellationToken);
    }

    public string GetCurrentJournalPath(DateTime? utcNow = null)
    {
        var timestamp = utcNow ?? _timeProvider.GetUtcNow().UtcDateTime;
        return _queryPort.GetJournalPath(timestamp);
    }
}
