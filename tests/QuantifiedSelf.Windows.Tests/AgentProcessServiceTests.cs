using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Agent;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using QuantifiedSelf.Windows.Tests.TestHelpers;

namespace QuantifiedSelf.Windows.Tests;

[Trait("Category", "Integration")]
public sealed class AgentProcessServiceTests
{
    [Fact]
    public async Task UnifiedStop_WhenAgentIsNotRunning_CompletesWithoutKillFallback()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var service = AgentTestServices.CreateProcess(
            paths,
            new RuntimeStateStore(),
            new AgentControlFileStore(),
            NullLogger<ApplicationLayer.Agent.AgentProcessService>.Instance,
            out var controller,
            new ThrowingTransport());
        service.StopPollMaxAttempts = 1;
        service.StopPollDelayMilliseconds = 0;

        var result = await service.StopAgentAsync();

        Assert.True(result.IsStopped);
        Assert.False(result.UsedKillFallback);
        Assert.Equal(0, controller.KillCount);
    }

    [Fact]
    public async Task UnifiedStop_WhenGracefulStopDoesNotExit_UsesKillAndVerifiesFinalState()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var service = AgentTestServices.CreateProcess(
            paths,
            new RuntimeStateStore(),
            new AgentControlFileStore(),
            NullLogger<ApplicationLayer.Agent.AgentProcessService>.Instance,
            out var controller,
            new ThrowingTransport());
        service.StopPollMaxAttempts = 1;
        service.StopPollDelayMilliseconds = 0;
        await service.StartAgentAsync();

        var result = await service.StopAgentAsync();

        Assert.True(result.IsStopped);
        Assert.True(result.UsedKillFallback);
        Assert.Equal(1, controller.StartCount);
        Assert.Equal(1, controller.KillCount);
        Assert.False(await service.IsAgentProcessRunningAsync());
    }

    [Fact]
    public async Task UnifiedStop_WhenKillCannotExit_ReturnsIncompleteFinalState()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var service = AgentTestServices.CreateProcess(
            paths,
            new RuntimeStateStore(),
            new AgentControlFileStore(),
            NullLogger<ApplicationLayer.Agent.AgentProcessService>.Instance,
            out var controller,
            new ThrowingTransport());
        service.StopPollMaxAttempts = 1;
        service.StopPollDelayMilliseconds = 0;
        await service.StartAgentAsync();
        controller.KeepRunningAfterKill = true;

        var result = await service.StopAgentAsync();

        Assert.False(result.IsStopped);
        Assert.True(result.UsedKillFallback);
        Assert.Equal(1, controller.KillCount);
        Assert.True(await service.IsAgentProcessRunningAsync());
    }

    private sealed class ThrowingTransport : IAgentTransport
    {
        public Task<AgentIpcResponse> SendAsync(
            AgentIpcRequest request,
            CancellationToken cancellationToken = default) =>
            throw new IOException("Test transport is unavailable.");
    }
}
