using QuantifiedSelf.Windows.ApplicationLayer.Contracts.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Models;
using QuantifiedSelf.Windows.Core.Control;

namespace QuantifiedSelf.Windows.ApplicationLayer.Agent;

public interface IAgentStatusService
{
    Task<AgentStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default);
}

public interface IAgentControlService
{
    Task<AgentCommandResult> RequestPauseAsync(CancellationToken cancellationToken = default);
    Task<AgentCommandResult> RequestResumeAsync(CancellationToken cancellationToken = default);
    Task<AgentCommandResult> RequestStopAsync(CancellationToken cancellationToken = default);
    Task<AgentCommandResult> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<AgentCommandResult> ReloadConfigAsync(CancellationToken cancellationToken = default);
    Task<AgentCommandResult> UpdateAppMetadataAsync(CancellationToken cancellationToken = default);
    Task<AgentCommandResult> UpdatePrivacyRulesAsync(CancellationToken cancellationToken = default);
    Task<AgentCommandResult> PruneDataAsync(CancellationToken cancellationToken = default);
    Task<AgentCommandResult> ClearHistoryAsync(CancellationToken cancellationToken = default);
    Task<AgentControlFileReadResult> ReadCurrentCommandAsync(CancellationToken cancellationToken = default);
}

public interface IAgentProcessService
{
    Task<AgentProcessInfo> StartAgentAsync(CancellationToken cancellationToken = default);
    Task<bool> StopAgentGracefullyAsync(CancellationToken cancellationToken = default);
    Task KillAgentAsFallbackAsync(CancellationToken cancellationToken = default);
    Task<bool> IsAgentProcessRunningAsync(CancellationToken cancellationToken = default);
    Task<AgentProcessInfo?> GetAgentProcessInfoAsync(CancellationToken cancellationToken = default);
}

public interface IAgentTransportHealthService
{
    AgentTransportHealthSnapshot GetSnapshot();
    string GetDisplayStatusText();
}
