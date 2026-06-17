using QuantifiedSelf.Windows.Core.Control;

namespace QuantifiedSelf.Windows.Core.Runtime;

public sealed class RuntimeState
{
    public int ProcessId { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSampleUtc { get; set; }

    public AgentActualState State { get; set; } = AgentActualState.Starting;

    public string MachineName { get; set; } = Environment.MachineName;

    public string UserName { get; set; } = Environment.UserName;

    public string Version { get; set; } = "0.1.0";
}
