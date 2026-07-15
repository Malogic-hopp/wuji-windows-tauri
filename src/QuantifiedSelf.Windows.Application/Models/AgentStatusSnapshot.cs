using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Runtime;

namespace QuantifiedSelf.Windows.ApplicationLayer.Models;

public sealed class AgentStatusSnapshot
{
    public AgentActualState ActualState { get; init; } = AgentActualState.NotRunning;

    public bool IsRunning { get; init; }

    public bool IsHealthy { get; init; } = true;

    public bool IsStale { get; init; }

    public string StatusText { get; init; } = "Not running";

    public string LastHeartbeatText { get; init; } = "-";

    public string LastSampleText { get; init; } = "-";

    public string ProcessText { get; init; } = "-";

    public RuntimeState? RuntimeState { get; init; }

    public AgentHealthState? HealthState { get; init; }

    public string? CurrentControlCommandText { get; init; }
}
