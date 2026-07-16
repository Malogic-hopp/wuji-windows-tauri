using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Models;
using QuantifiedSelf.Windows.Client.Agent;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using QuantifiedSelf.Windows.Infrastructure.Settings;

namespace QuantifiedSelf.Windows.Tests.TestHelpers;

internal static class AgentTestServices
{
    public static AgentStatusTestDependencies CreateStatusDependencies()
    {
        var paths = new WindowsAgentPaths(Path.GetTempPath());
        var state = new FileAgentStateAdapter(paths);
        return new AgentStatusTestDependencies(
            state,
            new RuntimeStateProcessController(paths, new RuntimeStateStore()));
    }

    public static AgentStatusService CreateStatus(
        WindowsAgentPaths paths,
        RuntimeStateStore runtimeStateStore,
        AgentHealthStateStore healthStateStore,
        AgentControlFileStore controlFileStore,
        WindowsAgentOptionsStore optionsStore,
        IAgentTransport? transport = null,
        AgentTransportHealthService? transportHealth = null)
    {
        var state = new FileAgentStateAdapter(
            paths, runtimeStateStore, healthStateStore, controlFileStore, optionsStore);
        var processController = new RuntimeStateProcessController(paths, runtimeStateStore);
        return new AgentStatusService(
            state, state, state, state, processController, transport, transportHealth);
    }

    public static AgentControlService CreateControl(
        WindowsAgentPaths paths,
        AgentControlFileStore controlFileStore,
        AgentStatusService statusService,
        IAgentTransport? transport = null,
        AgentTransportHealthService? transportHealth = null)
    {
        var state = new FileAgentStateAdapter(paths, controlFileStore: controlFileStore);
        return new AgentControlService(state, statusService, transport, transportHealth);
    }

    public static AgentProcessService CreateProcess(
        WindowsAgentPaths paths,
        RuntimeStateStore runtimeStateStore,
        AgentControlFileStore controlFileStore,
        ILogger<AgentProcessService> logger,
        IAgentTransport? transport = null,
        bool showAgentConsole = false,
        string? channelName = null)
    {
        _ = logger;
        _ = showAgentConsole;
        _ = channelName;
        var state = new FileAgentStateAdapter(
            paths, runtimeStateStore: runtimeStateStore, controlFileStore: controlFileStore);
        var controller = new FakeAgentProcessController(paths, runtimeStateStore);
        return new AgentProcessService(controller, state, state, transport);
    }

    public static AgentProcessService CreateProcess(
        WindowsAgentPaths paths,
        RuntimeStateStore runtimeStateStore,
        AgentControlFileStore controlFileStore,
        ILogger<AgentProcessService> logger,
        out FakeAgentProcessController controller,
        IAgentTransport? transport = null)
    {
        _ = logger;
        var state = new FileAgentStateAdapter(
            paths, runtimeStateStore: runtimeStateStore, controlFileStore: controlFileStore);
        controller = new FakeAgentProcessController(paths, runtimeStateStore);
        return new AgentProcessService(controller, state, state, transport);
    }

    public static WindowsAgentProcessController CreateWindowsProcessController(
        WindowsAgentPaths paths,
        RuntimeStateStore runtimeStateStore,
        bool showAgentConsole = false,
        string? channelName = null) =>
        new(
            paths,
            runtimeStateStore,
            NullLogger<WindowsAgentProcessController>.Instance,
            showAgentConsole,
            channelName);

    private sealed class RuntimeStateProcessController : IAgentProcessController
    {
        private readonly WindowsAgentPaths _paths;
        private readonly RuntimeStateStore _runtimeStateStore;

        public RuntimeStateProcessController(
            WindowsAgentPaths paths,
            RuntimeStateStore runtimeStateStore)
        {
            _paths = paths;
            _runtimeStateStore = runtimeStateStore;
        }

        public Task<AgentProcessInfo> StartAgentAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task KillAgentAsFallbackAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public async Task<bool> IsAgentProcessRunningAsync(CancellationToken cancellationToken = default) =>
            (await GetAgentProcessInfoAsync(cancellationToken))?.IsRunning == true;

        public async Task<AgentProcessInfo?> GetAgentProcessInfoAsync(
            CancellationToken cancellationToken = default)
        {
            var state = await _runtimeStateStore.ReadAsync(_paths.RuntimeStatePath, cancellationToken);
            if (state is null || state.ProcessId <= 0)
            {
                return null;
            }

            try
            {
                using var process = Process.GetProcessById(state.ProcessId);
                return new AgentProcessInfo
                {
                    ProcessId = state.ProcessId,
                    IsRunning = !process.HasExited,
                    StartedAtUtc = state.StartedAtUtc,
                    Version = state.Version,
                    MachineName = state.MachineName,
                    UserName = state.UserName
                };
            }
            catch
            {
                return null;
            }
        }
    }
}

internal sealed class FakeAgentProcessController : IAgentProcessController
{
    private readonly WindowsAgentPaths _paths;
    private readonly RuntimeStateStore _runtimeStateStore;
    private AgentProcessInfo? _current;

    public FakeAgentProcessController(
        WindowsAgentPaths paths,
        RuntimeStateStore runtimeStateStore)
    {
        _paths = paths;
        _runtimeStateStore = runtimeStateStore;
    }

    public int StartCount { get; private set; }

    public int KillCount { get; private set; }

    public AgentProcessInfo? Current => _current;

    public Task<AgentProcessInfo> StartAgentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;

        if (_current?.IsRunning == true)
        {
            return Task.FromResult(_current);
        }

        _current = new AgentProcessInfo
        {
            ProcessId = 900_000 + StartCount,
            IsRunning = true,
            StartedAtUtc = DateTime.UtcNow,
            Version = "test-fake"
        };
        return Task.FromResult(_current);
    }

    public Task KillAgentAsFallbackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        KillCount++;

        if (_current is not null)
        {
            _current = new AgentProcessInfo
            {
                ProcessId = _current.ProcessId,
                IsRunning = false,
                StartedAtUtc = _current.StartedAtUtc,
                Version = _current.Version,
                MachineName = _current.MachineName,
                UserName = _current.UserName
            };
        }

        return Task.CompletedTask;
    }

    public async Task<bool> IsAgentProcessRunningAsync(CancellationToken cancellationToken = default) =>
        (await GetAgentProcessInfoAsync(cancellationToken))?.IsRunning == true;

    public async Task<AgentProcessInfo?> GetAgentProcessInfoAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_current is not null)
        {
            return _current;
        }

        var state = await _runtimeStateStore.ReadAsync(_paths.RuntimeStatePath, cancellationToken);
        if (state is null || state.ProcessId <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(state.ProcessId);
            return new AgentProcessInfo
            {
                ProcessId = state.ProcessId,
                IsRunning = !process.HasExited,
                StartedAtUtc = state.StartedAtUtc,
                Version = state.Version,
                MachineName = state.MachineName,
                UserName = state.UserName
            };
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record AgentStatusTestDependencies(
    FileAgentStateAdapter State,
    IAgentProcessController ProcessController);
