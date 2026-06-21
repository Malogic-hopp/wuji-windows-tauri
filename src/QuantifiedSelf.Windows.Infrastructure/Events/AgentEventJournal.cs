using System.Globalization;
using System.Text.Json;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Core.Serialization;

namespace QuantifiedSelf.Windows.Infrastructure.Events;

public sealed class AgentEventJournal
{
    private readonly WindowsAgentPaths _paths;
    private static readonly JsonSerializerOptions CompactJsonOptions = CreateCompactJsonOptions();

    public AgentEventJournal(WindowsAgentPaths paths)
    {
        _paths = paths;
    }

    public string GetJournalPath(DateTime utcNow)
    {
        return Path.Combine(_paths.LogsDir, $"agent_events_{utcNow:yyyyMMdd}.jsonl");
    }

    public async Task AppendAsync(AgentEvent agentEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        var journalPath = GetJournalPath(agentEvent.EventTimeUtc);
        var directory = Path.GetDirectoryName(journalPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(new
        {
            eventTimeUtc = agentEvent.EventTimeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            eventType = agentEvent.EventType.ToString(),
            eventLevel = agentEvent.EventLevel.ToString(),
            message = agentEvent.Message,
            source = agentEvent.Source,
            requestId = agentEvent.RequestId,
            errorCode = agentEvent.ErrorCode,
            processName = agentEvent.ProcessName,
            sessionId = agentEvent.SessionId,
            payloadJson = agentEvent.PayloadJson
        }, CompactJsonOptions);

        await File.AppendAllTextAsync(journalPath, line + Environment.NewLine, cancellationToken);
    }

    private static JsonSerializerOptions CreateCompactJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializationOptions.Default)
        {
            WriteIndented = false
        };

        return options;
    }
}
