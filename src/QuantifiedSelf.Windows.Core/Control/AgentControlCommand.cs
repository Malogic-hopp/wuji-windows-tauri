namespace QuantifiedSelf.Windows.Core.Control;

public sealed class AgentControlCommand
{
    public AgentCommandType Command { get; set; } = AgentCommandType.GetStatus;

    public AgentDesiredState? DesiredState { get; set; }

    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;

    public string RequestedBy { get; set; } = "QuantifiedSelf.Windows.App";

    public bool WaitForCompletion { get; set; }

    public int TimeoutMilliseconds { get; set; } = 5000;

    public string? Reason { get; set; }
}
