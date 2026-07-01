using System.Text.Json.Serialization;
using QuantifiedSelf.Windows.Core.Control;

namespace QuantifiedSelf.Windows.Core.Ipc;

public sealed class AgentIpcRequest
{
    [JsonPropertyOrder(0)]
    public int ProtocolVersion { get; set; } = 1;

    [JsonPropertyOrder(1)]
    public string RequestId { get; set; } = $"ipc-{Guid.NewGuid():N}";

    [JsonPropertyOrder(2)]
    public string Command { get; set; } = "Ping";

    [JsonPropertyOrder(3)]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AgentDesiredState? DesiredState { get; set; }

    [JsonPropertyOrder(4)]
    public string RequestedBy { get; set; } = "QuantifiedSelf.Windows.App";

    [JsonPropertyOrder(5)]
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyOrder(6)]
    public bool WaitForCompletion { get; set; }

    [JsonPropertyOrder(7)]
    public int TimeoutMilliseconds { get; set; } = 5000;
}
