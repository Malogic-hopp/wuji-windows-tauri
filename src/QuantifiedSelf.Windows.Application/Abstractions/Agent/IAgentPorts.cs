using QuantifiedSelf.Windows.ApplicationLayer.Models;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Runtime;

namespace QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Agent;

public interface IAgentTransport
{
    Task<AgentIpcResponse> SendAsync(
        AgentIpcRequest request,
        CancellationToken cancellationToken = default);
}

public interface IAgentRuntimeStateReader
{
    Task<RuntimeState?> ReadRuntimeStateAsync(CancellationToken cancellationToken = default);
}

public interface IAgentHealthStateReader
{
    Task<AgentHealthState?> ReadHealthStateAsync(CancellationToken cancellationToken = default);
}

public interface IAgentControlFallback
{
    Task WriteControlCommandAsync(
        AgentControlCommand command,
        CancellationToken cancellationToken = default);

    Task<AgentControlFileReadResult> ReadCurrentCommandAsync(
        CancellationToken cancellationToken = default);
}

public interface IAgentOptionsReader
{
    Task<WindowsAgentOptions> ReadAgentOptionsAsync(CancellationToken cancellationToken = default);
}

public interface IAgentProcessController
{
    Task<AgentProcessInfo> StartAgentAsync(CancellationToken cancellationToken = default);

    Task KillAgentAsFallbackAsync(CancellationToken cancellationToken = default);

    Task<bool> IsAgentProcessRunningAsync(CancellationToken cancellationToken = default);

    Task<AgentProcessInfo?> GetAgentProcessInfoAsync(CancellationToken cancellationToken = default);
}
