using System.Diagnostics;
using System.IO;
using QuantifiedSelf.Windows.App.Models;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.Ipc;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using Microsoft.Extensions.Logging;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class AgentProcessService
{
    private readonly WindowsAgentPaths _paths;
    private readonly RuntimeStateStore _runtimeStateStore;
    private readonly AgentControlFileStore _controlFileStore;
    private readonly IAgentIpcClient? _ipcClient;
    private readonly ILogger<AgentProcessService> _logger;

    internal int StopPollMaxAttempts { get; set; } = 30;
    internal int StopPollDelayMilliseconds { get; set; } = 500;

    public AgentProcessService(
        WindowsAgentPaths paths,
        RuntimeStateStore runtimeStateStore,
        AgentControlFileStore controlFileStore,
        ILogger<AgentProcessService> logger,
        IAgentIpcClient? ipcClient = null)
    {
        _paths = paths;
        _runtimeStateStore = runtimeStateStore;
        _controlFileStore = controlFileStore;
        _ipcClient = ipcClient;
        _logger = logger;
    }

    public async Task<AgentProcessInfo> StartAgentAsync(CancellationToken cancellationToken = default)
    {
        if (await IsAgentProcessRunningAsync(cancellationToken))
        {
            var existing = await GetAgentProcessInfoAsync(cancellationToken);
            if (existing is not null)
            {
                return existing;
            }
        }

        var startInfo = ResolveStartInfo();
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Agent process.");

        _logger.LogInformation("Started Agent process {ProcessId}", process.Id);

        await WaitForRuntimeStateAsync(process.Id, cancellationToken);
        return (await GetAgentProcessInfoAsync(cancellationToken)) ?? new AgentProcessInfo
        {
            ProcessId = process.Id,
            IsRunning = true
        };
    }

    public async Task<bool> StopAgentGracefullyAsync(CancellationToken cancellationToken = default)
    {
        var requestId = $"ipc-stop-{Guid.NewGuid():N}";
        bool ipcDelivered = false;

        // Try IPC stop — send and confirm delivery, then poll for exit
        if (_ipcClient is not null)
        {
            try
            {
                await _ipcClient.SendAsync(new AgentIpcRequest
                {
                    Command = "Stop",
                    RequestId = requestId,
                    RequestedBy = "QuantifiedSelf.Windows.App",
                    WaitForCompletion = false,
                    TimeoutMilliseconds = 2000
                }, cancellationToken);
                ipcDelivered = true;
            }
            catch (TimeoutException)
            {
                // Message may or may not have been delivered.
                // Poll briefly to see if agent is already stopping before deciding on fallback.
                for (var i = 0; i < 6; i++)
                {
                    if (!await IsAgentProcessRunningAsync(cancellationToken))
                    {
                        return true;
                    }

                    var runtimeState = await _runtimeStateStore.ReadAsync(_paths.RuntimeStatePath, cancellationToken);
                    if (runtimeState?.State is AgentActualState.Stopped or AgentActualState.Stopping)
                    {
                        // Agent received the Stop — continue polling for exit, no fallback needed
                        ipcDelivered = true;
                        break;
                    }

                    await Task.Delay(500, cancellationToken);
                }
            }
            catch
            {
                // IPC failed — check if agent already stopped before writing fallback
                if (!await IsAgentProcessRunningAsync(cancellationToken))
                {
                    return true;
                }
            }
        }

        // Write file fallback only if IPC was not delivered, using the same requestId
        if (!ipcDelivered)
        {
            var command = new AgentControlCommand
            {
                Command = AgentCommandType.Stop,
                DesiredState = AgentDesiredState.Stopped,
                RequestId = requestId,
                RequestedBy = "QuantifiedSelf.Windows.App",
                Reason = "User requested stop"
            };
            await _controlFileStore.WriteAsync(_paths.AgentControlPath, command, cancellationToken);
        }

        // Wait for process to exit (common path)
        for (var attempt = 0; attempt < StopPollMaxAttempts; attempt++)
        {
            if (!await IsAgentProcessRunningAsync(cancellationToken))
            {
                return true;
            }

            await Task.Delay(StopPollDelayMilliseconds, cancellationToken);
        }

        return false;
    }

    public async Task KillAgentAsFallbackAsync(CancellationToken cancellationToken = default)
    {
        var info = await GetAgentProcessInfoAsync(cancellationToken);
        if (info?.ProcessId is not int processId)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill Agent process {ProcessId}", processId);
        }
    }

    public async Task<bool> IsAgentProcessRunningAsync(CancellationToken cancellationToken = default)
    {
        var info = await GetAgentProcessInfoAsync(cancellationToken);
        return info?.IsRunning == true;
    }

    public async Task<AgentProcessInfo?> GetAgentProcessInfoAsync(CancellationToken cancellationToken = default)
    {
        var runtimeState = await _runtimeStateStore.ReadAsync(_paths.RuntimeStatePath, cancellationToken);
        if (runtimeState is not null && runtimeState.ProcessId > 0)
        {
            try
            {
                using var process = Process.GetProcessById(runtimeState.ProcessId);
                return new AgentProcessInfo
                {
                    ProcessId = runtimeState.ProcessId,
                    IsRunning = !process.HasExited,
                    StartedAtUtc = runtimeState.StartedAtUtc,
                    Version = runtimeState.Version,
                    MachineName = runtimeState.MachineName,
                    UserName = runtimeState.UserName
                };
            }
            catch
            {
                // Fall through to process-name lookup.
            }
        }

        var processMatches = Process.GetProcessesByName("QuantifiedSelf.Windows.Agent");
        if (processMatches.Length == 0)
        {
            return null;
        }

        try
        {
            using var process = processMatches[0];
            return new AgentProcessInfo
            {
                ProcessId = process.Id,
                IsRunning = !process.HasExited,
                StartedAtUtc = runtimeState?.StartedAtUtc,
                Version = runtimeState?.Version,
                MachineName = runtimeState?.MachineName,
                UserName = runtimeState?.UserName
            };
        }
        catch
        {
            return null;
        }
    }

    private ProcessStartInfo ResolveStartInfo()
    {
        var agentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var executableCandidates = new[]
        {
            Environment.GetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE"),
            Path.Combine(agentRoot, "src", "QuantifiedSelf.Windows.Agent", "bin", "Debug", "net8.0-windows", "QuantifiedSelf.Windows.Agent.exe"),
            Path.Combine(agentRoot, "src", "QuantifiedSelf.Windows.Agent", "bin", "Release", "net8.0-windows", "QuantifiedSelf.Windows.Agent.exe"),
            Path.Combine(agentRoot, "src", "QuantifiedSelf.Windows.Agent", "bin", "Debug", "net8.0-windows", "QuantifiedSelf.Windows.Agent.dll"),
            Path.Combine(agentRoot, "src", "QuantifiedSelf.Windows.Agent", "bin", "Release", "net8.0-windows", "QuantifiedSelf.Windows.Agent.dll")
        };

        var executable = executableCandidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new FileNotFoundException("Unable to locate QuantifiedSelf.Windows.Agent output.", executableCandidates[^1]);
        }

        if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{executable}\"",
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                UseShellExecute = false
            };
        }

        return new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            UseShellExecute = false
        };
    }

    private async Task WaitForRuntimeStateAsync(int processId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var runtimeState = await _runtimeStateStore.ReadAsync(_paths.RuntimeStatePath, cancellationToken);
            if (runtimeState is not null && runtimeState.ProcessId == processId)
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }
    }
}
