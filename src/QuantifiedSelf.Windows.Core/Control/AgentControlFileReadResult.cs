namespace QuantifiedSelf.Windows.Core.Control;

public sealed class AgentControlFileReadResult
{
    public AgentControlCommand? Command { get; init; }

    public bool WasMalformed { get; init; }

    public string? ErrorMessage { get; init; }
}
