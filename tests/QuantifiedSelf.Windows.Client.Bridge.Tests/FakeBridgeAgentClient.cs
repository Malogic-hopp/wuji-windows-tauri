using QuantifiedSelf.Windows.ApplicationLayer.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Contracts.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Models;
using QuantifiedSelf.Windows.Client;
using QuantifiedSelf.Windows.Core.Control;

namespace QuantifiedSelf.Windows.Client.Bridge.Tests;

internal sealed class FakeBridgeAgentClient : IAgentClient, IAgentStatusService,
    IAgentControlService, IAgentProcessService, IAgentTransportHealthService
{
    public AgentStatusSnapshot CurrentStatus { get; set; } = new();

    public AgentStopResult StopResult { get; set; } = new()
    {
        IsStopped = true
    };

    public Func<CancellationToken, Task<AgentStatusSnapshot>>? StatusHandler { get; set; }

    public Func<CancellationToken, Task<AgentCommandResult>>? PauseHandler { get; set; }

    public Func<CancellationToken, Task<AgentCommandResult>>? ResumeHandler { get; set; }

    public int StatusCount { get; private set; }

    public int StartCount { get; private set; }

    public int PauseCount { get; private set; }

    public int ResumeCount { get; private set; }

    public int StopCount { get; private set; }

    IAgentStatusService IAgentClient.Status => this;

    IAgentControlService IAgentClient.Control => this;

    IAgentProcessService IAgentClient.Process => this;

    IAgentTransportHealthService IAgentClient.TransportHealth => this;

    async Task<AgentStatusSnapshot> IAgentStatusService.GetStatusAsync(
        CancellationToken cancellationToken)
    {
        StatusCount++;
        return StatusHandler is null
            ? CurrentStatus
            : await StatusHandler(cancellationToken);
    }

    public Task<AgentProcessInfo> StartAgentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        CurrentStatus = new AgentStatusSnapshot
        {
            ActualState = AgentActualState.Running,
            IsRunning = true,
            IsHealthy = true,
            StatusText = "Running"
        };
        return Task.FromResult(new AgentProcessInfo
        {
            ProcessId = 12345,
            IsRunning = true,
            MachineName = "PRIVATE-MACHINE",
            UserName = "PrivateUser"
        });
    }

    public async Task<AgentStopResult> StopAgentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        if (StopResult.IsStopped)
        {
            CurrentStatus = new AgentStatusSnapshot
            {
                ActualState = AgentActualState.Stopped,
                IsRunning = false,
                IsHealthy = true,
                StatusText = "Stopped"
            };
        }

        return await Task.FromResult(StopResult);
    }

    public async Task<AgentCommandResult> RequestPauseAsync(CancellationToken cancellationToken = default)
    {
        PauseCount++;
        return PauseHandler is null
            ? new AgentCommandResult
            {
                Accepted = true,
                Completed = true,
                ActualState = AgentActualState.Paused
            }
            : await PauseHandler(cancellationToken);
    }

    public async Task<AgentCommandResult> RequestResumeAsync(CancellationToken cancellationToken = default)
    {
        ResumeCount++;
        return ResumeHandler is null
            ? new AgentCommandResult
            {
                Accepted = true,
                Completed = true,
                ActualState = AgentActualState.Running
            }
            : await ResumeHandler(cancellationToken);
    }

    public Task<bool> StopAgentGracefullyAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Bridge must use the unified stop use case.");

    public Task KillAgentAsFallbackAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Bridge must not orchestrate the kill fallback.");

    public Task<bool> IsAgentProcessRunningAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CurrentStatus.IsRunning);

    public Task<AgentProcessInfo?> GetAgentProcessInfoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<AgentProcessInfo?>(null);

    public Task<AgentCommandResult> RequestStopAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Bridge must use the unified stop use case.");

    Task<AgentCommandResult> IAgentControlService.GetStatusAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<AgentCommandResult> ReloadConfigAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AgentCommandResult> UpdateAppMetadataAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AgentCommandResult> UpdatePrivacyRulesAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AgentCommandResult> PruneDataAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AgentCommandResult> ClearHistoryAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AgentControlFileReadResult> ReadCurrentCommandAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public AgentTransportHealthSnapshot GetSnapshot() => new();

    public string GetDisplayStatusText() => "IPC status unknown.";
}
