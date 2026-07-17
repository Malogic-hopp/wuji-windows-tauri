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
        Assert.Contains("const DEV_CHANNEL_NAME: &str = \"dev\"", supervisor, StringComparison.Ordinal);
        Assert.Contains(".arg(DEV_CHANNEL_NAME)", supervisor, StringComparison.Ordinal);
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
    public void GeneratedSettingsContracts_ExposeOnlyTheFrozenFiveAAllowlist()
    {
        var repositoryRoot = FindRepositoryRoot();
        var typescript = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "contracts",
            "wuji-bridge",
            "v1",
            "generated",
            "typescript",
            "bridge-contracts.generated.ts"));
        var rust = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "contracts",
            "wuji-bridge",
            "v1",
            "generated",
            "rust",
            "bridge_contracts.generated.rs"));

        foreach (var source in new[] { typescript, rust })
        {
            Assert.Contains("SettingsSnapshot", source, StringComparison.Ordinal);
            Assert.Contains("SettingsUpdateParams", source, StringComparison.Ordinal);
            Assert.Contains("sampling", source, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("mask", source, StringComparison.OrdinalIgnoreCase);
            string[] forbidden =
            [
                "StartAppOnWindowsLogin", "start_app_on_windows_login", "startAppOnWindowsLogin",
                "ExcludedProcesses", "excluded_processes", "excludedProcesses",
                "ExcludedTitlePatterns", "excluded_title_patterns", "excludedTitlePatterns",
                "DataRoot", "data_root", "dataRoot", "DatabasePath", "database_path", "databasePath"
            ];
            foreach (var marker in forbidden)
            {
                Assert.DoesNotContain(marker, source, StringComparison.Ordinal);
            }
        }
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
            "settings_get",
            "settings_update",
            "bridge_retry",
            "app_set_unsaved_changes",
            "window_show",
            "window_hide",
            "app_request_exit",
            "app_cancel_close"
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
    public void HostLifecycle_SeparatesHideFromExitAndKeepsAgentIndependent()
    {
        var lifecycle = ReadTauriFile("src-tauri", "src", "lifecycle", "mod.rs");
        var tray = ReadTauriFile("src-tauri", "src", "tray.rs");
        var entrypoint = ReadTauriFile("src-tauri", "src", "lib.rs");

        Assert.Contains("HostLifecycleState::Visible", lifecycle, StringComparison.Ordinal);
        Assert.Contains("HostLifecycleState::HiddenToTray", lifecycle, StringComparison.Ordinal);
        Assert.Contains("HostLifecycleState::ExitConfirmationPending", lifecycle, StringComparison.Ordinal);
        Assert.Contains("HostLifecycleState::ShuttingDown", lifecycle, StringComparison.Ordinal);
        Assert.Contains("api.prevent_close()", lifecycle, StringComparison.Ordinal);
        Assert.Contains("CloseIntent::Hide", lifecycle, StringComparison.Ordinal);
        Assert.Contains("CloseIntent::Exit", lifecycle, StringComparison.Ordinal);
        Assert.Contains("show-main-window", tray, StringComparison.Ordinal);
        Assert.Contains("exit-wuji", tray, StringComparison.Ordinal);
        Assert.Contains("supervisor.shutdown().await", lifecycle, StringComparison.Ordinal);
        Assert.DoesNotContain("agent.stop", lifecycle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("!permits_exit(app_handle)", entrypoint, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeTray_ExposesSafeAgentStatusActionsAndWindowControls()
    {
        var tray = ReadTauriFile("src-tauri", "src", "tray.rs");

        string[] requiredMenuItems =
        [
            "Agent：正在连接…",
            "启动记录",
            "暂停记录",
            "继续记录",
            "停止记录",
            "显示吾迹",
            "隐藏吾迹",
            "退出吾迹"
        ];
        foreach (var item in requiredMenuItems)
        {
            Assert.Contains(item, tray, StringComparison.Ordinal);
        }

        Assert.Contains("agent.getStatus", tray, StringComparison.Ordinal);
        Assert.Contains("agent.start", tray, StringComparison.Ordinal);
        Assert.Contains("agent.pause", tray, StringComparison.Ordinal);
        Assert.Contains("agent.resume", tray, StringComparison.Ordinal);
        Assert.Contains("agent.stop", tray, StringComparison.Ordinal);
        Assert.Contains("STATUS_REFRESH_INTERVAL", tray, StringComparison.Ordinal);
        Assert.Contains("MissedTickBehavior::Skip", tray, StringComparison.Ordinal);
        Assert.Contains("AtomicBool", tray, StringComparison.Ordinal);
        Assert.Contains("PredefinedMenuItem::separator", tray, StringComparison.Ordinal);
        Assert.Contains("AgentState::Running", tray, StringComparison.Ordinal);
        Assert.Contains("AgentState::Paused", tray, StringComparison.Ordinal);
        Assert.Contains("AgentState::Stale", tray, StringComparison.Ordinal);
        Assert.Contains("AgentState::NotRunning", tray, StringComparison.Ordinal);
        Assert.DoesNotContain("windowTitle", tray, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("databasePath", tray, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TauriSingleInstance_UsesADevOnlyMutexAndActivatesTheExistingWindow()
    {
        var singleInstance = ReadTauriFile("src-tauri", "src", "single_instance.rs");
        var entrypoint = ReadTauriFile("src-tauri", "src", "lib.rs");
        using var config = JsonDocument.Parse(ReadTauriFile("src-tauri", "tauri.conf.json"));
        var title = config.RootElement
            .GetProperty("app")
            .GetProperty("windows")[0]
            .GetProperty("title")
            .GetString();

        Assert.Contains("Local\\WUJI.Tauri.Dev.SingleInstance.v1", singleInstance, StringComparison.Ordinal);
        Assert.DoesNotContain("QuantifiedSelf.Windows.Agent", singleInstance, StringComparison.Ordinal);
        Assert.Contains("CreateMutexW", singleInstance, StringComparison.Ordinal);
        Assert.Contains("ERROR_ALREADY_EXISTS", singleInstance, StringComparison.Ordinal);
        Assert.Contains("FindWindowW", singleInstance, StringComparison.Ordinal);
        Assert.Contains("ShowWindowAsync", singleInstance, StringComparison.Ordinal);
        Assert.Contains("SetForegroundWindow", singleInstance, StringComparison.Ordinal);
        Assert.Contains("InstanceDecision::SecondaryActivated => return", entrypoint, StringComparison.Ordinal);
        Assert.Contains("acquire_dev_instance()", entrypoint, StringComparison.Ordinal);
        Assert.Equal("吾迹 · 开发预览", title);
        Assert.Contains("DEV_WINDOW_TITLE: &str = \"吾迹 · 开发预览\"", singleInstance, StringComparison.Ordinal);
    }

    [Fact]
    public void TauriChannel_IsFixedToDevAndCoreIsolationIdentifiersStayDistinctFromProd()
    {
        var supervisor = ReadTauriFile("src-tauri", "src", "bridge", "supervisor.rs");
        var client = ReadTauriFile("src", "bridge", "client.ts");
        var package = ReadTauriFile("package.json");
        var repositoryRoot = FindRepositoryRoot();
        var agentProgram = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "QuantifiedSelf.Windows.Agent",
            "Program.cs"));
        var dev = QuantifiedSelf.Windows.Core.Runtime.RuntimeChannel.Development;
        var prod = QuantifiedSelf.Windows.Core.Runtime.RuntimeChannel.Default;
        var devPipe = new QuantifiedSelf.Windows.Core.Ipc.AgentPipeName("test-user", dev.Name);
        var prodPipe = new QuantifiedSelf.Windows.Core.Ipc.AgentPipeName("test-user", prod.Name);
        var devPaths = new QuantifiedSelf.Windows.Core.Paths.WindowsAgentPaths(channelName: dev.Name);
        var prodPaths = new QuantifiedSelf.Windows.Core.Paths.WindowsAgentPaths(channelName: prod.Name);
        var devAgentMutex = $@"Local\QuantifiedSelf.Windows.Agent.{dev.Name}.test-user";
        var prodAgentMutex = @"Local\QuantifiedSelf.Windows.Agent.test-user";

        Assert.Contains("const DEV_CHANNEL_NAME: &str = \"dev\"", supervisor, StringComparison.Ordinal);
        Assert.Contains(".arg(DEV_CHANNEL_NAME)", supervisor, StringComparison.Ordinal);
        Assert.Contains("initialization.channel_name == DEV_CHANNEL_NAME", supervisor, StringComparison.Ordinal);
        Assert.Contains("!initialization.is_default_channel", supervisor, StringComparison.Ordinal);
        Assert.DoesNotContain("channelName", client, StringComparison.Ordinal);
        Assert.DoesNotContain("executablePath", client, StringComparison.Ordinal);
        Assert.DoesNotContain("dataRoot", client, StringComparison.Ordinal);
        Assert.DoesNotContain("plugin-autostart", package, StringComparison.OrdinalIgnoreCase);

        Assert.False(dev.IsDefault);
        Assert.Equal("WUJI-Dev", dev.DataRootProductFolder);
        Assert.Equal("WUJI Dev", dev.StartupRegistryValueName);
        Assert.Equal("--channel dev", dev.AgentLaunchArguments);
        Assert.NotEqual(prod.DataRootProductFolder, dev.DataRootProductFolder);
        Assert.NotEqual(prod.StartupRegistryValueName, dev.StartupRegistryValueName);
        Assert.NotEqual(prodPipe.FullPipeName, devPipe.FullPipeName);
        Assert.Contains(".dev.", devPipe.FullPipeName, StringComparison.Ordinal);
        Assert.DoesNotContain(".dev.", prodPipe.FullPipeName, StringComparison.Ordinal);
        Assert.NotEqual(prodPaths.Root, devPaths.Root);
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}WUJI-Dev{Path.DirectorySeparatorChar}WindowsAgent",
            devPaths.Root,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(prodAgentMutex, devAgentMutex);
        Assert.Contains("QuantifiedSelf.Windows.Agent.{runtimeChannel.Name}.{userSid}", agentProgram, StringComparison.Ordinal);
    }

    [Fact]
    public void BridgeShutdown_IsBoundedAndWaitsForWorkerCompletion()
    {
        var supervisor = ReadTauriFile("src-tauri", "src", "bridge", "supervisor.rs");

        Assert.Contains("SHUTDOWN_TIMEOUT", supervisor, StringComparison.Ordinal);
        Assert.Contains("ShutdownOutcome::Graceful", supervisor, StringComparison.Ordinal);
        Assert.Contains("ShutdownOutcome::Forced", supervisor, StringComparison.Ordinal);
        Assert.Contains("ShutdownOutcome::AlreadyExited", supervisor, StringComparison.Ordinal);
        Assert.Contains("worker_stopped.changed()", supervisor, StringComparison.Ordinal);
        Assert.Contains("terminate_child", supervisor, StringComparison.Ordinal);
        Assert.Contains("child.kill().await", supervisor, StringComparison.Ordinal);
        Assert.Contains("child.wait().await", supervisor, StringComparison.Ordinal);
        Assert.Contains("kill_on_drop(true)", supervisor, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsDirtyClose_UsesTypedHostCommandsAndSafeFixedEvents()
    {
        var page = ReadTauriFile("src", "pages", "SettingsPage.tsx");
        var client = ReadTauriFile("src", "bridge", "client.ts");
        var events = ReadTauriFile("src", "bridge", "hostLifecycle.ts");

        Assert.Contains("bridgeClient.setUnsavedChanges(dirty)", page, StringComparison.Ordinal);
        Assert.Contains("subscribeHostCloseRequested", page, StringComparison.Ordinal);
        Assert.Contains("bridgeClient.hideWindow()", page, StringComparison.Ordinal);
        Assert.Contains("bridgeClient.requestExit()", page, StringComparison.Ordinal);
        Assert.Contains("bridgeClient.cancelClose()", page, StringComparison.Ordinal);
        Assert.Contains("role=\"alertdialog\"", page, StringComparison.Ordinal);
        Assert.Contains("Agent 会继续独立运行", page, StringComparison.Ordinal);
        Assert.Contains("host://close-requested", events, StringComparison.Ordinal);
        Assert.Contains("invoke<null>(commandWhitelist.windowHide)", client, StringComparison.Ordinal);
        Assert.Contains("invoke<null>(commandWhitelist.requestExit)", client, StringComparison.Ordinal);

        string[] forbiddenDirectAccess =
        [
            "@tauri-apps/api/window",
            "getCurrentWindow",
            "WebviewWindow",
            "invoke("
        ];
        foreach (var token in forbiddenDirectAccess)
        {
            Assert.DoesNotContain(token, page, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("processId", events, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", events, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("['settings', 'current']", invalidation, StringComparison.Ordinal);
        Assert.Contains(
            "invalidateQueries({ queryKey: settingsQueryKey })",
            invalidation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsCommands_AreTypedPassThroughWithDistinctTimeouts()
    {
        var commands = ReadTauriFile("src-tauri", "src", "commands", "mod.rs");
        var supervisor = ReadTauriFile("src-tauri", "src", "bridge", "supervisor.rs");
        var client = ReadTauriFile("src", "bridge", "client.ts");

        Assert.Contains("pub async fn settings_get", commands, StringComparison.Ordinal);
        Assert.Contains("Result<SettingsGetResult, CommandError>", commands, StringComparison.Ordinal);
        Assert.Contains("supervisor.request(\"settings.get\").await", commands, StringComparison.Ordinal);
        Assert.Contains("pub async fn settings_update", commands, StringComparison.Ordinal);
        Assert.Contains("request: SettingsUpdateParams", commands, StringComparison.Ordinal);
        Assert.Contains("Result<SettingsUpdateResult, CommandError>", commands, StringComparison.Ordinal);
        Assert.Contains(
            ".request_with_params(\"settings.update\", request)",
            commands,
            StringComparison.Ordinal);
        Assert.Contains("\"settings.get\" => SETTINGS_READ_TIMEOUT", supervisor, StringComparison.Ordinal);
        Assert.Contains("\"settings.update\" => SETTINGS_UPDATE_TIMEOUT", supervisor, StringComparison.Ordinal);
        Assert.Contains("invoke<SettingsGetResult>(commandWhitelist.settingsGet)", client, StringComparison.Ordinal);
        Assert.Contains("invoke<SettingsUpdateResult>(commandWhitelist.settingsUpdate, { request })", client, StringComparison.Ordinal);

        string[] forbiddenBusinessSemantics =
        [
            "RefreshIntervalSecondsMin",
            "SamplingIntervalSecondsMin",
            "RetentionDaysMax",
            "default_settings",
            "merge_settings",
            "validate_settings"
        ];
        foreach (var semantic in forbiddenBusinessSemantics)
        {
            Assert.DoesNotContain(semantic, commands, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(semantic, supervisor, StringComparison.OrdinalIgnoreCase);
        }
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
    public void ReactSettings_UsesOnlyTypedClientCommandsAndPreservesUnsavedWork()
    {
        var page = ReadTauriFile("src", "pages", "SettingsPage.tsx");
        var view = ReadTauriFile("src", "features", "settings", "SettingsView.tsx");
        var model = ReadTauriFile("src", "features", "settings", "settingsModel.ts");

        Assert.Contains("bridgeClient.getSettings", page, StringComparison.Ordinal);
        Assert.Contains("bridgeClient.updateSettings", page, StringComparison.Ordinal);
        Assert.Contains("settingsQueryKey", page, StringComparison.Ordinal);
        Assert.Contains("useBlocker(dirty)", page, StringComparison.Ordinal);
        Assert.Contains("beforeunload", page, StringComparison.Ordinal);
        Assert.Contains("save.isPending || !draft || !dirty", page, StringComparison.Ordinal);
        Assert.Contains("SettingsLoading", view, StringComparison.Ordinal);
        Assert.Contains("saveState === 'saving'", view, StringComparison.Ordinal);
        Assert.Contains("saveState === 'success'", view, StringComparison.Ordinal);
        Assert.Contains("saveState === 'error'", view, StringComparison.Ordinal);
        Assert.Contains("aria-invalid", view, StringComparison.Ordinal);
        Assert.Contains("aria-describedby", view, StringComparison.Ordinal);
        Assert.Contains("role=\"alertdialog\"", page, StringComparison.Ordinal);
        Assert.Contains("Intl.NumberFormat", model, StringComparison.Ordinal);
        Assert.Contains("refreshQueriesAfterSettingsSaved(queryClient)", page, StringComparison.Ordinal);

        string[] forbiddenAccess =
        [
            "invoke(",
            "readTextFile",
            "writeTextFile",
            "localStorage",
            "@tauri-apps/plugin-fs",
            "@tauri-apps/plugin-sql",
            "Database.load",
            "reg.exe",
            "databasePath"
        ];
        foreach (var token in forbiddenAccess)
        {
            Assert.DoesNotContain(token, page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(token, view, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(token, model, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ReactSettings_UsesCoreDefaultsAndKeepsBusinessValidationOutOfReact()
    {
        var page = ReadTauriFile("src", "pages", "SettingsPage.tsx");
        var model = ReadTauriFile("src", "features", "settings", "settingsModel.ts");
        var css = ReadTauriFile("src", "design-system", "global.css");

        Assert.Contains("settings.data.defaults", page, StringComparison.Ordinal);
        Assert.Contains("parseSettingsDraft", page, StringComparison.Ordinal);
        Assert.Contains("Number.isSafeInteger", model, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshIntervalSecondsMin", model, StringComparison.Ordinal);
        Assert.DoesNotContain("SamplingIntervalSecondsMin", model, StringComparison.Ordinal);
        Assert.DoesNotContain("RetentionDaysMax", model, StringComparison.Ordinal);
        Assert.DoesNotContain("mergeSettings", model, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", css, StringComparison.Ordinal);
        Assert.Contains("@media (forced-colors: active)", css, StringComparison.Ordinal);
        Assert.Contains(".settings-section", css, StringComparison.Ordinal);
        Assert.Contains(".settings-number-control:has(input[aria-invalid=\"true\"])", css, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardParitySmoke_UsesFixedDevBridgeAndEnforcesBundleBudgets()
    {
        using var package = JsonDocument.Parse(ReadTauriFile("package.json"));
        var scripts = package.RootElement.GetProperty("scripts");
        var smoke = ReadTauriFile("scripts", "dashboard-parity-smoke.ps1");
        var budget = ReadTauriFile("scripts", "check-bundle-budget.mjs");

        Assert.Equal("node ./scripts/check-bundle-budget.mjs", scripts.GetProperty("bundle:check").GetString());
        Assert.Equal(
            "powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/dashboard-parity-smoke.ps1",
            scripts.GetProperty("smoke:dashboard-parity").GetString());
        Assert.Contains("src-tauri\\sidecars\\bridge\\QuantifiedSelf.Windows.Client.Bridge.exe", smoke, StringComparison.Ordinal);
        Assert.Contains("Compare the same dev data", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("--channel prod", smoke, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Dashboard route chunk", budget, StringComparison.Ordinal);
        Assert.Contains("Settings route chunk", budget, StringComparison.Ordinal);
        Assert.Contains("gzipSync", budget, StringComparison.Ordinal);
        Assert.Contains("rawKiB: 400", budget, StringComparison.Ordinal);
        Assert.Contains("gzipKiB: 120", budget, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardParityProbe_FixesDevChannelAndOmitsPrivateFieldsFromReports()
    {
        var probe = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "QuantifiedSelf.Windows.DashboardParity",
            "Program.cs"));

        Assert.Contains("private const string ChannelName = \"dev\"", probe, StringComparison.Ordinal);
        Assert.Contains("Agent 正在运行", probe, StringComparison.Ordinal);
        Assert.Contains("windowTitle", probe, StringComparison.Ordinal);
        Assert.Contains("databasePath", probe, StringComparison.Ordinal);
        Assert.Contains("EnumeratePropertyNames", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("AppendLine($\"| 数据库", probe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppendLine($\"| 窗口", probe, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SettingsParitySmoke_ProtectsProdAndRestoresDevSettings()
    {
        using var package = JsonDocument.Parse(ReadTauriFile("package.json"));
        var scripts = package.RootElement.GetProperty("scripts");
        var smoke = ReadTauriFile("scripts", "settings-parity-smoke.ps1");
        var probe = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "QuantifiedSelf.Windows.SettingsParity",
            "Program.cs"));

        Assert.Equal(
            "powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/settings-parity-smoke.ps1",
            scripts.GetProperty("smoke:settings-parity").GetString());
        Assert.Contains("src-tauri\\sidecars\\bridge\\QuantifiedSelf.Windows.Client.Bridge.exe", smoke, StringComparison.Ordinal);
        Assert.Contains("Compare WPF and Tauri against protected dev settings", smoke, StringComparison.Ordinal);
        Assert.Contains("--data-root $workspaceRoot", smoke, StringComparison.Ordinal);
        Assert.Contains("settings-parity-workspace-", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("--channel prod", smoke, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("private const string ChannelName = \"dev\"", probe, StringComparison.Ordinal);
        Assert.Contains("new SettingsViewModel(client.Settings, client.Paths)", probe, StringComparison.Ordinal);
        Assert.Contains("devBackup.Restore()", probe, StringComparison.Ordinal);
        Assert.Contains("TryDeleteOwnedWorkspace", probe, StringComparison.Ordinal);
        Assert.Contains("prodBefore.EquivalentToCurrent()", probe, StringComparison.Ordinal);
        Assert.Contains("RequestErrorAsync", probe, StringComparison.Ordinal);
        Assert.Contains("CrashAsync", probe, StringComparison.Ordinal);
        Assert.Contains("windowTitle", probe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("databasePath", probe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppendLine($\"| 路径", probe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppendLine($\"| 数据库", probe, StringComparison.OrdinalIgnoreCase);
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
