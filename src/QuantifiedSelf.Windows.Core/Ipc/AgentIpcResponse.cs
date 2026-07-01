using System.Text.Json.Serialization;
using QuantifiedSelf.Windows.Core.Control;

namespace QuantifiedSelf.Windows.Core.Ipc;

public sealed class AgentIpcResponse
{
    [JsonPropertyOrder(0)]
    public int ProtocolVersion { get; set; } = 1;

    [JsonPropertyOrder(1)]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyOrder(2)]
    public bool Accepted { get; set; }

    [JsonPropertyOrder(3)]
    public bool Completed { get; set; }

    [JsonPropertyOrder(4)]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentActualState ActualState { get; set; } = AgentActualState.NotRunning;

    [JsonPropertyOrder(5)]
    public string? Message { get; set; }

    [JsonPropertyOrder(6)]
    public string? ErrorCode { get; set; }

    [JsonPropertyOrder(7)]
    public DateTime StartedAtUtc { get; set; }

    [JsonPropertyOrder(8)]
    public DateTime CompletedAtUtc { get; set; }

    [JsonPropertyOrder(9)]
    public AgentIpcStatus? Status { get; set; }
}
