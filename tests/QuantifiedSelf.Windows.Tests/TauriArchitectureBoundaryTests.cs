using System.IO;
using System.Linq;
using System.Text.Json;

namespace QuantifiedSelf.Windows.Tests;

[Trait("Category", "Fast")]
public sealed class TauriArchitectureBoundaryTests
{
    private static readonly string[] ForbiddenWebviewCapabilities =
    [
        "shell:",
        "fs:",
        "http:",
        "sql:",
        "process:",
        "upload:",
        "opener:"
    ];

    [Fact]
    public void ToolchainAndFrontendDependencies_AreExactlyPinned()
    {
        using var package = JsonDocument.Parse(ReadTauriFile("package.json"));
        var root = package.RootElement;

        Assert.Equal("pnpm@11.9.0", root.GetProperty("packageManager").GetString());
        Assert.Equal("24.14.0", root.GetProperty("engines").GetProperty("node").GetString());
        Assert.Equal("11.9.0", root.GetProperty("engines").GetProperty("pnpm").GetString());
        Assert.Equal("19.2.7", root.GetProperty("dependencies").GetProperty("react").GetString());
        Assert.Equal("19.2.7", root.GetProperty("dependencies").GetProperty("react-dom").GetString());
        Assert.Equal("2.11.1", root.GetProperty("dependencies").GetProperty("@tauri-apps/api").GetString());

        var toolchain = ReadTauriFile("rust-toolchain.toml");
        Assert.Contains("channel = \"1.97.0\"", toolchain, StringComparison.Ordinal);
        Assert.Contains("x86_64-pc-windows-msvc", toolchain, StringComparison.Ordinal);
        Assert.Contains("clippy", toolchain, StringComparison.Ordinal);
        Assert.Contains("rustfmt", toolchain, StringComparison.Ordinal);
    }

    [Fact]
    public void TauriShell_IsDevOnlyAndCspRejectsRemoteOrEvaluatedCode()
    {
        using var config = JsonDocument.Parse(ReadTauriFile("src-tauri", "tauri.conf.json"));
        var root = config.RootElement;
        var csp = root.GetProperty("app").GetProperty("security").GetProperty("csp").GetString()!;

        Assert.Equal("com.wuji.windows.dev", root.GetProperty("identifier").GetString());
        Assert.False(root.GetProperty("bundle").GetProperty("active").GetBoolean());
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-src 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https:", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http: ", csp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TauriWindowsBuild_ReusesTheExistingWujiApplicationIcon()
    {
        var tauriIcon = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "QuantifiedSelf.Windows.Tauri",
            "src-tauri",
            "icons",
            "icon.ico");
        var wpfIcon = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "QuantifiedSelf.Windows.App",
            "Resources",
            "app.ico");

        Assert.True(File.Exists(tauriIcon));
        Assert.Equal(File.ReadAllBytes(wpfIcon), File.ReadAllBytes(tauriIcon));
    }

    [Fact]
    public void MainCapability_GrantsNoGenericShellFileNetworkOrSqlAccess()
    {
        var capability = ReadTauriFile("src-tauri", "capabilities", "main.json");
        foreach (var forbidden in ForbiddenWebviewCapabilities)
        {
            Assert.DoesNotContain(forbidden, capability, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("core:default", capability, StringComparison.Ordinal);
    }

    [Fact]
    public void BridgeSupervisor_UsesFixedDevSidecarAndNoWebviewSuppliedPathOrChannel()
    {
        var supervisor = ReadTauriFile("src-tauri", "src", "bridge", "supervisor.rs");
        Assert.Contains("QuantifiedSelf.Windows.Client.Bridge.exe", supervisor, StringComparison.Ordinal);
        Assert.Contains(".arg(\"--channel\")", supervisor, StringComparison.Ordinal);
        Assert.Contains(".arg(\"dev\")", supervisor, StringComparison.Ordinal);
        Assert.DoesNotContain("std::env::args", supervisor, StringComparison.Ordinal);

        var commands = ReadTauriFile("src-tauri", "src", "commands", "mod.rs");
        Assert.DoesNotContain("PathBuf", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("channel:", commands, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("method:", commands, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BridgeRecoverySmoke_KillsOnlyTheValidatedDevBridge()
    {
        using var package = JsonDocument.Parse(ReadTauriFile("package.json"));
        var smokeCommand = package.RootElement
            .GetProperty("scripts")
            .GetProperty("smoke:bridge-recovery")
            .GetString();
        var smoke = ReadTauriFile("scripts", "bridge-recovery-smoke.ps1");

        Assert.Equal(
            "powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/bridge-recovery-smoke.ps1",
            smokeCommand);
        Assert.Contains("Stop-ValidatedBridge", smoke, StringComparison.Ordinal);
        Assert.Contains("$bridge.ParentProcessId -ne $TauriProcessId", smoke, StringComparison.Ordinal);
        Assert.Contains("--channel\\s+dev", smoke, StringComparison.Ordinal);
        Assert.Equal(1, smoke.Split("Stop-Process -Id", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Stop-Process -Name", smoke, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stop-Process -Id $agent", smoke, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetTempPath", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void RustAndTypeScript_ConsumeTheGeneratedContractSource()
    {
        var rust = ReadTauriFile("src-tauri", "src", "contracts.rs");
        var typescript = ReadTauriFile("src", "bridge", "contracts.ts");
        Assert.Contains("generated/rust/bridge_contracts.generated.rs", rust, StringComparison.Ordinal);
        Assert.Contains("generated/typescript/bridge-contracts.generated", typescript, StringComparison.Ordinal);
    }

    [Fact]
    public void BridgeAvailabilityNotification_IsFixedAndContainsNoRuntimePaths()
    {
        var supervisor = ReadTauriFile("src-tauri", "src", "bridge", "supervisor.rs");
        var frontend = ReadTauriFile("src", "bridge", "availability.ts");

        Assert.Contains("bridge://availability", supervisor, StringComparison.Ordinal);
        Assert.Contains("bridge://availability", frontend, StringComparison.Ordinal);
        Assert.Contains("generation", supervisor, StringComparison.Ordinal);
        Assert.DoesNotContain("dataRoot", frontend, StringComparison.Ordinal);
        Assert.DoesNotContain("executablePath", frontend, StringComparison.Ordinal);
        Assert.DoesNotContain("channelName", frontend, StringComparison.Ordinal);
    }

    [Fact]
    public void ReactBridgeClient_InvokesOnlyTheSemanticWhitelist()
    {
        var client = ReadTauriFile("src", "bridge", "client.ts");
        string[] allowedCommands =
        [
            "app_initialize",
            "agent_get_status",
            "agent_start",
            "agent_pause",
            "agent_resume",
            "agent_stop",
            "activity_get_overview",
            "bridge_retry"
        ];

        foreach (var command in allowedCommands)
        {
            Assert.Contains(command, client, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("channelName", client, StringComparison.Ordinal);
        Assert.DoesNotContain("dataRoot", client, StringComparison.Ordinal);
        Assert.DoesNotContain("execute(", client, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivityOverviewCommand_IsTypedPassThroughWithoutRustBusinessSemantics()
    {
        var commands = ReadTauriFile("src-tauri", "src", "commands", "mod.rs");
        var supervisor = ReadTauriFile("src-tauri", "src", "bridge", "supervisor.rs");

        Assert.Contains("pub async fn activity_get_overview", commands, StringComparison.Ordinal);
        Assert.Contains("Result<ActivityOverviewResult, CommandError>", commands, StringComparison.Ordinal);
        Assert.Contains("supervisor.request(\"activity.getOverview\").await", commands, StringComparison.Ordinal);
        Assert.Contains("READ_ONLY_QUERY_TIMEOUT", supervisor, StringComparison.Ordinal);
        Assert.Contains("\"activity.getOverview\" => READ_ONLY_QUERY_TIMEOUT", supervisor, StringComparison.Ordinal);

        string[] forbiddenBusinessOperations =
        [
            "sort_by",
            "sort_unstable",
            ".sum(",
            "total_duration_seconds +",
            "active_duration_seconds +",
            "actual_state =="
        ];
        foreach (var operation in forbiddenBusinessOperations)
        {
            Assert.DoesNotContain(operation, commands, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(operation, supervisor, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void BridgeReady_InvalidatesTheDashboardOverviewQuery()
    {
        var container = ReadTauriFile("src", "features", "agent", "AgentCommandContainer.tsx");
        var invalidation = ReadTauriFile("src", "bridge", "queryInvalidation.ts");

        Assert.Contains("event.state === 'ready'", container, StringComparison.Ordinal);
        Assert.Contains("refreshQueriesAfterBridgeReady(queryClient)", container, StringComparison.Ordinal);
        Assert.Contains("['activity', 'overview']", invalidation, StringComparison.Ordinal);
        Assert.Contains(
            "invalidateQueries({ queryKey: activityOverviewQueryKey })",
            invalidation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReactDashboard_UsesTypedOverviewAndOwnsOnlyPresentationState()
    {
        var page = ReadTauriFile("src", "pages", "DashboardPage.tsx");
        var view = ReadTauriFile("src", "features", "dashboard", "DashboardView.tsx");
        var model = ReadTauriFile("src", "features", "dashboard", "dashboardModel.ts");

        Assert.Contains("bridgeClient.getActivityOverview", page, StringComparison.Ordinal);
        Assert.Contains("activityOverviewQueryKey", page, StringComparison.Ordinal);
        Assert.Contains("kind: 'loading'", model, StringComparison.Ordinal);
        Assert.Contains("kind: 'empty'", model, StringComparison.Ordinal);
        Assert.Contains("kind: 'ready'", model, StringComparison.Ordinal);
        Assert.Contains("kind: 'error'", model, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", view, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("invoke(", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("windowTitle", view, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("processName", view, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReactDashboard_ReducesHiddenRefreshAndSupportsSystemAccessibilityModes()
    {
        var page = ReadTauriFile("src", "pages", "DashboardPage.tsx");
        var model = ReadTauriFile("src", "features", "dashboard", "dashboardModel.ts");
        var visibility = ReadTauriFile("src", "features", "dashboard", "useDocumentVisibility.ts");
        var styles = ReadTauriFile("src", "design-system", "global.css");

        Assert.Contains("refetchIntervalInBackground: true", page, StringComparison.Ordinal);
        Assert.Contains("overviewVisibleRefreshInterval = 15_000", model, StringComparison.Ordinal);
        Assert.Contains("overviewHiddenRefreshInterval = 60_000", model, StringComparison.Ordinal);
        Assert.Contains("visibilitychange", visibility, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", styles, StringComparison.Ordinal);
        Assert.Contains("@media (forced-colors: active)", styles, StringComparison.Ordinal);
        Assert.Contains(".dashboard-state", styles, StringComparison.Ordinal);
        Assert.Contains(".dashboard-module", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void TauriSource_DoesNotAccessSqliteNamedPipeRegistryOrWpf()
    {
        string[] forbiddenMarkers =
        [
            "Microsoft.Data.Sqlite",
            "rusqlite",
            "NamedPipe",
            "RegistryKey",
            "System.Windows",
            "QuantifiedSelf.Windows.Infrastructure"
        ];
        var projectDirectory = Path.Combine(FindRepositoryRoot(), "src", "QuantifiedSelf.Windows.Tauri");
        var sources = Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".rs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}target{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        foreach (var source in sources)
        {
            var content = File.ReadAllText(source);
            foreach (var marker in forbiddenMarkers)
            {
                Assert.DoesNotContain(marker, content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string ReadTauriFile(params string[] segments)
    {
        var path = segments.Aggregate(
            Path.Combine(FindRepositoryRoot(), "src", "QuantifiedSelf.Windows.Tauri"),
            (current, segment) => Path.Combine(current, segment));
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "QuantifiedSelf.Windows.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
