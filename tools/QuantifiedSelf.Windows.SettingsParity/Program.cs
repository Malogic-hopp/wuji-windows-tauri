using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using QuantifiedSelf.Windows.ApplicationLayer.Settings;
using QuantifiedSelf.Windows.App.ViewModels;
using QuantifiedSelf.Windows.Client;
using QuantifiedSelf.Windows.Client.Startup;
using QuantifiedSelf.Windows.Core.Paths;

return await SettingsParityProgram.RunAsync(args);

internal static class SettingsParityProgram
{
    private const string ChannelName = "dev";

    public static async Task<int> RunAsync(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("Usage: settings-parity --bridge <path> --data-root <path> [--report <path>]");
            return 2;
        }

        var checks = new List<ParityCheck>();
        var reportPath = options.ReportPath ?? CreateDefaultReportPath();
        var devPaths = new WindowsAgentPaths(options.DataRootPath, ChannelName);
        var prodPaths = new WindowsAgentPaths(channelName: "prod");
        var devFiles = TrackedSettingsFiles(devPaths).ToArray();
        var prodFiles = TrackedSettingsFiles(prodPaths).ToArray();
        var devBackup = FileSetSnapshot.Capture(devFiles);
        var prodBefore = FileSetSnapshot.Capture(prodFiles);
        IWujiClient? client = null;
        Exception? failure = null;

        try
        {
            var rootsAreIsolated = !Path.GetFullPath(devPaths.Root).Equals(
                Path.GetFullPath(prodPaths.Root),
                StringComparison.OrdinalIgnoreCase);
            Add(checks, "dev/prod 根目录隔离", rootsAreIsolated, "dev 与 prod 使用不同根目录");
            if (!rootsAreIsolated)
            {
                throw new InvalidOperationException("Runtime channel roots are not isolated.");
            }

            var launchOptions = StartupLaunchOptions.Parse(["--channel", ChannelName]);
            client = WujiClientFactory.Create(WujiClientOptions.FromLaunchOptions(
                launchOptions,
                options.DataRootPath));
            await client.InitializeAsync();
            Add(checks, "WPF Client 固定 dev channel",
                client.Context.ChannelName == ChannelName && !client.Context.IsDefaultChannel,
                "channel=dev, non-default");

            Add(checks, "隔离 dev 设置工作区",
                Path.GetFullPath(client.Paths.Root).Equals(
                    Path.GetFullPath(options.DataRootPath),
                    StringComparison.OrdinalIgnoreCase),
                "使用唯一临时 data root，不读写默认 dev 设置");

            var initialSeed = await client.Settings.UpdateClientSettingsAsync(SafeSettings.Initial.ToApplication());
            if (!initialSeed.IsValid)
            {
                throw new InvalidOperationException("Could not seed isolated dev settings.");
            }

            await using (var firstBridge = await BridgeSession.StartAsync(options.BridgePath, options.DataRootPath))
            {
                var initialization = await firstBridge.RequestResultAsync("client.initialize", new { });
                Add(checks, "Tauri Bridge 固定 dev channel",
                    initialization.GetProperty("channelName").GetString() == ChannelName
                    && !initialization.GetProperty("isDefaultChannel").GetBoolean(),
                    "channel=dev, non-default");

                var initialResult = await firstBridge.RequestResultAsync("settings.get", new { });
                var initialBridge = SafeSettings.FromJson(initialResult.GetProperty("settings"));
                var initialWpf = await ReadWpfSettingsAsync(client);
                Add(checks, "初始设置值一致", initialBridge == initialWpf, "十二个 allowlist 字段一致");

                var bridgeDefaults = SafeSettings.FromJson(initialResult.GetProperty("defaults"));
                var coreDefaults = SafeSettings.FromApplication(client.Settings.GetDefaultClientSettings());
                Add(checks, "Core/Application 默认值一致", bridgeDefaults == coreDefaults,
                    "Bridge defaults 与 WPF 使用的 Core 默认模型一致");
                CheckPrivacy(checks, "初始 settings.get 隐私 allowlist", initialResult);

                var tauriUpdate = SafeSettings.TauriAcceptance;
                var updateResult = await firstBridge.RequestResultAsync(
                    "settings.update",
                    new { settings = tauriUpdate.ToJsonModel() });
                var savedByTauri = SafeSettings.FromJson(updateResult.GetProperty("settings"));
                var readByWpf = await ReadWpfSettingsAsync(client);
                Add(checks, "Tauri 合法更新可由 WPF 读取",
                    updateResult.GetProperty("saved").GetBoolean()
                    && savedByTauri == tauriUpdate
                    && readByWpf == tauriUpdate,
                    "Bridge 保存后 WPF ViewModel 读取一致");
                await firstBridge.ShutdownAsync();
            }

            var wpfViewModel = await LoadWpfViewModelAsync(client);
            ApplyToWpf(wpfViewModel, SafeSettings.WpfAcceptance);
            await wpfViewModel.SaveAppSettingsAsync();
            await wpfViewModel.SaveAgentOptionsAsync();
            Add(checks, "WPF 合法更新完成",
                !wpfViewModel.HasValidationError
                && !wpfViewModel.HasSaveError
                && !wpfViewModel.HasAgentOptionsValidationError
                && !wpfViewModel.HasAgentOptionsSaveError,
                "WPF App/Agent 设置保存无校验或写入错误");

            JsonElement persistedResult;
            JsonElement invalidError;
            await using (var secondBridge = await BridgeSession.StartAsync(options.BridgePath, options.DataRootPath))
            {
                _ = await secondBridge.RequestResultAsync("client.initialize", new { });
                persistedResult = await secondBridge.RequestResultAsync("settings.get", new { });
                var persisted = SafeSettings.FromJson(persistedResult.GetProperty("settings"));
                Add(checks, "WPF 合法更新可由 Tauri 读取", persisted == SafeSettings.WpfAcceptance,
                    "重新启动 Bridge 后十二个字段仍存在");
                Add(checks, "UI/Bridge 重启后设置持久化", persisted == SafeSettings.WpfAcceptance,
                    "新 Bridge generation 重新读取磁盘结果");

                var filesBeforeInvalid = FileSetSnapshot.Capture(devFiles);
                var invalidWpf = await LoadWpfViewModelAsync(client);
                invalidWpf.RefreshIntervalSecondsText = "0";
                await invalidWpf.SaveAppSettingsAsync();
                invalidWpf.SamplingIntervalSecondsText = "0";
                invalidWpf.HeartbeatIntervalSecondsText = "30";
                invalidWpf.StaleThresholdSecondsText = "30";
                await invalidWpf.SaveAgentOptionsAsync();
                Add(checks, "WPF 非法值被拒绝",
                    invalidWpf.HasValidationError && invalidWpf.HasAgentOptionsValidationError,
                    "越界值与 stale/heartbeat 关系均未保存");

                var invalid = SafeSettings.WpfAcceptance with
                {
                    RefreshIntervalSeconds = 0,
                    SamplingIntervalSeconds = 0,
                    HeartbeatIntervalSeconds = 30,
                    StaleThresholdSeconds = 30
                };
                invalidError = await secondBridge.RequestErrorAsync(
                    "settings.update",
                    new { settings = invalid.ToJsonModel() });
                var errorCode = invalidError.GetProperty("code").GetString();
                var errorData = invalidError.GetProperty("data");
                var rejectedFields = errorData.GetProperty("fieldErrors")
                    .EnumerateArray()
                    .Select(item => item.GetProperty("field").GetString())
                    .ToHashSet(StringComparer.Ordinal);
                string[] expectedFields =
                [
                    "appSettings.refreshIntervalSeconds",
                    "agentOptions.samplingIntervalSeconds",
                    "agentOptions.staleThresholdSeconds"
                ];
                Add(checks, "Tauri 非法值被同样拒绝",
                    errorCode == "validation_failed"
                    && expectedFields.All(rejectedFields.Contains)
                    && filesBeforeInvalid.EquivalentToCurrent(),
                    "返回安全字段错误且设置文件未改变");
                CheckPrivacy(checks, "非法响应隐私安全", invalidError);

                await secondBridge.CrashAsync();
            }

            await using (var recoveredBridge = await BridgeSession.StartAsync(options.BridgePath, options.DataRootPath))
            {
                _ = await recoveredBridge.RequestResultAsync("client.initialize", new { });
                var recovered = await recoveredBridge.RequestResultAsync("settings.get", new { });
                Add(checks, "Bridge 断线后可重连并重试",
                    SafeSettings.FromJson(recovered.GetProperty("settings")) == SafeSettings.WpfAcceptance,
                    "新进程恢复后读取到最后一次合法保存");
                await recoveredBridge.ShutdownAsync();
            }

            CheckPrivacy(checks, "持久化 settings.get 隐私 allowlist", persistedResult);
        }
        catch (Exception exception)
        {
            failure = exception;
            Add(checks, "验收流程完整", false,
                $"未完成（{exception.GetType().Name}）");
        }
        finally
        {
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync();
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                    Add(checks, "WPF Client 已释放", false, $"释放未完成（{exception.GetType().Name}）");
                }
            }

            try
            {
                devBackup.Restore();
                Add(checks, "dev 设置已恢复", devBackup.EquivalentToCurrent(),
                    "验收前文件内容与时间戳已恢复");
                var workspaceCleaned = TryDeleteOwnedWorkspace(options.DataRootPath);
                Add(checks, "临时 dev 工作区已清理", workspaceCleaned,
                    "仅删除位于系统临时目录下的本次验收工作区");
            }
            catch (Exception exception)
            {
                failure ??= exception;
                Add(checks, "dev 设置已恢复", false, $"恢复未完成（{exception.GetType().Name}）");
            }

            try
            {
                Add(checks, "prod 设置全程未修改", prodBefore.EquivalentToCurrent(),
                    "prod app/agent 设置及备份指纹不变");
            }
            catch (Exception exception)
            {
                failure ??= exception;
                Add(checks, "prod 设置全程未修改", false, $"指纹复核未完成（{exception.GetType().Name}）");
            }
        }

        await WriteReportAsync(reportPath, checks);
        foreach (var check in checks)
        {
            Console.WriteLine($"[{(check.Passed ? "PASS" : "FAIL")}] {check.Name}: {check.Detail}");
        }
        Console.WriteLine($"报告已写入：{reportPath}");

        return failure is null && checks.All(check => check.Passed) ? 0 : 1;
    }

    private static async Task<SettingsViewModel> LoadWpfViewModelAsync(IWujiClient client)
    {
        var viewModel = new SettingsViewModel(client.Settings, client.Paths);
        await viewModel.LoadAsync();
        return viewModel;
    }

    private static async Task<SafeSettings> ReadWpfSettingsAsync(IWujiClient client) =>
        SafeSettings.FromWpf(await LoadWpfViewModelAsync(client));

    private static void ApplyToWpf(SettingsViewModel viewModel, SafeSettings settings)
    {
        viewModel.SelectedTheme = settings.Theme switch
        {
            "dark" => "Dark",
            "high_contrast" => "HighContrast",
            _ => "Light"
        };
        viewModel.RefreshIntervalSecondsText = settings.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        viewModel.AutoStartAgentWhenAppStarts = settings.AutoStartAgentWhenAppStarts;
        viewModel.SamplingIntervalSecondsText = settings.SamplingIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        viewModel.IdleThresholdSecondsText = settings.IdleThresholdSeconds.ToString(CultureInfo.InvariantCulture);
        viewModel.HeartbeatIntervalSecondsText = settings.HeartbeatIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        viewModel.StaleThresholdSecondsText = settings.StaleThresholdSeconds.ToString(CultureInfo.InvariantCulture);
        viewModel.RetentionDaysText = settings.RetentionDays.ToString(CultureInfo.InvariantCulture);
        viewModel.EnableJsonlJournal = settings.EnableJsonlJournal;
        viewModel.EnableAgentEventJournal = settings.EnableAgentEventJournal;
        viewModel.EnableSessionMerge = settings.EnableSessionMerge;
        viewModel.MaskWindowTitles = settings.MaskWindowTitles;
    }

    private static IEnumerable<string> TrackedSettingsFiles(WindowsAgentPaths paths)
    {
        var app = Path.Combine(paths.ConfigDir, "app-settings.json");
        var agent = paths.AgentOptionsPath;
        yield return app;
        yield return app + ".tmp";
        yield return agent;
        yield return agent + ".bak";
        yield return agent + ".tmp";
    }

    private static bool TryDeleteOwnedWorkspace(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "WUJI.Smoke"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var directoryName = Path.GetFileName(fullPath);
        if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)
            || !directoryName.StartsWith("settings-parity-workspace-", StringComparison.Ordinal))
        {
            return false;
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
        return !Directory.Exists(fullPath);
    }

    private static void CheckPrivacy(ICollection<ParityCheck> checks, string name, JsonElement element)
    {
        string[] forbiddenProperties =
        [
            "startAppOnWindowsLogin", "excludedProcesses", "excludedTitlePatterns",
            "dataRoot", "databasePath", "agentExecutablePath", "pipeName", "mutexName",
            "registryKey", "registryValue", "processName", "windowTitle", "exception", "stackTrace"
        ];
        var names = EnumeratePropertyNames(element).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var safeNames = forbiddenProperties.All(property => !names.Contains(property));
        var safeStrings = EnumerateStrings(element).All(value =>
            !value.Contains(@":\", StringComparison.Ordinal)
            && !value.Contains(@"\\", StringComparison.Ordinal)
            && !value.Contains("/Users/", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("/home/", StringComparison.OrdinalIgnoreCase));
        Add(checks, name, safeNames && safeStrings,
            "无路径、数据库、注册表、IPC、自由文本隐私字段或异常详情");
    }

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumeratePropertyNames(item)) yield return nested;
            }
        }
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            yield return element.GetString() ?? string.Empty;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var nested in EnumerateStrings(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumerateStrings(item)) yield return nested;
            }
        }
    }

    private static async Task WriteReportAsync(string path, IReadOnlyList<ParityCheck> checks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var builder = new StringBuilder()
            .AppendLine("# Tauri Settings dev parity")
            .AppendLine()
            .AppendLine($"- 执行时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine("- channel：dev（非默认通道）")
            .AppendLine("- 数据保护：使用唯一临时 dev data root 并在 finally 清理；prod 设置只做前后指纹对照")
            .AppendLine("- Agent：不连接、停止、reload 或启动任何真实 Agent")
            .AppendLine()
            .AppendLine("## 验收项")
            .AppendLine()
            .AppendLine("| 验收项 | 结果 | 说明 |")
            .AppendLine("|---|---|---|");
        foreach (var check in checks)
        {
            builder.AppendLine($"| {check.Name} | {(check.Passed ? "PASS" : "FAIL")} | {check.Detail} |");
        }
        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(false));
    }

    private static void Add(ICollection<ParityCheck> checks, string name, bool passed, string detail) =>
        checks.Add(new ParityCheck(name, passed, detail));

    private static string CreateDefaultReportPath() => Path.Combine(
        Path.GetTempPath(), "WUJI.Smoke", $"settings-parity-{DateTime.Now:yyyyMMdd-HHmmss}.md");

    private sealed record ParityCheck(string Name, bool Passed, string Detail);

    private sealed record Options(string BridgePath, string DataRootPath, string? ReportPath)
    {
        public static Options? Parse(IReadOnlyList<string> args)
        {
            string? bridge = null;
            string? dataRoot = null;
            string? report = null;
            for (var index = 0; index < args.Count; index++)
            {
                switch (args[index])
                {
                    case "--bridge" when index + 1 < args.Count:
                        bridge = Path.GetFullPath(args[++index]);
                        break;
                    case "--report" when index + 1 < args.Count:
                        report = Path.GetFullPath(args[++index]);
                        break;
                    case "--data-root" when index + 1 < args.Count:
                        dataRoot = Path.GetFullPath(args[++index]);
                        break;
                }
            }
            return bridge is not null && File.Exists(bridge) && dataRoot is not null
                ? new Options(bridge, dataRoot, report)
                : null;
        }
    }
}

internal sealed record SafeSettings(
    string Theme,
    long RefreshIntervalSeconds,
    bool AutoStartAgentWhenAppStarts,
    long SamplingIntervalSeconds,
    long IdleThresholdSeconds,
    long HeartbeatIntervalSeconds,
    long StaleThresholdSeconds,
    long RetentionDays,
    bool EnableJsonlJournal,
    bool EnableAgentEventJournal,
    bool EnableSessionMerge,
    bool MaskWindowTitles)
{
    public static SafeSettings Initial { get; } = new(
        "light", 27, false, 3, 90, 3, 18, 30, true, true, true, true);

    public static SafeSettings TauriAcceptance { get; } = new(
        "dark", 41, true, 4, 150, 4, 25, 45, false, true, false, true);

    public static SafeSettings WpfAcceptance { get; } = new(
        "high_contrast", 55, false, 5, 180, 5, 35, 60, true, false, true, false);

    public object ToJsonModel() => new
    {
        appSettings = new
        {
            theme = Theme,
            refreshIntervalSeconds = RefreshIntervalSeconds,
            autoStartAgentWhenAppStarts = AutoStartAgentWhenAppStarts
        },
        agentOptions = new
        {
            samplingIntervalSeconds = SamplingIntervalSeconds,
            idleThresholdSeconds = IdleThresholdSeconds,
            heartbeatIntervalSeconds = HeartbeatIntervalSeconds,
            staleThresholdSeconds = StaleThresholdSeconds,
            retentionDays = RetentionDays,
            enableJsonlJournal = EnableJsonlJournal,
            enableAgentEventJournal = EnableAgentEventJournal,
            enableSessionMerge = EnableSessionMerge,
            maskWindowTitles = MaskWindowTitles
        }
    };

    public static SafeSettings FromJson(JsonElement element)
    {
        var app = element.GetProperty("appSettings");
        var agent = element.GetProperty("agentOptions");
        return new SafeSettings(
            NormalizeTheme(app.GetProperty("theme").GetString()),
            app.GetProperty("refreshIntervalSeconds").GetInt64(),
            app.GetProperty("autoStartAgentWhenAppStarts").GetBoolean(),
            agent.GetProperty("samplingIntervalSeconds").GetInt64(),
            agent.GetProperty("idleThresholdSeconds").GetInt64(),
            agent.GetProperty("heartbeatIntervalSeconds").GetInt64(),
            agent.GetProperty("staleThresholdSeconds").GetInt64(),
            agent.GetProperty("retentionDays").GetInt64(),
            agent.GetProperty("enableJsonlJournal").GetBoolean(),
            agent.GetProperty("enableAgentEventJournal").GetBoolean(),
            agent.GetProperty("enableSessionMerge").GetBoolean(),
            agent.GetProperty("maskWindowTitles").GetBoolean());
    }

    public static SafeSettings FromApplication(ClientSettingsSnapshot value) => new(
        NormalizeTheme(value.AppSettings.Theme),
        value.AppSettings.RefreshIntervalSeconds,
        value.AppSettings.AutoStartAgentWhenAppStarts,
        value.AgentOptions.SamplingIntervalSeconds,
        value.AgentOptions.IdleThresholdSeconds,
        value.AgentOptions.HeartbeatIntervalSeconds,
        value.AgentOptions.StaleThresholdSeconds,
        value.AgentOptions.RetentionDays,
        value.AgentOptions.EnableJsonlJournal,
        value.AgentOptions.EnableAgentEventJournal,
        value.AgentOptions.EnableSessionMerge,
        value.AgentOptions.MaskWindowTitles);

    public ClientSettingsSnapshot ToApplication() => new(
        new ClientAppSettings(Theme switch
        {
            "dark" => "Dark",
            "high_contrast" => "HighContrast",
            _ => "Light"
        }, RefreshIntervalSeconds, AutoStartAgentWhenAppStarts),
        new ClientAgentOptions(
            SamplingIntervalSeconds,
            IdleThresholdSeconds,
            HeartbeatIntervalSeconds,
            StaleThresholdSeconds,
            RetentionDays,
            EnableJsonlJournal,
            EnableAgentEventJournal,
            EnableSessionMerge,
            MaskWindowTitles));

    public static SafeSettings FromWpf(SettingsViewModel value) => new(
        NormalizeTheme(value.SelectedTheme),
        Parse(value.RefreshIntervalSecondsText),
        value.AutoStartAgentWhenAppStarts,
        Parse(value.SamplingIntervalSecondsText),
        Parse(value.IdleThresholdSecondsText),
        Parse(value.HeartbeatIntervalSecondsText),
        Parse(value.StaleThresholdSecondsText),
        Parse(value.RetentionDaysText),
        value.EnableJsonlJournal,
        value.EnableAgentEventJournal,
        value.EnableSessionMerge,
        value.MaskWindowTitles);

    private static long Parse(string value) => long.Parse(value, CultureInfo.InvariantCulture);

    private static string NormalizeTheme(string? value)
    {
        var normalized = (value ?? string.Empty).Replace(" ", string.Empty).Replace("_", string.Empty);
        return normalized.Equals("HighContrast", StringComparison.OrdinalIgnoreCase)
            ? "high_contrast"
            : normalized.ToLowerInvariant();
    }
}

internal sealed class FileSetSnapshot
{
    private readonly IReadOnlyDictionary<string, FileSnapshot> _files;

    private FileSetSnapshot(IReadOnlyDictionary<string, FileSnapshot> files) => _files = files;

    public static FileSetSnapshot Capture(IEnumerable<string> paths) => new(
        paths.ToDictionary(Path.GetFullPath, FileSnapshot.Capture, StringComparer.OrdinalIgnoreCase));

    public bool EquivalentToCurrent() => _files.All(entry => entry.Value.EquivalentToCurrent(entry.Key));

    public void Restore()
    {
        foreach (var entry in _files)
        {
            entry.Value.Restore(entry.Key);
        }
    }
}

internal sealed record FileSnapshot(bool Exists, byte[] Content, DateTime LastWriteTimeUtc)
{
    public static FileSnapshot Capture(string path) => File.Exists(path)
        ? new FileSnapshot(true, File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path))
        : new FileSnapshot(false, [], default);

    public bool EquivalentToCurrent(string path)
    {
        if (File.Exists(path) != Exists) return false;
        return !Exists
            || (File.GetLastWriteTimeUtc(path) == LastWriteTimeUtc
                && File.ReadAllBytes(path).AsSpan().SequenceEqual(Content));
    }

    public void Restore(string path)
    {
        if (!Exists)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Content);
        File.SetLastWriteTimeUtc(path, LastWriteTimeUtc);
    }
}

internal sealed class BridgeSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _input;
    private readonly StreamReader _output;
    private bool _shutdown;

    private BridgeSession(Process process)
    {
        _process = process;
        _input = process.StandardInput;
        _output = process.StandardOutput;
        _ = DrainDiagnosticsAsync(process.StandardError);
    }

    public static async Task<BridgeSession> StartAsync(string path, string dataRootPath)
    {
        var startInfo = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--channel");
        startInfo.ArgumentList.Add("dev");
        startInfo.Environment["QUANTIFIEDSELF_WINDOWS_AGENT_ROOT"] = dataRootPath;
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Bridge did not start.");
        var session = new BridgeSession(process);
        var hello = await session.RequestResultAsync("bridge.hello", new { });
        if (!hello.GetProperty("capabilities").EnumerateArray()
            .Select(item => item.GetString())
            .Contains("settings.get", StringComparer.Ordinal)
            || !hello.GetProperty("capabilities").EnumerateArray()
                .Select(item => item.GetString())
                .Contains("settings.update", StringComparer.Ordinal))
        {
            await session.DisposeAsync();
            throw new InvalidOperationException("Bridge capability mismatch.");
        }
        return session;
    }

    public async Task<JsonElement> RequestResultAsync(string method, object parameters)
    {
        var response = await RequestAsync(method, parameters);
        if (response.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"Bridge returned {error.GetProperty("code").GetString()}.");
        }
        return response.GetProperty("result").Clone();
    }

    public async Task<JsonElement> RequestErrorAsync(string method, object parameters)
    {
        var response = await RequestAsync(method, parameters);
        if (!response.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException("Bridge unexpectedly returned success.");
        }
        return error.Clone();
    }

    public async Task ShutdownAsync()
    {
        if (_shutdown) return;
        _shutdown = true;
        _ = await RequestResultAsync("bridge.shutdown", new { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _process.WaitForExitAsync(timeout.Token);
    }

    public async Task CrashAsync()
    {
        if (_process.HasExited) return;
        _shutdown = true;
        _process.Kill(entireProcessTree: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _process.WaitForExitAsync(timeout.Token);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_shutdown && !_process.HasExited) await ShutdownAsync();
        }
        catch
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        finally
        {
            _input.Dispose();
            _output.Dispose();
            _process.Dispose();
        }
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var id = Guid.NewGuid().ToString("N");
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters,
            meta = new { apiVersion = "1.0", correlationId = id }
        });
        await _input.WriteLineAsync(request.AsMemory(), timeout.Token);
        await _input.FlushAsync(timeout.Token);
        var line = await _output.ReadLineAsync(timeout.Token)
            ?? throw new EndOfStreamException("Bridge response ended.");
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.GetProperty("id").GetString() != id)
        {
            throw new InvalidOperationException("Bridge response id mismatch.");
        }
        return document.RootElement.Clone();
    }

    private static async Task DrainDiagnosticsAsync(StreamReader error)
    {
        while (await error.ReadLineAsync() is not null)
        {
            // Diagnostics intentionally stay out of the parity report.
        }
    }
}
