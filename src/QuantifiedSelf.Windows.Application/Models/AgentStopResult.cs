namespace QuantifiedSelf.Windows.ApplicationLayer.Models;

public sealed class AgentStopResult
{
    public bool IsStopped { get; init; }

    public bool UsedKillFallback { get; init; }
}
