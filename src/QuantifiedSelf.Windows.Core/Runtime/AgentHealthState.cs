using QuantifiedSelf.Windows.Core.Control;

namespace QuantifiedSelf.Windows.Core.Runtime;

public sealed class AgentHealthState
{
    public AgentActualState ActualState { get; set; } = AgentActualState.Starting;

    public bool IsHealthy { get; set; } = true;

    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSampleUtc { get; set; }

    public DateTime? LastErrorUtc { get; set; }

    public string? Message { get; set; }

    public string? ErrorCode { get; set; }

    public int SampleCountSinceStart { get; set; }

    public int DatabaseWriteErrorCount { get; set; }

    public int CaptureErrorCount { get; set; }

    public long? CurrentSessionId { get; set; }
}
