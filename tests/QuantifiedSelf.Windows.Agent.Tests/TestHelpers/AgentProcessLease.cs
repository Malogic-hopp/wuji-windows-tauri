using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Core.Serialization;
using QuantifiedSelf.Windows.Infrastructure.Ipc;

namespace QuantifiedSelf.Windows.Agent.Tests.TestHelpers;

internal sealed class AgentProcessLease : IAsyncDisposable
{
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "WUJI.Tests");

    private readonly Process _process;
    private readonly WindowsJobObject _job;
    private bool _disposed;

    private AgentProcessLease(
        Process process,
        WindowsJobObject job,
        string rootPath,
        string agentDirectory,
        string dataRootPath,
        string channelName,
        string runId)
    {
        _process = process;
        _job = job;
        RootPath = rootPath;
        AgentDirectory = agentDirectory;
        DataRootPath = dataRootPath;
        ChannelName = channelName;
        RunId = runId;
    }

    public int ProcessId => _process.Id;

    public string ExecutablePath => _process.MainModule?.FileName
        ?? Path.Combine(AgentDirectory, "QuantifiedSelf.Windows.Agent.exe");

    public string RootPath { get; }

    public string AgentDirectory { get; }

    public string DataRootPath { get; }

    public string ChannelName { get; }

    public string RunId { get; }

    public bool GracefulStopSucceeded { get; private set; }

    public bool UsedKillFallback { get; private set; }

    public static async Task<AgentProcessLease> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid().ToString("N");
        var rootPath = Path.Combine(TestRoot, runId);
        var agentDirectory = Path.Combine(rootPath, "Agent");
        var dataRootPath = Path.Combine(rootPath, "Data");
        var channelName = $"test-{runId}";

        Directory.CreateDirectory(agentDirectory);
        Directory.CreateDirectory(dataRootPath);
        CopyAgentOutput(AppContext.BaseDirectory, agentDirectory);

        var executablePath = Path.Combine(agentDirectory, "QuantifiedSelf.Windows.Agent.exe");
        var companionDllPath = Path.Combine(agentDirectory, "QuantifiedSelf.Windows.Agent.dll");
        if (!File.Exists(executablePath) || !File.Exists(companionDllPath))
        {
            DeleteOwnedRoot(rootPath);
            throw new FileNotFoundException(
                "Agent lifecycle tests require a complete Agent apphost output.",
                executablePath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = $"--channel {channelName}",
            WorkingDirectory = agentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.Environment["QUANTIFIEDSELF_WINDOWS_AGENT_ROOT"] = dataRootPath;
        startInfo.Environment["WUJI_RUNTIME_CHANNEL"] = channelName;
        startInfo.Environment["WUJI_TEST_RUN_ID"] = runId;
        startInfo.Environment.Remove("WUJI_AGENT_SHOW_CONSOLE");

        var job = WindowsJobObject.CreateKillOnClose();
        Process? process = null;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start copied test Agent.");
            job.Assign(process);

            var lease = new AgentProcessLease(
                process,
                job,
                rootPath,
                agentDirectory,
                dataRootPath,
                channelName,
                runId);
            await lease.WaitUntilReadyAsync(cancellationToken);
            return lease;
        }
        catch
        {
            if (process is not null)
            {
                await KillAndWaitAsync(process);
                process.Dispose();
            }

            job.Dispose();
            DeleteOwnedRoot(rootPath);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                GracefulStopSucceeded = await RequestGracefulStopAsync();
            }

            if (!_process.HasExited)
            {
                UsedKillFallback = true;
                await KillAndWaitAsync(_process);
            }
        }
        finally
        {
            _job.Dispose();
            _process.Dispose();
            DeleteOwnedRoot(RootPath);
        }
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var runtimeDirectory = Path.Combine(DataRootPath, "runtime");
        var runtimeStatePath = Path.Combine(runtimeDirectory, "runtime_state.json");
        Directory.CreateDirectory(runtimeDirectory);

        var ready = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(runtimeDirectory, "runtime_state.json")
        {
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.CreationTime
                | NotifyFilters.Size,
            EnableRaisingEvents = true
        };

        void ObserveRuntimeState()
        {
            try
            {
                using var stream = new FileStream(
                    runtimeStatePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var state = JsonSerializer.Deserialize<RuntimeState>(
                    stream,
                    JsonSerializationOptions.Default);
                if (state?.ProcessId == ProcessId)
                {
                    ready.TrySetResult(true);
                }
            }
            catch (IOException)
            {
                // A subsequent file event will retry after the atomic replace completes.
            }
            catch (JsonException)
            {
                // A subsequent file event will retry after serialization completes.
            }
        }

        FileSystemEventHandler changedHandler = (_, _) => ObserveRuntimeState();
        RenamedEventHandler renamedHandler = (_, _) => ObserveRuntimeState();
        watcher.Created += changedHandler;
        watcher.Changed += changedHandler;
        watcher.Renamed += renamedHandler;

        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => ready.TrySetException(
            new InvalidOperationException(
                $"Copied test Agent exited before becoming ready with code {_process.ExitCode}."));

        ObserveRuntimeState();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
    }

    private async Task<bool> RequestGracefulStopAsync()
    {
        var userIdentity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var client = new NamedPipeAgentControlClient(
            new AgentPipeName(userIdentity, ChannelName),
            new AgentIpcClientOptions
            {
                ConnectTimeoutMilliseconds = 1000,
                RequestTimeoutMilliseconds = 3000
            });

        try
        {
            await client.SendAsync(new AgentIpcRequest
            {
                Command = "Stop",
                DesiredState = AgentDesiredState.Stopped,
                RequestId = $"test-stop-{RunId}",
                RequestedBy = "QuantifiedSelf.Windows.Agent.Tests",
                WaitForCompletion = false,
                TimeoutMilliseconds = 3000
            });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await _process.WaitForExitAsync(timeout.Token);
            return _process.HasExited;
        }
        catch
        {
            // DisposeAsync applies the PID-scoped kill fallback below.
            return false;
        }
    }

    private static async Task KillAndWaitAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
    }

    private static void CopyAgentOutput(string sourceDirectory, string targetDirectory)
    {
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        foreach (var sourcePath in Directory.EnumerateFiles(
                     sourceRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var targetPath = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: false);
        }
    }

    private static void DeleteOwnedRoot(string rootPath)
    {
        var expectedParent = Path.GetFullPath(TestRoot)
            .TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(rootPath);
        if (!fullRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to delete test root outside {TestRoot}.");
        }

        if (Directory.Exists(fullRoot))
        {
            Directory.Delete(fullRoot, recursive: true);
        }
    }
}
