using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using QuantifiedSelf.Windows.Client;
using QuantifiedSelf.Windows.Client.Startup;
using QuantifiedSelf.Windows.Core.Models;

return await DashboardParityProgram.RunAsync(args);

internal static class DashboardParityProgram
{
    private const string ApiVersion = "1.0";
    private const string ChannelName = "dev";

    public static async Task<int> RunAsync(string[] args)
    {
        var options = Options.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("Usage: dashboard-parity --bridge <path> [--report <path>]");
            return 2;
        }

        var reportPath = options.ReportPath ?? CreateDefaultReportPath();
        var checks = new List<ParityCheck>();
        try
        {
            var launchOptions = StartupLaunchOptions.Parse(["--channel", ChannelName]);
            await using var client = WujiClientFactory.Create(
                WujiClientOptions.FromLaunchOptions(launchOptions));
            await client.InitializeAsync();

            var status = await client.Agent.Status.GetStatusAsync();
            if (status.IsRunning)
            {
                Console.Error.WriteLine("[BLOCKED] dev Agent 正在运行。请先显式停止 Agent，再重试 parity。 ");
                return 3;
            }

            var wpfSummary = await client.Activity.DailyStats.GetTodaySummaryAsync(
                topAppsLimit: 5,
                topWindowsLimit: 10);
            var wpfRecentSessions = await client.Activity.Sessions.GetRecentSessionsAsync(limit: 5);

            await using var bridge = await BridgeSession.StartAsync(options.BridgePath);
            var initialization = await bridge.RequestAsync("client.initialize");
            var bridgeChannel = initialization.GetProperty("channelName").GetString();
            var isDefaultChannel = initialization.GetProperty("isDefaultChannel").GetBoolean();
            Add(checks, "dev channel 固定", bridgeChannel == ChannelName && !isDefaultChannel, "channel=dev, non-default");

            var overview = await bridge.RequestAsync("activity.getOverview");
            CompareSummary(checks, wpfSummary, overview.GetProperty("summary"));
            CompareTopApps(checks, wpfSummary.TopApps, overview.GetProperty("topApps"));
            CompareRecentSessions(checks, wpfRecentSessions, overview.GetProperty("recentSessions"));
            CheckPrivacy(checks, overview);

            await bridge.ShutdownAsync();
            await WriteReportAsync(reportPath, checks, wpfSummary, overview);

            foreach (var check in checks)
            {
                Console.WriteLine($"[{(check.Passed ? "PASS" : "FAIL")}] {check.Name}: {check.Detail}");
            }

            Console.WriteLine($"报告已写入：{reportPath}");
            return checks.All(check => check.Passed) ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[FAIL] parity probe 未完成（{exception.GetType().Name}）。");
            return 1;
        }
    }

    private static void CompareSummary(
        ICollection<ParityCheck> checks,
        DailyActivitySummary wpf,
        JsonElement tauri)
    {
        var tauriTotal = tauri.GetProperty("totalDurationSeconds").GetInt64();
        var tauriActive = tauri.GetProperty("activeDurationSeconds").GetInt64();
        var tauriIdle = tauri.GetProperty("idleDurationSeconds").GetInt64();
        var tauriUnknown = tauri.GetProperty("unknownDurationSeconds").GetInt64();
        var tauriSessions = tauri.GetProperty("sessionCount").GetInt64();
        var wpfUnknown = Math.Max(0, wpf.TotalDurationSeconds - wpf.TotalActiveDurationSeconds - wpf.TotalIdleDurationSeconds);

        Add(checks, "今日总时长", wpf.TotalDurationSeconds == tauriTotal,
            $"WPF={wpf.TotalDurationSeconds}s, Tauri={tauriTotal}s");
        Add(checks, "今日有效时长", wpf.TotalActiveDurationSeconds == tauriActive,
            $"WPF={wpf.TotalActiveDurationSeconds}s, Tauri={tauriActive}s");
        Add(checks, "今日空闲与未分类", wpf.TotalIdleDurationSeconds == tauriIdle && wpfUnknown == tauriUnknown,
            $"idle={tauriIdle}s, unknown={tauriUnknown}s");
        Add(checks, "今日会话数", wpf.SessionCount == tauriSessions,
            $"WPF={wpf.SessionCount}, Tauri={tauriSessions}");

        var today = DateOnly.FromDateTime(DateTime.Now);
        var tauriDate = DateTime.Parse(
            tauri.GetProperty("dateUtc").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        var tauriLocal = tauriDate.ToLocalTime();
        var dateMatches = DateOnly.FromDateTime(wpf.Date) == today
            && DateOnly.FromDateTime(tauriLocal) == today
            && tauriLocal.TimeOfDay == TimeSpan.Zero;
        Add(checks, "本地日期边界", dateMatches,
            $"localDate={today:yyyy-MM-dd}, utcOffset={TimeZoneInfo.Local.GetUtcOffset(DateTime.Now)}");
    }

    private static void CompareTopApps(
        ICollection<ParityCheck> checks,
        IReadOnlyList<AppUsageSummary> wpf,
        JsonElement tauri)
    {
        var items = tauri.EnumerateArray().ToArray();
        var matches = wpf.Count == items.Length;
        for (var index = 0; matches && index < wpf.Count; index++)
        {
            var left = wpf[index];
            var right = items[index];
            matches = string.Equals(left.DisplayName, right.GetProperty("displayName").GetString(), StringComparison.Ordinal)
                && left.TotalDurationSeconds == right.GetProperty("totalDurationSeconds").GetInt64()
                && left.ActiveDurationSeconds == right.GetProperty("activeDurationSeconds").GetInt64()
                && left.IdleDurationSeconds == right.GetProperty("idleDurationSeconds").GetInt64()
                && left.UnknownDurationSeconds == right.GetProperty("unknownDurationSeconds").GetInt64()
                && left.SessionCount == right.GetProperty("sessionCount").GetInt64();
        }

        Add(checks, "Top Apps 排序与时长", matches, $"count={items.Length}, limit=5");
    }

    private static void CompareRecentSessions(
        ICollection<ParityCheck> checks,
        IReadOnlyList<AppSession> wpf,
        JsonElement tauri)
    {
        var items = tauri.EnumerateArray().ToArray();
        var matches = wpf.Count == items.Length;
        for (var index = 0; matches && index < wpf.Count; index++)
        {
            var left = wpf[index];
            var right = items[index];
            var rightStart = ParseUtc(right.GetProperty("startedAtUtc").GetString()!);
            var rightEnd = right.TryGetProperty("endedAtUtc", out var endedAt)
                ? ParseUtc(endedAt.GetString()!)
                : (DateTime?)null;
            matches = string.Equals(left.DisplayName, right.GetProperty("displayName").GetString(), StringComparison.Ordinal)
                && left.StartedAtUtc.ToUniversalTime() == rightStart
                && Nullable.Equals(left.EndedAtUtc?.ToUniversalTime(), rightEnd)
                && left.TotalDurationSeconds == right.GetProperty("totalDurationSeconds").GetInt64()
                && left.ActiveDurationSeconds == right.GetProperty("activeDurationSeconds").GetInt64()
                && left.IdleDurationSeconds == right.GetProperty("idleDurationSeconds").GetInt64()
                && left.UnknownDurationSeconds == right.GetProperty("unknownDurationSeconds").GetInt64();
        }

        Add(checks, "最近会话数量与 UTC 时间", matches, $"count={items.Length}, limit=5");
        var localTimesValid = items.All(item =>
        {
            var utc = ParseUtc(item.GetProperty("startedAtUtc").GetString()!);
            return utc.Kind == DateTimeKind.Utc && utc.ToLocalTime().Kind == DateTimeKind.Local;
        });
        Add(checks, "最近会话本地时区转换", localTimesValid,
            $"timeZone={TimeZoneInfo.Local.Id}");
    }

    private static void CheckPrivacy(ICollection<ParityCheck> checks, JsonElement overview)
    {
        string[] forbiddenProperties =
        [
            "id", "processName", "windowTitle", "executablePath", "databasePath",
            "closeReason", "dataRoot", "pipeName", "exception", "stackTrace"
        ];
        var propertyNames = EnumeratePropertyNames(overview).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var safeProperties = forbiddenProperties.All(property => !propertyNames.Contains(property));
        var safeStrings = EnumerateStrings(overview).All(value =>
            !value.Contains(@":\", StringComparison.Ordinal)
            && !value.Contains(@"\\", StringComparison.Ordinal)
            && !value.Contains("/Users/", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("/home/", StringComparison.OrdinalIgnoreCase));
        Add(checks, "隐私字段 allowlist", safeProperties && safeStrings,
            "无 id/process/window/path/database/exception 字段或路径形态字符串");
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

    private static async Task WriteReportAsync(
        string reportPath,
        IReadOnlyList<ParityCheck> checks,
        DailyActivitySummary wpf,
        JsonElement overview)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var summary = overview.GetProperty("summary");
        var builder = new StringBuilder()
            .AppendLine("# Tauri Dashboard dev parity")
            .AppendLine()
            .AppendLine($"- 执行时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine("- channel：dev（非默认通道）")
            .AppendLine("- 数据条件：dev Agent 已停止，WPF 与 Tauri 顺序读取同一数据库")
            .AppendLine()
            .AppendLine("## 数据摘要")
            .AppendLine()
            .AppendLine("| 指标 | WPF | Tauri |")
            .AppendLine("|---|---:|---:|")
            .AppendLine($"| 今日总时长（秒） | {wpf.TotalDurationSeconds} | {summary.GetProperty("totalDurationSeconds").GetInt64()} |")
            .AppendLine($"| 今日有效时长（秒） | {wpf.TotalActiveDurationSeconds} | {summary.GetProperty("activeDurationSeconds").GetInt64()} |")
            .AppendLine($"| 今日会话数 | {wpf.SessionCount} | {summary.GetProperty("sessionCount").GetInt64()} |")
            .AppendLine($"| Top Apps 数量 | {wpf.TopApps.Count} | {overview.GetProperty("topApps").GetArrayLength()} |")
            .AppendLine($"| 最近会话数量 | - | {overview.GetProperty("recentSessions").GetArrayLength()} |")
            .AppendLine()
            .AppendLine("## 验收项")
            .AppendLine()
            .AppendLine("| 验收项 | 结果 | 说明 |")
            .AppendLine("|---|---|---|");

        foreach (var check in checks)
        {
            builder.AppendLine($"| {check.Name} | {(check.Passed ? "PASS" : "FAIL")} | {check.Detail} |");
        }

        await File.WriteAllTextAsync(reportPath, builder.ToString(), new UTF8Encoding(false));
    }

    private static DateTime ParseUtc(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static void Add(ICollection<ParityCheck> checks, string name, bool passed, string detail) =>
        checks.Add(new ParityCheck(name, passed, detail));

    private static string CreateDefaultReportPath() => Path.Combine(
        Path.GetTempPath(),
        "WUJI.Smoke",
        $"dashboard-parity-{DateTime.Now:yyyyMMdd-HHmmss}.md");

    private sealed record ParityCheck(string Name, bool Passed, string Detail);

    private sealed record Options(string BridgePath, string? ReportPath)
    {
        public static Options? Parse(IReadOnlyList<string> args)
        {
            string? bridge = null;
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
                }
            }

            return bridge is not null && File.Exists(bridge) ? new Options(bridge, report) : null;
        }
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

    public static async Task<BridgeSession> StartAsync(string path)
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
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Bridge did not start.");
        var session = new BridgeSession(process);
        var hello = await session.RequestAsync("bridge.hello");
        if (!hello.GetProperty("capabilities").EnumerateArray()
            .Any(item => item.GetString() == "activity.getOverview"))
        {
            await session.DisposeAsync();
            throw new InvalidOperationException("Bridge capability mismatch.");
        }

        return session;
    }

    public async Task<JsonElement> RequestAsync(string method)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var id = Guid.NewGuid().ToString("N");
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = new { },
            meta = new { apiVersion = "1.0", correlationId = id }
        });
        await _input.WriteLineAsync(request.AsMemory(), timeout.Token);
        await _input.FlushAsync(timeout.Token);
        var line = await _output.ReadLineAsync(timeout.Token)
            ?? throw new EndOfStreamException("Bridge response ended.");
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.GetProperty("id").GetString() != id)
        {
            throw new InvalidOperationException("Bridge response id mismatch.");
        }

        if (root.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var codeValue)
                ? codeValue.GetString()
                : "unknown";
            throw new InvalidOperationException($"Bridge returned {code}.");
        }

        return root.GetProperty("result").Clone();
    }

    public async Task ShutdownAsync()
    {
        if (_shutdown) return;
        _shutdown = true;
        _ = await RequestAsync("bridge.shutdown");
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

    private static async Task DrainDiagnosticsAsync(StreamReader error)
    {
        while (await error.ReadLineAsync() is not null)
        {
            // Bridge stderr contains diagnostics only. The parity report intentionally omits it.
        }
    }
}
