using System.IO;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Database;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class DiagnosticsDataService
{
    private readonly DiagnosticsQueryService _queryService;
    private readonly WindowsAgentPaths _paths;

    public DiagnosticsDataService(WindowsAgentPaths paths)
    {
        _paths = paths;
        _queryService = new DiagnosticsQueryService(paths.DatabasePath);
    }

    public Task<IReadOnlyList<AgentEvent>> GetRecentEventsAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        return _queryService.GetRecentEventsAsync(limit, cancellationToken);
    }

    public Task<IReadOnlyList<AgentEvent>> GetRecentErrorsAsync(int limit = 10, CancellationToken cancellationToken = default)
    {
        return _queryService.GetRecentErrorsAsync(limit, cancellationToken);
    }

    public string GetCurrentJournalPath(DateTime? utcNow = null)
    {
        var timestamp = utcNow ?? DateTime.UtcNow;
        return Path.Combine(_paths.LogsDir, $"agent_events_{timestamp:yyyyMMdd}.jsonl");
    }
}
