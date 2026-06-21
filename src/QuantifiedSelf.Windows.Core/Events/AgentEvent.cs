namespace QuantifiedSelf.Windows.Core.Events;

public sealed class AgentEvent
{
    public long Id { get; set; }

    public DateTime EventTimeUtc { get; set; } = DateTime.UtcNow;

    public AgentEventType EventType { get; set; }

    public AgentEventLevel EventLevel { get; set; } = AgentEventLevel.Info;

    public string Message { get; set; } = string.Empty;

    public string? Source { get; set; }

    public string? RequestId { get; set; }

    public string? ErrorCode { get; set; }

    public string? ProcessName { get; set; }

    public long? SessionId { get; set; }

    public string? PayloadJson { get; set; }
}
