using QuantifiedSelf.Windows.Core.Ipc;

namespace QuantifiedSelf.Windows.Infrastructure.Ipc;

public interface IAgentIpcServer
{
    Task StartAsync(string pipeName, Func<AgentIpcRequest, CancellationToken, Task<AgentIpcResponse>> handler, CancellationToken cancellationToken);
}
