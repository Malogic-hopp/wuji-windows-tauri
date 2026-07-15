using System.Text.Json;
using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Models;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Core.Serialization;

namespace QuantifiedSelf.Windows.ApplicationLayer.Agent;

public class AgentStatusService : IAgentStatusService
{
    private readonly IAgentRuntimeStateReader _runtimeStateReader;
    private readonly IAgentHealthStateReader _healthStateReader;
    private readonly IAgentControlFallback _controlFallback;
    private readonly IAgentOptionsReader _optionsReader;
    private readonly IAgentProcessController _processController;
    private readonly IAgentTransport? _transport;
    private readonly AgentTransportHealthService? _transportHealth;
    private readonly TimeProvider _timeProvider;

    public AgentStatusService(
        IAgentRuntimeStateReader runtimeStateReader,
        IAgentHealthStateReader healthStateReader,
        IAgentControlFallback controlFallback,
        IAgentOptionsReader optionsReader,
        IAgentProcessController processController,
        IAgentTransport? transport = null,
        AgentTransportHealthService? transportHealth = null,
        TimeProvider? timeProvider = null)
    {
        _runtimeStateReader = runtimeStateReader ?? throw new ArgumentNullException(nameof(runtimeStateReader));
        _healthStateReader = healthStateReader ?? throw new ArgumentNullException(nameof(healthStateReader));
        _controlFallback = controlFallback ?? throw new ArgumentNullException(nameof(controlFallback));
        _optionsReader = optionsReader ?? throw new ArgumentNullException(nameof(optionsReader));
        _processController = processController ?? throw new ArgumentNullException(nameof(processController));
        _transport = transport;
        _transportHealth = transportHealth;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<RuntimeState?> ReadRuntimeStateAsync(CancellationToken cancellationToken = default)
    {
        return await _runtimeStateReader.ReadRuntimeStateAsync(cancellationToken);
    }

    public async Task<AgentHealthState?> ReadHealthStateAsync(CancellationToken cancellationToken = default)
    {
        return await _healthStateReader.ReadHealthStateAsync(cancellationToken);
    }

    public async Task<bool> CheckProcessAsync(CancellationToken cancellationToken = default)
    {
        return await _processController.IsAgentProcessRunningAsync(cancellationToken);
    }

    public async Task<bool> CheckHeartbeatFreshnessAsync(CancellationToken cancellationToken = default)
    {
        var runtimeState = await ReadRuntimeStateAsync(cancellationToken);
        if (runtimeState is null)
        {
            return false;
        }

        var options = await _optionsReader.ReadAgentOptionsAsync(cancellationToken);
        var staleThreshold = TimeSpan.FromSeconds(Math.Max(1, options.StaleThresholdSeconds));
        return _timeProvider.GetUtcNow().UtcDateTime - runtimeState.LastHeartbeatUtc <= staleThreshold;
    }

    public async Task<AgentControlFileReadResult> ReadCurrentCommandAsync(CancellationToken cancellationToken = default)
    {
        return await _controlFallback.ReadCurrentCommandAsync(cancellationToken);
    }

    public virtual async Task<AgentStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // Try IPC first
        if (_transport is not null)
        {
            try
            {
                var ipcRequest = new AgentIpcRequest
                {
                    Command = "GetStatus",
                    RequestId = $"ipc-status-{Guid.NewGuid():N}"
                };

                var ipcResponse = await _transport.SendAsync(ipcRequest, cancellationToken);

                if (ipcResponse is { Accepted: true, Completed: true, Status: not null } && ipcResponse.ErrorCode is null)
                {
                    _transportHealth?.RecordIpcSuccess();

                    var status = ipcResponse.Status;
                    return new AgentStatusSnapshot
                    {
                        ActualState = status.ActualState,
                        IsRunning = true,
                        IsHealthy = status.IsHealthy,
                        IsStale = false,
                        StatusText = status.ActualState.ToString(),
                        LastHeartbeatText = status.LastHeartbeatUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                        LastSampleText = status.LastSampleUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
                        ProcessText = $"PID {status.ProcessId}",
                        RuntimeState = new RuntimeState
                        {
                            ProcessId = status.ProcessId,
                            StartedAtUtc = status.StartedAtUtc ?? DateTime.MinValue,
                            LastHeartbeatUtc = status.LastHeartbeatUtc ?? DateTime.MinValue,
                            LastSampleUtc = status.LastSampleUtc,
                            State = status.ActualState,
                            Version = status.Version ?? "0.1.0"
                        }
                    };
                }

                // IPC responded but status is invalid — fallback
                _transportHealth?.RecordIpcFallback("IPC protocol error; using file fallback.");
            }
            catch (TimeoutException)
            {
                _transportHealth?.RecordIpcFallback("IPC request timed out; using file fallback.");
            }
            catch (Exception)
            {
                _transportHealth?.RecordIpcFallback("IPC unavailable; using file fallback.");
            }
        }

        // Fallback to file-based status
        return await GetFileFallbackStatusAsync(cancellationToken);
    }

    private async Task<AgentStatusSnapshot> GetFileFallbackStatusAsync(CancellationToken cancellationToken)
    {
        var runtimeState = await ReadRuntimeStateAsync(cancellationToken);
        var healthState = await ReadHealthStateAsync(cancellationToken);
        var currentCommand = await ReadCurrentCommandAsync(cancellationToken);
        var processRunning = await CheckProcessAsync(cancellationToken);
        var heartbeatFresh = await CheckHeartbeatFreshnessAsync(cancellationToken);

        var actualState = runtimeState?.State
            ?? healthState?.ActualState
            ?? (processRunning ? AgentActualState.Starting : AgentActualState.NotRunning);

        if (!processRunning && runtimeState is null)
        {
            actualState = AgentActualState.NotRunning;
        }

        if (!processRunning && (runtimeState?.State == AgentActualState.Stopped || healthState?.ActualState == AgentActualState.Stopped))
        {
            actualState = AgentActualState.Stopped;
        }
        else if (!processRunning && runtimeState is not null)
        {
            actualState = AgentActualState.Stale;
        }

        if (processRunning && !heartbeatFresh)
        {
            actualState = AgentActualState.Stale;
        }

        return new AgentStatusSnapshot
        {
            ActualState = actualState,
            IsRunning = processRunning,
            IsHealthy = healthState?.IsHealthy ?? processRunning,
            IsStale = actualState == AgentActualState.Stale,
            StatusText = actualState.ToString(),
            LastHeartbeatText = runtimeState?.LastHeartbeatUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
            LastSampleText = runtimeState?.LastSampleUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-",
            ProcessText = runtimeState is null
                ? "No runtime state"
                : $"PID {runtimeState.ProcessId}",
            RuntimeState = runtimeState,
            HealthState = healthState,
            CurrentControlCommandText = FormatCurrentControlCommand(currentCommand)
        };
    }

    private static string? FormatCurrentControlCommand(AgentControlFileReadResult result)
    {
        if (result.WasMalformed)
        {
            return string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? result.RawText
                : $"Malformed control file: {result.ErrorMessage}\n{result.RawText}";
        }

        return result.Command is null
            ? null
            : JsonSerializer.Serialize(result.Command, JsonSerializationOptions.Default);
    }
}
