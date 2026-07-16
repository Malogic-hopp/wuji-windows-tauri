using System.Diagnostics;
using QuantifiedSelf.Windows.Agent.Tests.TestHelpers;

namespace QuantifiedSelf.Windows.Agent.Tests;

[Trait("Category", "Integration")]
public sealed class AgentProcessLifecycleTests
{
    [Fact]
    public async Task Lease_RunsCopiedAgentInUniqueTempRootAndReclaimsProcess()
    {
        var sourceOutput = Path.GetFullPath(AppContext.BaseDirectory);
        var lease = await AgentProcessLease.StartAsync();
        var processId = lease.ProcessId;
        var rootPath = lease.RootPath;

        try
        {
            Assert.StartsWith(
                Path.Combine(Path.GetTempPath(), "WUJI.Tests"),
                lease.RootPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(
                lease.AgentDirectory,
                lease.ExecutablePath,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(lease.ExecutablePath.StartsWith(
                sourceOutput,
                StringComparison.OrdinalIgnoreCase));
            Assert.StartsWith("test-", lease.ChannelName, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(
                lease.DataRootPath,
                "runtime",
                "runtime_state.json")));
        }
        finally
        {
            await lease.DisposeAsync();
        }

        Assert.True(lease.GracefulStopSucceeded);
        Assert.False(lease.UsedKillFallback);
        Assert.False(IsProcessRunning(processId));
        Assert.False(Directory.Exists(rootPath));
    }

    [Fact]
    public async Task JobObject_CloseTerminatesAssignedProcessTree()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /c ping.exe 127.0.0.1 -n 60 > nul",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("Unable to start Job Object probe process.");

        using (var job = WindowsJobObject.CreateKillOnClose())
        {
            job.Assign(process);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.HasExited);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
