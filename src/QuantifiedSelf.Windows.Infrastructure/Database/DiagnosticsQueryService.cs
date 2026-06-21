using QuantifiedSelf.Windows.Core.Events;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

public sealed class DiagnosticsQueryService
{
    private readonly AgentEventRepository _eventRepository;

    public DiagnosticsQueryService(string databasePath)
    {
        _eventRepository = new AgentEventRepository(databasePath);
    }

    public DiagnosticsQueryService(AgentEventRepository eventRepository)
    {
        ArgumentNullException.ThrowIfNull(eventRepository);
        _eventRepository = eventRepository;
    }

    public Task<IReadOnlyList<AgentEvent>> GetRecentEventsAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        return _eventRepository.GetRecentAsync(limit, cancellationToken);
    }

    public Task<IReadOnlyList<AgentEvent>> GetRecentErrorsAsync(int limit = 10, CancellationToken cancellationToken = default)
    {
        return _eventRepository.GetRecentErrorsAsync(limit, cancellationToken);
    }
}
