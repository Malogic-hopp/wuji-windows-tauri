using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;

namespace QuantifiedSelf.Windows.Tests;

public sealed class RuntimeChannelTests
{
    [Fact]
    public void RuntimeChannel_DevUsesSeparateProductNames()
    {
        var channel = RuntimeChannel.Parse("dev");

        Assert.Equal("dev", channel.Name);
        Assert.Equal("WUJI Dev", channel.ProductDisplayName);
        Assert.Equal("WUJI-Dev", channel.DataRootProductFolder);
        Assert.Equal("WUJI Dev", channel.StartupRegistryValueName);
        Assert.Equal("--channel dev", channel.AgentLaunchArguments);
    }

    [Fact]
    public void WindowsAgentPaths_DevUsesSeparateLocalAppDataRoot()
    {
        var oldRoot = Environment.GetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_ROOT", null);

            var paths = new WindowsAgentPaths(channelName: "dev");

            Assert.Equal("dev", paths.ChannelName);
            Assert.Contains($"{Path.DirectorySeparatorChar}WUJI-Dev{Path.DirectorySeparatorChar}WindowsAgent", paths.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_ROOT", oldRoot);
        }
    }

    [Fact]
    public void AgentPipeName_DevDoesNotCollideWithDefaultPipe()
    {
        var defaultPipe = new AgentPipeName("S-1-5-21-test");
        var devPipe = new AgentPipeName("S-1-5-21-test", "dev");

        Assert.NotEqual(defaultPipe.FullPipeName, devPipe.FullPipeName);
        Assert.Contains(".dev.", devPipe.FullPipeName, StringComparison.Ordinal);
        Assert.DoesNotContain(".prod.", defaultPipe.FullPipeName, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupLaunchOptions_ParsesChannelAndPreviewFlags()
    {
        var options = StartupLaunchOptions.Parse(["--channel", "dev", "--ui-preview", "--show-agent-console"]);

        Assert.Equal("dev", options.ChannelName);
        Assert.True(options.UsePreviewUi);
        Assert.True(options.ShowAgentConsole);
    }

    [Fact]
    public void StartupCommandBuilder_DevCommandRequiresChannel()
    {
        var builder = new StartupCommandBuilder(() => @"C:\WUJI\QuantifiedSelf.Windows.App.exe", "dev");

        var command = builder.BuildCommand();

        Assert.Equal(@"""C:\WUJI\QuantifiedSelf.Windows.App.exe"" --from-autostart --start-hidden --channel dev", command);
        Assert.True(builder.CommandsMatch(command));
        Assert.False(builder.CommandsMatch(@"""C:\WUJI\QuantifiedSelf.Windows.App.exe"" --from-autostart --start-hidden"));
    }

    [Fact]
    public void StartupCommandBuilder_DefaultDoesNotMatchDevCommand()
    {
        var builder = new StartupCommandBuilder(() => @"C:\WUJI\QuantifiedSelf.Windows.App.exe");
        var devCommand = @"""C:\WUJI\QuantifiedSelf.Windows.App.exe"" --from-autostart --start-hidden --channel dev";

        Assert.False(builder.CommandsMatch(devCommand));
    }

    [Fact]
    public void AgentProcessService_DevStartInfoPassesChannelToAgent()
    {
        using var workspace = new TempDirectory();
        var agentExe = Path.Combine(workspace.Path, "QuantifiedSelf.Windows.Agent.exe");
        File.WriteAllText(agentExe, "fake");

        var paths = new WindowsAgentPaths(workspace.Path, "dev");
        var service = new AgentProcessService(
            paths,
            new RuntimeStateStore(),
            new AgentControlFileStore(),
            NullLogger<AgentProcessService>.Instance,
            channelName: "dev");

        var startInfo = service.ResolveStartInfo(workspace.Path);

        Assert.Equal("--channel dev", startInfo.Arguments);
        Assert.Equal("dev", startInfo.Environment["WUJI_RUNTIME_CHANNEL"]);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "wuji-runtime-channel-tests",
            Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best effort cleanup for Windows file-handle timing.
            }
        }
    }
}
