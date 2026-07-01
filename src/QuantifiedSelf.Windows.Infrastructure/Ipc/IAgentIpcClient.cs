using QuantifiedSelf.Windows.Core.Ipc;

namespace QuantifiedSelf.Windows.Infrastructure.Ipc;

public interface IAgentIpcClient
{
    Task<AgentIpcResponse> SendAsync(AgentIpcRequest request, CancellationToken cancellationToken = default);
}
