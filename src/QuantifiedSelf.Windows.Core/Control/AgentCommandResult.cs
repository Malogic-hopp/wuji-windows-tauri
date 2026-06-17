namespace QuantifiedSelf.Windows.Core.Control;

public sealed class AgentCommandResult
{
    public string RequestId { get; set; } = string.Empty;

    public bool Accepted { get; set; }

    public bool Completed { get; set; }

    public AgentActualState ActualState { get; set; }

    public string? Message { get; set; }

    public string? ErrorCode { get; set; }
}
