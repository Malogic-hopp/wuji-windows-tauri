using QuantifiedSelf.Windows.ApplicationLayer.Contracts.Agent;
using QuantifiedSelf.Windows.Core.Ipc;

namespace QuantifiedSelf.Windows.ApplicationLayer.Agent;

public sealed class AgentTransportHealthService : IAgentTransportHealthService
{
    private readonly TimeProvider _timeProvider;
    private AgentTransportSource _lastCommandSource;

    public AgentTransportHealthService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DateTime? LastIpcSuccessUtc { get; private set; }
    public string? LastIpcError { get; private set; }
    public DateTime? LastFallbackUsedUtc { get; private set; }
    public string LastCommandSource => _lastCommandSource.ToString();
    public string? FullPipeName { get; private set; }
    public string? DisplayPipeName { get; private set; }

    public void Initialize(string fullEndpointName, string displayEndpointName)
    {
        FullPipeName = fullEndpointName;
        DisplayPipeName = displayEndpointName;
        _lastCommandSource = AgentTransportSource.Unavailable;
    }

    public void Initialize(AgentPipeName pipeName)
    {
        ArgumentNullException.ThrowIfNull(pipeName);
        Initialize(pipeName.FullPipeName, pipeName.DisplayPipeName);
    }

    public void RecordIpcSuccess()
    {
        LastIpcSuccessUtc = _timeProvider.GetUtcNow().UtcDateTime;
        LastIpcError = null;
        _lastCommandSource = AgentTransportSource.NamedPipe;
    }

    public void RecordIpcFallback(string? safeError)
    {
        LastFallbackUsedUtc = _timeProvider.GetUtcNow().UtcDateTime;
        LastIpcError = safeError;
        _lastCommandSource = AgentTransportSource.FileFallback;
    }

    public AgentTransportHealthSnapshot GetSnapshot() => new()
    {
        LastCommandSource = _lastCommandSource,
        LastTransportSuccessUtc = LastIpcSuccessUtc,
        LastFallbackUsedUtc = LastFallbackUsedUtc,
        SafeError = LastIpcError,
        DisplayEndpointName = DisplayPipeName
    };

    public string GetDisplayStatusText() => _lastCommandSource switch
    {
        AgentTransportSource.NamedPipe => "IPC connected.",
        AgentTransportSource.FileFallback => "IPC unavailable; using file fallback.",
        _ => "IPC status unknown."
    };
}
