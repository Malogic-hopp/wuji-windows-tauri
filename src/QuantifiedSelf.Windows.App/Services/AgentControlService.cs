using QuantifiedSelf.Windows.App.Models;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Control;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class AgentControlService
{
    private readonly WindowsAgentPaths _paths;
    private readonly AgentControlFileStore _controlFileStore;
    private readonly AgentStatusService _statusService;

    public AgentControlService(
        WindowsAgentPaths paths,
        AgentControlFileStore controlFileStore,
        AgentStatusService statusService)
    {
        _paths = paths;
        _controlFileStore = controlFileStore;
        _statusService = statusService;
    }

    public Task<AgentCommandResult> RequestPauseAsync(CancellationToken cancellationToken = default)
        => IssueCommandAsync(AgentCommandType.Pause, AgentDesiredState.Paused, cancellationToken);

    public Task<AgentCommandResult> RequestResumeAsync(CancellationToken cancellationToken = default)
        => IssueCommandAsync(AgentCommandType.Resume, AgentDesiredState.Running, cancellationToken);

    public Task<AgentCommandResult> RequestStopAsync(CancellationToken cancellationToken = default)
        => IssueCommandAsync(AgentCommandType.Stop, AgentDesiredState.Stopped, cancellationToken);

    public Task<AgentCommandResult> GetStatusAsync(CancellationToken cancellationToken = default)
        => IssueCommandAsync(AgentCommandType.GetStatus, null, cancellationToken);

    public Task<AgentCommandResult> ReloadConfigAsync(CancellationToken cancellationToken = default)
        => IssueCommandAsync(AgentCommandType.ReloadConfig, null, cancellationToken);

    public Task<AgentCommandResult> UpdateAppMetadataAsync(CancellationToken cancellationToken = default)
        => IssueCommandAsync(AgentCommandType.UpdateAppMetadata, null, cancellationToken);

    public Task<AgentCommandResult> UpdatePrivacyRulesAsync(CancellationToken cancellationToken = default)
        => IssueCommandAsync(AgentCommandType.UpdatePrivacyRules, null, cancellationToken);

    public Task<AgentCommandResult> PruneDataAsync(CancellationToken cancellationToken = default)
        => IssueCommandAsync(AgentCommandType.PruneData, null, cancellationToken);

    public Task<AgentCommandResult> ClearHistoryAsync(CancellationToken cancellationToken = default)
        => IssueCommandAsync(AgentCommandType.ClearHistory, null, cancellationToken);

    public async Task<AgentControlFileReadResult> ReadCurrentCommandAsync(CancellationToken cancellationToken = default)
    {
        return await _controlFileStore.PeekAsync(_paths.AgentControlPath, cancellationToken);
    }

    private async Task<AgentCommandResult> IssueCommandAsync(
        AgentCommandType commandType,
        AgentDesiredState? desiredState,
        CancellationToken cancellationToken)
    {
        if (commandType == AgentCommandType.ReloadConfig)
        {
            var reloadStatus = await _statusService.GetStatusAsync(cancellationToken);
            if (!reloadStatus.IsRunning)
            {
                return new AgentCommandResult
                {
                    Accepted = false,
                    Completed = false,
                    Message = "Agent is not running. Configuration will take effect on next Agent start.",
                    ActualState = reloadStatus.ActualState
                };
            }
        }

        var command = new AgentControlCommand
        {
            Command = commandType,
            DesiredState = desiredState,
            RequestedBy = "QuantifiedSelf.Windows.App",
            Reason = $"UI requested {commandType}"
        };

        await _controlFileStore.WriteAsync(_paths.AgentControlPath, command, cancellationToken);

        var status = await _statusService.GetStatusAsync(cancellationToken);
        return new AgentCommandResult
        {
            RequestId = command.RequestId,
            Accepted = true,
            Completed = commandType == AgentCommandType.GetStatus,
            ActualState = commandType switch
            {
                AgentCommandType.Pause => AgentActualState.Pausing,
                AgentCommandType.Resume => AgentActualState.Resuming,
                AgentCommandType.Stop => AgentActualState.Stopping,
                AgentCommandType.GetStatus => status.ActualState,
                _ => status.ActualState
            },
            Message = commandType == AgentCommandType.GetStatus
                ? status.StatusText
                : status.IsRunning
                    ? $"Command {commandType} queued"
                    : $"Command {commandType} queued while Agent is not running"
        };
    }
}
