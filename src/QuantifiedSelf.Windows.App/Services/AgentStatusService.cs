using System.Diagnostics;
using System.Text.Json;
using QuantifiedSelf.Windows.App.Models;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Core.Serialization;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.Ipc;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using QuantifiedSelf.Windows.Infrastructure.Settings;

namespace QuantifiedSelf.Windows.App.Services;

public class AgentStatusService
{
    private readonly WindowsAgentPaths _paths;
    private readonly RuntimeStateStore _runtimeStateStore;
    private readonly AgentHealthStateStore _healthStateStore;
    private readonly AgentControlFileStore _controlFileStore;
    private readonly WindowsAgentOptionsStore _optionsStore;
    private readonly IAgentIpcClient? _ipcClient;
    private readonly AgentIpcStatusService? _ipcStatusService;

    public AgentStatusService(
        WindowsAgentPaths paths,
        RuntimeStateStore runtimeStateStore,
        AgentHealthStateStore healthStateStore,
        AgentControlFileStore controlFileStore,
        WindowsAgentOptionsStore optionsStore,
        IAgentIpcClient? ipcClient = null,
        AgentIpcStatusService? ipcStatusService = null)
    {
        _paths = paths;
        _runtimeStateStore = runtimeStateStore;
        _healthStateStore = healthStateStore;
        _controlFileStore = controlFileStore;
        _optionsStore = optionsStore;
        _ipcClient = ipcClient;
        _ipcStatusService = ipcStatusService;
    }

    public async Task<RuntimeState?> ReadRuntimeStateAsync(CancellationToken cancellationToken = default)
    {
        return await _runtimeStateStore.ReadAsync(_paths.RuntimeStatePath, cancellationToken);
    }

    public async Task<AgentHealthState?> ReadHealthStateAsync(CancellationToken cancellationToken = default)
    {
        return await _healthStateStore.ReadAsync(_paths.HealthStatePath, cancellationToken);
    }

    public async Task<bool> CheckProcessAsync(CancellationToken cancellationToken = default)
    {
        var runtimeState = await ReadRuntimeStateAsync(cancellationToken);
        if (runtimeState is null || runtimeState.ProcessId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(runtimeState.ProcessId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> CheckHeartbeatFreshnessAsync(CancellationToken cancellationToken = default)
    {
        var runtimeState = await ReadRuntimeStateAsync(cancellationToken);
        if (runtimeState is null)
        {
            return false;
        }

        var options = await _optionsStore.ReadAsync(_paths.AgentOptionsPath, cancellationToken) ?? new WindowsAgentOptions();
        var staleThreshold = TimeSpan.FromSeconds(Math.Max(1, options.StaleThresholdSeconds));
        return DateTime.UtcNow - runtimeState.LastHeartbeatUtc <= staleThreshold;
    }

    public async Task<AgentControlFileReadResult> ReadCurrentCommandAsync(CancellationToken cancellationToken = default)
    {
        return await _controlFileStore.PeekAsync(_paths.AgentControlPath, cancellationToken);
    }

    public virtual async Task<AgentStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // Try IPC first
        if (_ipcClient is not null)
        {
            try
            {
                var ipcRequest = new AgentIpcRequest
                {
                    Command = "GetStatus",
                    RequestId = $"ipc-status-{Guid.NewGuid():N}"
                };

                var ipcResponse = await _ipcClient.SendAsync(ipcRequest, cancellationToken);

                if (ipcResponse is { Accepted: true, Completed: true, Status: not null } && ipcResponse.ErrorCode is null)
                {
                    _ipcStatusService?.RecordIpcSuccess();

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
                _ipcStatusService?.RecordIpcFallback("IPC protocol error; using file fallback.");
            }
            catch (TimeoutException)
            {
                _ipcStatusService?.RecordIpcFallback("IPC request timed out; using file fallback.");
            }
            catch (Exception)
            {
                _ipcStatusService?.RecordIpcFallback("IPC unavailable; using file fallback.");
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
