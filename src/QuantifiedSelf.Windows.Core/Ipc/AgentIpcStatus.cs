using System.Text.Json.Serialization;
using QuantifiedSelf.Windows.Core.Control;

namespace QuantifiedSelf.Windows.Core.Ipc;

public sealed class AgentIpcStatus
{
    [JsonPropertyOrder(0)]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentActualState ActualState { get; set; }

    [JsonPropertyOrder(1)]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentDesiredState? DesiredState { get; set; }

    [JsonPropertyOrder(2)]
    public int ProcessId { get; set; }

    [JsonPropertyOrder(3)]
    public DateTime? StartedAtUtc { get; set; }

    [JsonPropertyOrder(4)]
    public DateTime? LastHeartbeatUtc { get; set; }

    [JsonPropertyOrder(5)]
    public DateTime? LastSampleUtc { get; set; }

    [JsonPropertyOrder(6)]
    public long? CurrentSessionId { get; set; }

    [JsonPropertyOrder(7)]
    public string? Version { get; set; }

    [JsonPropertyOrder(8)]
    public bool IsHealthy { get; set; }
}
