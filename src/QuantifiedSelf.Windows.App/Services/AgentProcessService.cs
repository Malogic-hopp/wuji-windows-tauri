using System.Collections;
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
    private readonly bool _showAgentConsole;
    private readonly RuntimeChannel _runtimeChannel;

    internal int StopPollMaxAttempts { get; set; } = 30;
    internal int StopPollDelayMilliseconds { get; set; } = 500;

    public AgentProcessService(
        WindowsAgentPaths paths,
        RuntimeStateStore runtimeStateStore,
        AgentControlFileStore controlFileStore,
        ILogger<AgentProcessService> logger,
        IAgentIpcClient? ipcClient = null,
        bool showAgentConsole = false,
        string? channelName = null)
    {
        _paths = paths;
        _runtimeStateStore = runtimeStateStore;
        _controlFileStore = controlFileStore;
        _ipcClient = ipcClient;
        _logger = logger;
        _showAgentConsole = showAgentConsole;
        _runtimeChannel = RuntimeChannel.Parse(channelName ?? paths.ChannelName);
    }

    public async Task<AgentProcessInfo> StartAgentAsync(CancellationToken cancellationToken = default)
    {
        var runtimeState = await _runtimeStateStore.ReadAsync(_paths.RuntimeStatePath, cancellationToken);
        var existing = await GetAgentProcessInfoAsync(cancellationToken);
        if (existing?.IsRunning == true)
        {
            if (runtimeState?.State == AgentActualState.Stopped
                && runtimeState.ProcessId > 0
                && runtimeState.ProcessId == existing.ProcessId
                && existing.ProcessId is int stoppedProcessId)
            {
                await KillProcessByIdAsync(stoppedProcessId, cancellationToken);
            }
            else
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

                // If the runtime state explicitly says Stopped and its PID no longer
                // exists, trust the state rather than falling through to a potentially
                // unrelated process-name match.
                var state = await _runtimeStateStore.ReadAsync(_paths.RuntimeStatePath, cancellationToken);
                if (state?.State == AgentActualState.Stopped && state.ProcessId > 0)
                {
                    try
                    {
                        using var p = Process.GetProcessById(state.ProcessId);
                        // Process still exists — not stopped yet
                    }
                    catch
                    {
                        // PID does not exist — agent is truly gone
                        return true;
                    }
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

        await KillProcessByIdAsync(processId, cancellationToken);
    }

    private async Task KillProcessByIdAsync(int processId, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken);
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

        var expectedExecutable = ResolveAgentExecutablePath();
        try
        {
            var matchingProcess = string.IsNullOrWhiteSpace(expectedExecutable)
                ? processMatches[0]
                : processMatches.FirstOrDefault(process =>
                    ProcessMatchesExpectedExecutable(process, expectedExecutable));

            if (matchingProcess is null)
            {
                return null;
            }

            using var process = matchingProcess;
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

    private static bool ProcessMatchesExpectedExecutable(Process process, string? expectedExecutable)
    {
        if (string.IsNullOrWhiteSpace(expectedExecutable))
        {
            return true;
        }

        try
        {
            var processPath = process.MainModule?.FileName;
            return string.Equals(
                Path.GetFullPath(processPath ?? string.Empty),
                Path.GetFullPath(expectedExecutable),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the Agent executable path using the following priority:
    /// 1. <paramref name="baseDirectory"/>/Agent (publish directory — Agent dependencies isolated by publish/scripts/publish.ps1)
    /// 2. <paramref name="baseDirectory"/> (legacy co-located publish layout)
    /// 3. Environment variable QUANTIFIEDSELF_WINDOWS_AGENT_EXE
    /// 4. Development build output fallback (bin/Debug or bin/Release under repo)
    /// Returns null if no executable is found anywhere.
    /// </summary>
    /// <param name="baseDirectory">
    /// Optional override for AppContext.BaseDirectory. Used by tests to simulate
    /// different deployment layouts. When null (production), AppContext.BaseDirectory is used.
    /// </param>
    internal static string? ResolveAgentExecutablePath(string? baseDirectory = null)
    {
        var baseDir = baseDirectory ?? AppContext.BaseDirectory;

        // 1. Publish directory — Agent exe isolated under App\Agent so its
        // self-contained dependencies do not conflict with App dependencies.
        var publishSubdirExe = Path.Combine(baseDir, "Agent", "QuantifiedSelf.Windows.Agent.exe");
        if (IsRunnableAgentCandidate(publishSubdirExe))
            return publishSubdirExe;

        // 2. Legacy co-located publish layout.
        var publishExe = Path.Combine(baseDir, "QuantifiedSelf.Windows.Agent.exe");
        if (IsRunnableAgentCandidate(publishExe))
            return publishExe;

        // 3. Environment variable override
        var envExe = Environment.GetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_EXE");
        if (!string.IsNullOrWhiteSpace(envExe) && File.Exists(envExe))
            return envExe;

        // 4. Development build fallback: derive repo root from baseDirectory.
        // App output paths can change with the target framework moniker, so walk
        // upward instead of assuming a fixed bin/Debug/<tfm> depth.
        var agentRoot = ResolveDevelopmentRepositoryRoot(baseDir);
        var devCandidates = new[]
        {
            Path.Combine(agentRoot, "src", "QuantifiedSelf.Windows.Agent", "bin", "Release", "net8.0-windows", "QuantifiedSelf.Windows.Agent.exe"),
            Path.Combine(agentRoot, "src", "QuantifiedSelf.Windows.Agent", "bin", "Debug", "net8.0-windows", "QuantifiedSelf.Windows.Agent.exe"),
            Path.Combine(agentRoot, "src", "QuantifiedSelf.Windows.Agent", "bin", "Release", "net8.0-windows", "QuantifiedSelf.Windows.Agent.dll"),
            Path.Combine(agentRoot, "src", "QuantifiedSelf.Windows.Agent", "bin", "Debug", "net8.0-windows", "QuantifiedSelf.Windows.Agent.dll"),
        };

        return devCandidates.FirstOrDefault(File.Exists);
    }

    private static string ResolveDevelopmentRepositoryRoot(string baseDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        while (current is not null)
        {
            var root = current.FullName;
            if (File.Exists(Path.Combine(root, "QuantifiedSelf.Windows.sln"))
                || Directory.Exists(Path.Combine(root, "src", "QuantifiedSelf.Windows.Agent")))
            {
                return root;
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", ".."));
    }

    private static bool IsRunnableAgentCandidate(string executablePath)
    {
        if (!File.Exists(executablePath))
            return false;

        if (!executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return true;

        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
            return true;

        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(executablePath);
        var companionDll = Path.Combine(directory, fileNameWithoutExtension + ".dll");
        if (File.Exists(companionDll))
            return true;

        var depsJson = Path.Combine(directory, fileNameWithoutExtension + ".deps.json");
        var runtimeConfig = Path.Combine(directory, fileNameWithoutExtension + ".runtimeconfig.json");

        // A framework-dependent apphost with deps/runtimeconfig but no companion dll
        // exits immediately with "The application to execute does not exist".
        // This can appear in the App debug output because the App project references
        // the Agent project with ReferenceOutputAssembly=false.
        if (File.Exists(depsJson) || File.Exists(runtimeConfig))
            return false;

        // No sidecar metadata: allow possible single-file or test fake executables.
        return true;
    }

    internal ProcessStartInfo ResolveStartInfo(string? baseDirectory = null)
    {
        var executable = ResolveAgentExecutablePath(baseDirectory);
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new FileNotFoundException("Unable to locate QuantifiedSelf.Windows.Agent executable.");
        }

        if (executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = JoinArguments($"\"{executable}\"", _runtimeChannel.AgentLaunchArguments),
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
                UseShellExecute = false
            };

            ApplyConsoleWindowPolicy(startInfo);
            ApplySanitizedEnvironment(startInfo);
            ApplyRuntimeChannelEnvironment(startInfo);
            return startInfo;
        }

        var executableStartInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = _runtimeChannel.AgentLaunchArguments ?? string.Empty,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            UseShellExecute = false
        };

        ApplyConsoleWindowPolicy(executableStartInfo);
        ApplySanitizedEnvironment(executableStartInfo);
        ApplyRuntimeChannelEnvironment(executableStartInfo);
        return executableStartInfo;
    }

    private void ApplyRuntimeChannelEnvironment(ProcessStartInfo startInfo)
    {
        if (!_runtimeChannel.IsDefault)
        {
            startInfo.Environment["WUJI_RUNTIME_CHANNEL"] = _runtimeChannel.Name;
        }
    }

    private static string JoinArguments(params string?[] values)
    {
        return string.Join(" ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    internal static void ApplySanitizedEnvironment(
        ProcessStartInfo startInfo,
        IDictionary? sourceEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var sanitized = BuildSanitizedEnvironment(sourceEnvironment ?? Environment.GetEnvironmentVariables());
        startInfo.Environment.Clear();

        foreach (var (key, value) in sanitized)
        {
            startInfo.Environment[key] = value;
        }
    }

    internal static IReadOnlyDictionary<string, string> BuildSanitizedEnvironment(IDictionary sourceEnvironment)
    {
        ArgumentNullException.ThrowIfNull(sourceEnvironment);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in sourceEnvironment)
        {
            if (entry.Key is not string key || string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = entry.Value?.ToString() ?? string.Empty;
            if (!result.TryGetValue(key, out _))
            {
                result[key] = value;
                continue;
            }

            var existingKey = result.Keys.First(existing =>
                string.Equals(existing, key, StringComparison.OrdinalIgnoreCase));

            // Windows environment variables are case-insensitive. Some launchers
            // provide both Path and PATH; prefer the canonical Path spelling.
            if (string.Equals(key, "Path", StringComparison.Ordinal)
                && !string.Equals(existingKey, "Path", StringComparison.Ordinal))
            {
                result.Remove(existingKey);
                result[key] = value;
            }
        }

        return result;
    }

    private void ApplyConsoleWindowPolicy(ProcessStartInfo startInfo)
    {
        if (ShouldShowAgentConsole(_showAgentConsole))
        {
            startInfo.CreateNoWindow = false;
            startInfo.WindowStyle = ProcessWindowStyle.Normal;
            return;
        }

        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
    }

    internal static bool ShouldShowAgentConsole(bool commandLineFlag = false)
    {
        if (commandLineFlag)
        {
            return true;
        }

        var value = Environment.GetEnvironmentVariable("WUJI_AGENT_SHOW_CONSOLE");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
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
