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

    public int EventWriteErrorCount { get; set; }

    public int JournalWriteErrorCount { get; set; }

    public string? LastEventWriteError { get; set; }

    public string? LastJournalWriteError { get; set; }

    public DateTime? LastEventWriteErrorUtc { get; set; }

    public DateTime? LastJournalWriteErrorUtc { get; set; }

    // ── Tick-level diagnostics for stale root-cause analysis ──

    public string? LastTickPhase { get; set; }

    public double? LastTickDurationMs { get; set; }

    public double? LastCaptureDurationMs { get; set; }

    public double? LastPersistDurationMs { get; set; }

    public double? LastMaintenanceDurationMs { get; set; }

    public string? LastErrorCode { get; set; }

    public string? LastErrorMessage { get; set; }

    public DateTime? LastSuccessUtc { get; set; }
}
