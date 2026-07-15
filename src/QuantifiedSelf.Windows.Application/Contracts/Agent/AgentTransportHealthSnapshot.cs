namespace QuantifiedSelf.Windows.ApplicationLayer.Contracts.Agent;

public enum AgentTransportSource
{
    Unavailable,
    NamedPipe,
    FileFallback
}

public sealed class AgentTransportHealthSnapshot
{
    public AgentTransportSource LastCommandSource { get; init; }

    public DateTime? LastTransportSuccessUtc { get; init; }

    public DateTime? LastFallbackUsedUtc { get; init; }

    public string? SafeError { get; init; }

    public string? DisplayEndpointName { get; init; }
}
