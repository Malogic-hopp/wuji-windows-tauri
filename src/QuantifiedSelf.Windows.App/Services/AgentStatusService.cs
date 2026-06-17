using System.Diagnostics;
using System.Text.Json;
using QuantifiedSelf.Windows.App.Models;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Core.Serialization;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using QuantifiedSelf.Windows.Infrastructure.Settings;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class AgentStatusService
{
    private readonly WindowsAgentPaths _paths;
    private readonly RuntimeStateStore _runtimeStateStore;
    private readonly AgentHealthStateStore _healthStateStore;
    private readonly AgentControlFileStore _controlFileStore;
    private readonly WindowsAgentOptionsStore _optionsStore;

    public AgentStatusService(
        WindowsAgentPaths paths,
        RuntimeStateStore runtimeStateStore,
        AgentHealthStateStore healthStateStore,
        AgentControlFileStore controlFileStore,
        WindowsAgentOptionsStore optionsStore)
    {
        _paths = paths;
        _runtimeStateStore = runtimeStateStore;
        _healthStateStore = healthStateStore;
        _controlFileStore = controlFileStore;
        _optionsStore = optionsStore;
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

    public async Task<AgentControlCommand?> ReadCurrentCommandAsync(CancellationToken cancellationToken = default)
    {
        var result = await _controlFileStore.ReadAsync(_paths.AgentControlPath, cancellationToken);
        return result.Command;
    }

    public async Task<AgentStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
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
            CurrentControlCommandText = currentCommand is null ? null : JsonSerializer.Serialize(currentCommand, JsonSerializationOptions.Default)
        };
    }
}
