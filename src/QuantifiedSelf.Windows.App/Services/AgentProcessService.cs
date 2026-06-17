using System.Diagnostics;
using System.IO;
using QuantifiedSelf.Windows.App.Models;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using Microsoft.Extensions.Logging;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class AgentProcessService
{
    private readonly WindowsAgentPaths _paths;
    private readonly RuntimeStateStore _runtimeStateStore;
    private readonly AgentControlFileStore _controlFileStore;
    private readonly ILogger<AgentProcessService> _logger;

    public AgentProcessService(
        WindowsAgentPaths paths,
        RuntimeStateStore runtimeStateStore,
        AgentControlFileStore controlFileStore,
        ILogger<AgentProcessService> logger)
    {
        _paths = paths;
        _runtimeStateStore = runtimeStateStore;
        _controlFileStore = controlFileStore;
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
        var command = new AgentControlCommand
        {
            Command = AgentCommandType.Stop,
            DesiredState = AgentDesiredState.Stopped,
            RequestedBy = "QuantifiedSelf.Windows.App",
            Reason = "User requested stop"
        };

        await _controlFileStore.WriteAsync(_paths.AgentControlPath, command, cancellationToken);

        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (!await IsAgentProcessRunningAsync(cancellationToken))
            {
                return true;
            }

            await Task.Delay(500, cancellationToken);
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
