using QuantifiedSelf.Windows.App.Models;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.Ipc;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class AgentControlService
{
    private readonly WindowsAgentPaths _paths;
    private readonly AgentControlFileStore _controlFileStore;
    private readonly AgentStatusService _statusService;
    private readonly IAgentIpcClient? _ipcClient;
    private readonly AgentIpcStatusService? _ipcStatusService;

    public AgentControlService(
        WindowsAgentPaths paths,
        AgentControlFileStore controlFileStore,
        AgentStatusService statusService,
        IAgentIpcClient? ipcClient = null,
        AgentIpcStatusService? ipcStatusService = null)
    {
        _paths = paths;
        _controlFileStore = controlFileStore;
        _statusService = statusService;
        _ipcClient = ipcClient;
        _ipcStatusService = ipcStatusService;
    }

    public Task<AgentCommandResult> RequestPauseAsync(CancellationToken cancellationToken = default)
        => IssueCommandWithIpcAsync("Pause", AgentCommandType.Pause, AgentDesiredState.Paused, cancellationToken);

    public Task<AgentCommandResult> RequestResumeAsync(CancellationToken cancellationToken = default)
        => IssueCommandWithIpcAsync("Resume", AgentCommandType.Resume, AgentDesiredState.Running, cancellationToken);

    public Task<AgentCommandResult> RequestStopAsync(CancellationToken cancellationToken = default)
        => IssueCommandWithIpcAsync("Stop", AgentCommandType.Stop, AgentDesiredState.Stopped, cancellationToken);

    public Task<AgentCommandResult> GetStatusAsync(CancellationToken cancellationToken = default)
        => IssueCommandAsync(AgentCommandType.GetStatus, null, cancellationToken);

    public Task<AgentCommandResult> ReloadConfigAsync(CancellationToken cancellationToken = default)
        => IssueCommandWithIpcAsync("ReloadConfig", AgentCommandType.ReloadConfig, null, cancellationToken);

    public Task<AgentCommandResult> UpdateAppMetadataAsync(CancellationToken cancellationToken = default)
        => IssueCommandAsync(AgentCommandType.UpdateAppMetadata, null, cancellationToken);

    public Task<AgentCommandResult> UpdatePrivacyRulesAsync(CancellationToken cancellationToken = default)
        => IssueCommandAsync(AgentCommandType.UpdatePrivacyRules, null, cancellationToken);

    public Task<AgentCommandResult> PruneDataAsync(CancellationToken cancellationToken = default)
        => IssueCommandWithIpcAsync("PruneData", AgentCommandType.PruneData, null, cancellationToken);

    public Task<AgentCommandResult> ClearHistoryAsync(CancellationToken cancellationToken = default)
        => IssueCommandWithIpcAsync("ClearHistory", AgentCommandType.ClearHistory, null, cancellationToken);

    public async Task<AgentControlFileReadResult> ReadCurrentCommandAsync(CancellationToken cancellationToken = default)
    {
        return await _controlFileStore.PeekAsync(_paths.AgentControlPath, cancellationToken);
    }

    private async Task<AgentCommandResult> IssueCommandWithIpcAsync(
        string ipcCommand,
        AgentCommandType commandType,
        AgentDesiredState? desiredState,
        CancellationToken cancellationToken)
    {
        // Try IPC first
        if (_ipcClient is not null)
        {
            var requestId = $"ipc-{Guid.NewGuid():N}";
            var isMaintenance = commandType is AgentCommandType.PruneData or AgentCommandType.ClearHistory;

            try
            {
                var ipcResult = await _ipcClient.SendAsync(new AgentIpcRequest
                {
                    Command = ipcCommand,
                    RequestId = requestId,
                    RequestedBy = "QuantifiedSelf.Windows.App",
                    RequestedAtUtc = DateTime.UtcNow,
                    DesiredState = desiredState,
                    WaitForCompletion = true,
                    TimeoutMilliseconds = isMaintenance ? 30000 : 5000
                }, cancellationToken);

                // IPC responded — map result directly, regardless of Completed
                // (Completed=false means Agent processed but rejected, e.g. AlreadyInMaintenance)
                _ipcStatusService?.RecordIpcSuccess();
                return new AgentCommandResult
                {
                    RequestId = ipcResult.RequestId,
                    Accepted = ipcResult.Accepted,
                    Completed = ipcResult.Completed,
                    ActualState = ipcResult.ActualState,
                    Message = ipcResult.Message,
                    ErrorCode = ipcResult.ErrorCode
                };
            }
            catch (TimeoutException)
            {
                // Don't fallback with a different command — agent may have already acted
                _ipcStatusService?.RecordIpcFallback("IPC request timed out.");
                return new AgentCommandResult
                {
                    RequestId = requestId,
                    Accepted = true,
                    Completed = false,
                    Message = "IPC request timed out. Check Diagnostics for result.",
                    ErrorCode = "IpcTimeout"
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller cancelled — don't fallback
                return new AgentCommandResult
                {
                    RequestId = requestId,
                    Accepted = false,
                    Completed = false,
                    Message = "Request cancelled.",
                    ErrorCode = "Cancelled"
                };
            }
            catch
            {
                // IPC unavailable — fallback with same requestId for dedup
                _ipcStatusService?.RecordIpcFallback("IPC unavailable; using file fallback.");
                return await IssueCommandAsync(commandType, desiredState, cancellationToken, requestId);
            }
        }

        // Fallback to file
        return await IssueCommandAsync(commandType, desiredState, cancellationToken);
    }

    private async Task<AgentCommandResult> IssueCommandAsync(
        AgentCommandType commandType,
        AgentDesiredState? desiredState,
        CancellationToken cancellationToken,
        string? overrideRequestId = null)
    {
        if (commandType == AgentCommandType.ReloadConfig
            || commandType == AgentCommandType.PruneData
            || commandType == AgentCommandType.ClearHistory)
        {
            var checkStatus = await _statusService.GetStatusAsync(cancellationToken);
            if (!checkStatus.IsRunning)
            {
                var message = commandType == AgentCommandType.ReloadConfig
                    ? "Agent is not running. Configuration will take effect on next Agent start."
                    : "Agent is not running. Data cleanup requires a running Agent.";
                return new AgentCommandResult
                {
                    Accepted = false,
                    Completed = false,
                    Message = message,
                    ActualState = checkStatus.ActualState
                };
            }
        }

        var command = new AgentControlCommand
        {
            Command = commandType,
            DesiredState = desiredState,
            RequestId = overrideRequestId ?? Guid.NewGuid().ToString("N"),
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
