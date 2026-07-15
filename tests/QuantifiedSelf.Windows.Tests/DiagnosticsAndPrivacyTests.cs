using System.IO;
using QuantifiedSelf.Windows.ApplicationLayer.Models;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.Agent.Services;
using QuantifiedSelf.Windows.Core.Capture;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Core.Serialization;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Tests.TestHelpers;

namespace QuantifiedSelf.Windows.Tests;

public sealed class DiagnosticsAndPrivacyTests
{
    [Trait("Category", "Integration")]
    [Fact]
    public async Task AgentControlFileStore_RoundsTripStringEnums_AndFlagsMalformedFiles()
    {
        using var workspace = new TempWorkspace();
        var store = new AgentControlFileStore();
        var controlPath = Path.Combine(workspace.Root, "runtime", "agent_control.json");

        var command = new AgentControlCommand
        {
            Command = AgentCommandType.Pause,
            DesiredState = AgentDesiredState.Paused
        };

        await store.WriteAsync(controlPath, command);

        var raw = await File.ReadAllTextAsync(controlPath);
        Assert.Contains("\"command\": \"Pause\"", raw);
        Assert.Contains("\"desiredState\": \"Paused\"", raw);

        var readResult = await store.PeekAsync(controlPath);
        Assert.False(readResult.WasMalformed);
        Assert.NotNull(readResult.Command);
        Assert.Equal(AgentCommandType.Pause, readResult.Command!.Command);
        Assert.Equal(AgentDesiredState.Paused, readResult.Command.DesiredState);

        await File.WriteAllTextAsync(controlPath, "{ not json");
        var peekResult = await store.PeekAsync(controlPath);

        Assert.True(peekResult.WasMalformed);
        Assert.Null(peekResult.Command);
        Assert.True(File.Exists(controlPath));
        Assert.False(File.Exists(controlPath + ".bad"));

        var agentReadResult = await store.ReadForAgentAsync(controlPath);
        Assert.True(agentReadResult.WasMalformed);
        Assert.Null(agentReadResult.Command);
        Assert.False(File.Exists(controlPath));
        Assert.True(File.Exists(controlPath + ".bad"));
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void PrivacyFilter_ExcludesProcesses_AndMasksWindowTitles()
    {
        var filter = new ForegroundSamplePrivacyFilter();
        var options = new WindowsAgentOptions
        {
            MaskWindowTitles = true,
            ExcludedProcesses = ["KeePass"],
            ExcludedTitlePatterns = ["*Secret*"]
        };

        var excludedProcessDecision = filter.Apply(
            new ForegroundSample
            {
                ProcessName = "KeePass",
                WindowTitle = "Vault",
                ActivityState = "Active"
            },
            options);

        Assert.False(excludedProcessDecision.ShouldWriteSample);
        Assert.True(excludedProcessDecision.ShouldCloseOpenSession);
        Assert.Contains("process privacy rule", excludedProcessDecision.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var maskedDecision = filter.Apply(
            new ForegroundSample
            {
                ProcessName = "Code",
                WindowTitle = "Regular Project",
                ActivityState = "Active"
            },
            options);

        Assert.True(maskedDecision.ShouldWriteSample);
        Assert.NotNull(maskedDecision.Sample);
        Assert.Null(maskedDecision.Sample!.WindowTitle);

        var excludedTitleDecision = filter.Apply(
            new ForegroundSample
            {
                ProcessName = "Code",
                WindowTitle = "My Secret Notes",
                ActivityState = "Active"
            },
            options);

        Assert.False(excludedTitleDecision.ShouldWriteSample);
        Assert.True(excludedTitleDecision.ShouldCloseOpenSession);
        Assert.Contains("title privacy rule", excludedTitleDecision.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", excludedTitleDecision.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void DiagnosticMessageSanitizer_RedactsWindowsAndUncPaths()
    {
        var exception = new InvalidOperationException(
            @"Failed to open C:\Users\Alice\secrets\db.sqlite and \\server\share\logs\agent.log");

        var message = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(exception);

        Assert.DoesNotContain(@"C:\Users\Alice\secrets\db.sqlite", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\server\share\logs\agent.log", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<path>", message, StringComparison.Ordinal);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void AgentEventPayloadSanitizer_RemovesForbiddenKeys_AndRedactsStringValues()
    {
        var payloadJson = AgentEventPayloadSanitizer.CreatePayloadJson(
            new Dictionary<string, object?>
            {
                ["errorCode"] = "SampleWriteFailed",
                ["exceptionType"] = "InvalidOperationException",
                ["shortMessage"] = @"Failed at C:\Users\Alice\secrets\db.sqlite",
                ["windowTitle"] = "Secret Bank Account",
                ["rawJson"] = "{ secret: true }",
                ["executablePath"] = @"C:\Users\Alice\AppData\Local\app.exe"
            },
            "errorCode",
            "exceptionType",
            "shortMessage");

        Assert.NotNull(payloadJson);
        Assert.Contains("\"errorCode\": \"SampleWriteFailed\"", payloadJson);
        Assert.Contains("\"exceptionType\": \"InvalidOperationException\"", payloadJson);
        Assert.Contains("\\u003Cpath\\u003E", payloadJson);
        Assert.DoesNotContain("Secret Bank Account", payloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawJson", payloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("executablePath", payloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Users\Alice", payloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_QuotesExecutablePath()
    {
        var builder = new StartupCommandBuilder(() => @"C:\Program Files\WUJI\WUJI.exe");
        var command = builder.BuildCommand();

        Assert.NotNull(command);
        Assert.StartsWith("\"C:", command);
        Assert.Contains("\" --from-autostart --start-hidden", command);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_IncludesAutostartHiddenArgs()
    {
        var builder = new StartupCommandBuilder(() => @"C:\WUJI\app.exe");
        var command = builder.BuildCommand();

        Assert.NotNull(command);
        Assert.Contains("--from-autostart", command);
        Assert.Contains("--start-hidden", command);
        Assert.EndsWith("--start-hidden", command);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_RejectsDotnetHostPath()
    {
        var builder = new StartupCommandBuilder(() => @"C:\Program Files\dotnet\dotnet.exe");
        Assert.False(builder.IsValidProcessPath());
        Assert.Null(builder.BuildCommand());
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_UsesCurrentAppExePath()
    {
        var testPath = @"C:\WUJI\QuantifiedSelf.Windows.App.exe";
        var builder = new StartupCommandBuilder(() => testPath);

        Assert.True(builder.IsValidProcessPath());
        var command = builder.BuildCommand();
        Assert.NotNull(command);
        Assert.Contains(testPath, command);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_NormalizesPathsBeforeComparing()
    {
        var builder = new StartupCommandBuilder(() => @"C:\WUJI\app.exe");

        var registered = @"""C:/WUJI/app.exe"" --from-autostart --start-hidden";
        Assert.True(builder.CommandsMatch(registered));
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_HandlesSpacesAndQuotes()
    {
        var builder = new StartupCommandBuilder(() => @"C:\Program Files\WUJI\My App.exe");

        var registered = @"""C:\Program Files\WUJI\My App.exe"" --from-autostart --start-hidden";
        Assert.True(builder.CommandsMatch(registered));

        var extraSpaces = @"""C:\Program Files\WUJI\My App.exe""   --from-autostart   --start-hidden  ";
        Assert.True(builder.CommandsMatch(extraSpaces));
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_SupportsInjectedProcessPathProvider()
    {
        var captured = "";
        var builder = new StartupCommandBuilder(() =>
        {
            captured = "called";
            return @"C:\Test\WUJI.exe";
        });

        var command = builder.BuildCommand();
        Assert.Equal("called", captured);
        Assert.NotNull(command);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_RejectsEmptyProcessPath()
    {
        var builder = new StartupCommandBuilder(() => "");
        Assert.False(builder.IsValidProcessPath());
        Assert.Null(builder.BuildCommand());
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_RejectsNullProcessPath()
    {
        var builder = new StartupCommandBuilder(() => null!);
        Assert.False(builder.IsValidProcessPath());
        Assert.Null(builder.BuildCommand());
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_RejectsDllPath()
    {
        var builder = new StartupCommandBuilder(() => @"C:\Test\library.dll");
        Assert.False(builder.IsValidProcessPath());
        Assert.Null(builder.BuildCommand());
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_DetectsMissingAutostartArg()
    {
        var builder = new StartupCommandBuilder(() => @"C:\WUJI\app.exe");

        var registered = @"""C:\WUJI\app.exe"" --start-hidden";
        Assert.False(builder.CommandsMatch(registered));
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_DetectsMissingStartHiddenArg()
    {
        var builder = new StartupCommandBuilder(() => @"C:\WUJI\app.exe");

        var registered = @"""C:\WUJI\app.exe"" --from-autostart";
        Assert.False(builder.CommandsMatch(registered));
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_DetectsExePathMismatch()
    {
        var builder = new StartupCommandBuilder(() => @"C:\WUJI\app.exe");

        var registered = @"""C:\Other\different.exe"" --from-autostart --start-hidden";
        Assert.False(builder.CommandsMatch(registered));
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_CommandsMatchIsCaseInsensitive()
    {
        var builder = new StartupCommandBuilder(() => @"C:\WUJI\app.exe");

        var registered = @"""C:\WUJI\APP.EXE"" --FROM-AUTOSTART --START-HIDDEN";
        Assert.True(builder.CommandsMatch(registered));
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_DoesNotMatchAutostartArgPrefix()
    {
        var builder = new StartupCommandBuilder(() => @"C:\WUJI\app.exe");

        var registered = @"""C:\WUJI\app.exe"" --from-autostart-disabled --start-hidden";
        Assert.False(builder.CommandsMatch(registered));
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupCommandBuilder_DoesNotMatchStartHiddenArgPrefix()
    {
        var builder = new StartupCommandBuilder(() => @"C:\WUJI\app.exe");

        var registered = @"""C:\WUJI\app.exe"" --from-autostart --start-hidden-old";
        Assert.False(builder.CommandsMatch(registered));
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void WindowStartupPolicy_ManualLaunchShowsWindow()
    {
        var options = StartupLaunchOptions.Parse([]);
        var policy = WindowStartupPolicy.Decide(options);

        Assert.True(policy.ShouldShowMainWindowOnLaunch);
        Assert.False(policy.ShouldStartHidden);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void WindowStartupPolicy_AutostartHiddenStartsHidden()
    {
        var options = StartupLaunchOptions.Parse(["--from-autostart", "--start-hidden"]);
        var policy = WindowStartupPolicy.Decide(options);

        Assert.False(policy.ShouldShowMainWindowOnLaunch);
        Assert.True(policy.ShouldStartHidden);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void WindowStartupPolicy_StartHiddenAloneIsManual()
    {
        var options = StartupLaunchOptions.Parse(["--start-hidden"]);
        var policy = WindowStartupPolicy.Decide(options);

        Assert.True(policy.ShouldShowMainWindowOnLaunch);
        Assert.False(policy.ShouldStartHidden);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void WindowStartupPolicy_AutostartAloneIsManual()
    {
        var options = StartupLaunchOptions.Parse(["--from-autostart"]);
        var policy = WindowStartupPolicy.Decide(options);

        Assert.True(policy.ShouldShowMainWindowOnLaunch);
        Assert.False(policy.ShouldStartHidden);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void WindowStartupPolicy_DoesNotUseCloseToTrayForAutostartHidden()
    {
        var options = StartupLaunchOptions.Parse(["--from-autostart", "--start-hidden"]);
        var policy = WindowStartupPolicy.Decide(options);

        Assert.False(policy.ShouldShowMainWindowOnLaunch);
        Assert.True(policy.ShouldStartHidden);
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void WindowStartupPolicy_DecideThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => WindowStartupPolicy.Decide(null!));
    }

    [Trait("Category", "Fast")]
    [Fact]
    public void StartupRegistrationDisplayModel_SafeTextDoesNotContainPaths()
    {
        var enabled = StartupRegistrationDisplayModel.FromStatus(StartupRegistrationStatus.Enabled(), LaunchMode.Manual);
        var disabled = StartupRegistrationDisplayModel.FromStatus(StartupRegistrationStatus.Disabled(), LaunchMode.Manual);
        var mismatch = StartupRegistrationDisplayModel.FromStatus(StartupRegistrationStatus.Mismatch("test"), LaunchMode.Manual);
        var error = StartupRegistrationDisplayModel.FromStatus(StartupRegistrationStatus.Error("test"), LaunchMode.Manual);
        var unsupported = StartupRegistrationDisplayModel.FromStatus(StartupRegistrationStatus.Unsupported(), LaunchMode.Manual);

        foreach (var d in new[] { enabled, disabled, mismatch, error, unsupported })
        {
            Assert.DoesNotContain("C:", d.LoginStartupStatusText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:", d.StartupRegistrationSummary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:", d.LastStartupRegistrationErrorText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(":\\", d.LoginStartupStatusText);
            Assert.DoesNotContain(":\\", d.StartupRegistrationSummary);
            Assert.DoesNotContain(":\\", d.LastStartupRegistrationErrorText);
        }
    }
}
