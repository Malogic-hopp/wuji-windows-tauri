using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Infrastructure.Database;

namespace QuantifiedSelf.Windows.App.Services;

/// <summary>
/// Read-only service that produces a <see cref="FocusInterruptionInsight"/> for a given
/// local date by analysing foreground_samples.
///
/// Reuses <see cref="DailyStatsQueryService"/> for data access; all computation is in-memory.
/// Classification rules mirror <see cref="FocusMetricsCalculator"/> where possible.
/// </summary>
public sealed class FocusInterruptionInsightService
{
    private readonly DailyStatsQueryService _queryService;

    // Work-block detection thresholds (different from FocusMetricsCalculator
    // because Insights targets broader "work blocks", not just pristine focus sessions).
    internal const int WorkBlockMinMinutes = 25;
    internal const int MaxGapMinutes = 5;
    internal const int MaxSwitchesForFocus = 3;
    internal const double FocusPrimaryContextRatio = 0.70;

    public FocusInterruptionInsightService(string databasePath)
    {
        _queryService = new DailyStatsQueryService(databasePath);
    }

    // ── Public API ──────────────────────────────────────────────────────────

    public async Task<FocusInterruptionInsight> GetInsightAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        var samples = await _queryService.GetSamplesForLocalDayAsync(date, cancellationToken);

        if (samples.Count == 0)
        {
            return new FocusInterruptionInsight { Date = date };
        }

        // 1. Filter to Active only, sort by time
        var active = samples
            .Where(s => IsActive(s.ActivityState))
            .OrderBy(s => s.SampleTimeUtc)
            .ToList();

        if (active.Count < 2)
        {
            return new FocusInterruptionInsight
            {
                Date = date,
                ActiveSampleCount = active.Count,
            };
        }

        // 2. Classify each sample
        foreach (var s in active)
        {
            s.Context = ClassifyContext(s.ProcessName, s.WindowTitle ?? string.Empty);
        }

        // 3. Detect raw tool hops and meaningful switches
        var (rawHops, meaningfulSwitches) = CountSwitches(active);

        // 4. Detect work blocks
        var allBlocks = DetectWorkBlocks(active);
        var workBlocks = allBlocks
            .Where(b => b.Duration.TotalMinutes >= WorkBlockMinMinutes)
            .OrderByDescending(b => b.Duration)
            .Take(4)
            .ToList();

        // 5. Build interruption sources
        var interruptionSources = BuildInterruptionSources(workBlocks, active);

        // 6. Build context transitions
        var contextTransitions = BuildContextTransitions(meaningfulSwitches);

        // 7. Estimate active time
        var estimatedActiveTime = EstimateActiveTime(active);

        // 8. Top-level stat texts
        var longestBlock = workBlocks.FirstOrDefault();
        var longestBlockText = longestBlock is not null
            ? $"{FormatMinutes((long)longestBlock.Duration.TotalSeconds)} · {longestBlock.PrimaryApp}"
            : "-";
        var topInterruption = interruptionSources.FirstOrDefault();
        var topInterruptionText = topInterruption is not null
            ? $"{topInterruption.AppName} · {topInterruption.Count}×"
            : "-";

        // 9. Generate texts
        var (summaryText, actionText) = GenerateTexts(
            workBlocks, interruptionSources, rawHops, meaningfulSwitches);

        return new FocusInterruptionInsight
        {
            Date = date,
            ActiveSampleCount = active.Count,
            EstimatedActiveTime = estimatedActiveTime,
            RawToolHopCount = rawHops.Count,
            MeaningfulContextSwitchCount = meaningfulSwitches.Count,
            WorkBlocks = workBlocks,
            TopInterruptionSources = interruptionSources,
            TopContextTransitions = contextTransitions,
            SummaryText = summaryText,
            ActionText = actionText,
            LongestWorkBlockText = longestBlockText,
            TopInterruptionText = topInterruptionText,
        };
    }

    // ── Classification (mirrors FocusMetricsCalculator) ────────────────────

    internal static string ClassifyContext(string processName, string windowTitle)
    {
        var p = Normalize(processName);
        var title = windowTitle.Trim();

        if (IsBrowser(p))
            return ClassifyBrowserTitle(title);

        if (IsDevelopmentProcess(p)) return "开发";
        if (IsCommunicationProcess(p)) return "沟通";
        if (IsEntertainmentProcess(p)) return "娱乐";
        if (IsSystemProcess(p)) return "系统";
        if (IsProductivityProcess(p)) return "效率";
        return "其他";
    }

    internal static string ClassifyBrowserTitle(string title)
    {
        var t = title.ToLowerInvariant();

        if (ContainsAny(t,
                "youtube", "bilibili", "哔哩哔哩", "xiaohongshu", "小红书", "migu", "咪咕",
                "weibo", "微博", "zhiboba", "直播吧", "netflix", "twitch", "douyin", "抖音",
                "视频", "直播", "游戏"))
            return "娱乐";

        if (ContainsAny(t,
                "gmail", "outlook", "mail", "teams", "slack", "discord", "wechat", "微信", "飞书"))
            return "沟通";

        if (ContainsAny(t,
                "github", "gitlab", "stack overflow", "stackoverflow", "microsoft learn",
                "docs", "documentation", "api", "nuget", "npm", "localhost", "127.0.0.1",
                "openai", "codex", "developer", "devdocs", "copilot"))
            return "开发";

        return "研究";
    }

    // ── Work-block detection ────────────────────────────────────────────────

    internal static List<WorkBlockInsight> DetectWorkBlocks(List<ForegroundSample> activeSamples)
    {
        var blocks = new List<WorkBlockInsight>();
        if (activeSamples.Count == 0) return blocks;

        var maxGap = TimeSpan.FromMinutes(MaxGapMinutes);

        // Build raw segments
        var rawSegments = new List<RawWorkSegment>();
        var seg = new RawWorkSegment
        {
            StartUtc = activeSamples[0].SampleTimeUtc,
            EndUtc = activeSamples[0].SampleTimeUtc,
        };
        seg.Samples.Add(activeSamples[0]);
        seg.ContextCounts[activeSamples[0].Context] = 1;
        seg.AppCounts[ShortName(activeSamples[0].ProcessName)] = 1;

        string? lastContext = activeSamples[0].Context;
        string? lastApp = activeSamples[0].ProcessName;

        for (var i = 1; i < activeSamples.Count; i++)
        {
            var s = activeSamples[i];
            var gap = s.SampleTimeUtc - seg.EndUtc;

            if (gap > maxGap)
            {
                rawSegments.Add(seg);
                seg = new RawWorkSegment { StartUtc = s.SampleTimeUtc, EndUtc = s.SampleTimeUtc };
                seg.Samples.Add(s);
                seg.ContextCounts[s.Context] = 1;
                seg.AppCounts[ShortName(s.ProcessName)] = 1;
                lastContext = s.Context;
                continue;
            }

            seg.EndUtc = s.SampleTimeUtc;
            seg.Samples.Add(s);

            if (!seg.ContextCounts.ContainsKey(s.Context))
                seg.ContextCounts[s.Context] = 0;
            seg.ContextCounts[s.Context]++;

            var sn = ShortName(s.ProcessName);
            if (!seg.AppCounts.ContainsKey(sn))
                seg.AppCounts[sn] = 0;
            seg.AppCounts[sn]++;

            if (s.Context != lastContext)
            {
                // 开发 ↔ dev_tool 双向豁免
                if ((lastContext == "开发" && IsDevToolApp(s.ProcessName))
                    || (s.Context == "开发" && IsDevToolApp(lastApp)))
                {
                    // same workflow, keep lastContext unchanged
                }
                else
                {
                    seg.SwitchCount++;
                    lastContext = s.Context;
                }
            }
            else
            {
                lastContext = s.Context;
            }
            lastApp = s.ProcessName;
        }
        rawSegments.Add(seg);

        // Convert to WorkBlockInsight
        foreach (var rs in rawSegments)
        {
            var localStart = rs.StartUtc.ToLocalTime();
            var localEnd = rs.EndUtc.ToLocalTime();
            var duration = localEnd - localStart;

            var primaryCtx = rs.ContextCounts.OrderByDescending(kv => kv.Value).FirstOrDefault();
            var primaryApp = rs.AppCounts.OrderByDescending(kv => kv.Value).FirstOrDefault();

            var totalSamples = rs.Samples.Count;
            var primaryCtxRatio = totalSamples > 0
                ? (double)primaryCtx.Value / totalSamples
                : 0.0;
            var isFocus = duration.TotalMinutes >= WorkBlockMinMinutes
                && rs.SwitchCount <= MaxSwitchesForFocus
                && primaryCtxRatio >= FocusPrimaryContextRatio;

            var avgInterval = rs.SwitchCount > 0
                ? TimeSpan.FromTicks(duration.Ticks / (rs.SwitchCount + 1))
                : TimeSpan.Zero;

            // Build inner interruptions
            var primaryCtxKey = primaryCtx.Key ?? string.Empty;
            var innerInterruptions = new List<InterruptionSourceInsight>();
            foreach (var kv in rs.AppCounts
                .Where(kv => GetAppContext(kv.Key) != primaryCtxKey)
                .OrderByDescending(kv => kv.Value)
                .Take(3))
            {
                var ctx = GetAppContext(kv.Key);
                innerInterruptions.Add(new InterruptionSourceInsight
                {
                    AppName = kv.Key,
                    Context = ctx,
                    Count = kv.Value,
                    DisplayText = $"{kv.Key} · {kv.Value}×",
                });
            }

            string explanation;
            if (duration.TotalMinutes < WorkBlockMinMinutes)
                explanation = $"时长不足（{FormatMinutes((long)duration.TotalSeconds)} < {WorkBlockMinMinutes} 分钟）";
            else if (rs.SwitchCount > MaxSwitchesForFocus)
                explanation = $"切换过多（{rs.SwitchCount} > {MaxSwitchesForFocus}） — 平均每 {FormatSeconds((long)avgInterval.TotalSeconds)} 一次";
            else if (primaryCtxRatio < FocusPrimaryContextRatio)
                explanation = $"主语境「{primaryCtxKey}」仅占 {primaryCtxRatio:P0}";
            else
                explanation = "有效专注 ✓";

            blocks.Add(new WorkBlockInsight
            {
                StartLocal = localStart,
                EndLocal = localEnd,
                PrimaryContext = primaryCtxKey,
                PrimaryApp = primaryApp.Key ?? string.Empty,
                ContextSwitchCount = rs.SwitchCount,
                AverageSwitchInterval = avgInterval,
                TopInterruptions = innerInterruptions,
                IsRecognizedFocusBlock = isFocus,
                ExplanationText = explanation,
            });
        }

        return blocks;
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    internal static (List<SwitchRecord> Raw, List<SwitchRecord> Meaningful) CountSwitches(
        List<ForegroundSample> active)
    {
        var raw = new List<SwitchRecord>();
        var meaningful = new List<SwitchRecord>();

        for (var i = 1; i < active.Count; i++)
        {
            var prev = active[i - 1];
            var cur = active[i];
            var appChanged = !string.Equals(prev.ProcessName, cur.ProcessName, StringComparison.OrdinalIgnoreCase);
            var titleChanged = !string.Equals(prev.WindowTitle ?? "", cur.WindowTitle ?? "", StringComparison.Ordinal);

            if (!appChanged && !titleChanged) continue;

            var fromCtx = prev.Context ?? "其他";
            var toCtx = cur.Context ?? "其他";
            var rec = new SwitchRecord(
                ShortName(prev.ProcessName), ShortName(cur.ProcessName),
                fromCtx, toCtx, cur.SampleTimeUtc);

            raw.Add(rec);
            if (fromCtx != toCtx)
            {
                // 开发 ↔ dev_tool 双向豁免
                if (!((fromCtx == "开发" && IsDevToolApp(cur.ProcessName))
                   || (toCtx == "开发" && IsDevToolApp(prev.ProcessName))))
                    meaningful.Add(rec);
            }
        }

        return (raw, meaningful);
    }

    private static List<InterruptionSourceInsight> BuildInterruptionSources(
        List<WorkBlockInsight> workBlocks, List<ForegroundSample> active)
    {
        var workContexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "开发", "效率", "研究" };

        // Count apps that appear in a non-work context across all active samples
        var interrupterCounts = new Dictionary<string, InterruptionCount>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < active.Count; i++)
        {
            var prev = active[i - 1];
            var cur = active[i];
            var fromCtx = prev.Context ?? "其他";
            var toCtx = cur.Context ?? "其他";

            // Interruption: moving from work context to non-work context
            if (workContexts.Contains(fromCtx) && !workContexts.Contains(toCtx))
            {
                var app = ShortName(cur.ProcessName);
                if (!interrupterCounts.TryGetValue(app, out var count))
                {
                    count = new InterruptionCount { AppName = app };
                    interrupterCounts[app] = count;
                }
                count.TotalCount++;
                if (!count.ContextCounts.ContainsKey(toCtx))
                    count.ContextCounts[toCtx] = 0;
                count.ContextCounts[toCtx]++;
            }
        }

        return interrupterCounts.Values
            .OrderByDescending(c => c.TotalCount)
            .Take(6)
            .Select(c => new InterruptionSourceInsight
            {
                AppName = c.AppName,
                Context = c.ContextCounts.OrderByDescending(kv => kv.Value).First().Key,
                Count = c.TotalCount,
                DisplayText = $"{c.AppName} · {c.TotalCount}×",
            })
            .ToList();
    }

    private static List<ContextTransitionInsight> BuildContextTransitions(
        List<SwitchRecord> meaningfulSwitches)
    {
        var pairs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = meaningfulSwitches.Count;

        foreach (var s in meaningfulSwitches)
        {
            var key = $"{s.FromContext}→{s.ToContext}";
            if (!pairs.ContainsKey(key))
                pairs[key] = 0;
            pairs[key]++;
        }

        return pairs
            .OrderByDescending(kv => kv.Value)
            .Take(8)
            .Select(kv =>
            {
                var parts = kv.Key.Split('→');
                var from = parts.Length > 0 ? parts[0] : "";
                var to = parts.Length > 1 ? parts[1] : "";
                var ratio = total > 0 ? (double)kv.Value / total : 0.0;
                return new ContextTransitionInsight
                {
                    FromContext = from,
                    ToContext = to,
                    Count = kv.Value,
                    Ratio = ratio,
                    DisplayText = $"{from} → {to} · {kv.Value} ({ratio:P1})",
                };
            })
            .ToList();
    }

    private static TimeSpan EstimateActiveTime(List<ForegroundSample> active)
    {
        const double maxGap = 60.0;
        double total = 0;

        for (var i = 0; i < active.Count; i++)
        {
            double gap;
            if (i < active.Count - 1)
            {
                gap = Math.Min(
                    (active[i + 1].SampleTimeUtc - active[i].SampleTimeUtc).TotalSeconds,
                    maxGap);
                if (gap < 0) gap = 0;
            }
            else
            {
                gap = 1.0;
            }
            total += gap;
        }

        return TimeSpan.FromSeconds(total);
    }

    internal static (string Summary, string Action) GenerateTexts(
        List<WorkBlockInsight> workBlocks,
        List<InterruptionSourceInsight> interruptionSources,
        List<SwitchRecord> rawSwitches,
        List<SwitchRecord> meaningfulSwitches)
    {
        var summary = string.Empty;
        var action = string.Empty;

        if (workBlocks.Count == 0)
        {
            if (meaningfulSwitches.Count > 0)
            {
                summary = "今天有零散活动，但未形成较长连续工作块。活动较为碎片化。";
                action = "试试安排一个 25 分钟不受打扰的 Code-Only 块。";
            }
            else
            {
                summary = "今日活动数据不足以生成专注洞察。";
                action = string.Empty;
            }
            return (summary, action);
        }

        var focusBlocks = workBlocks.Where(b => b.IsRecognizedFocusBlock).ToList();
        var longBlocks = workBlocks.Where(b => b.Duration.TotalMinutes >= WorkBlockMinMinutes).ToList();

        // Build summary
        if (focusBlocks.Count > 0)
        {
            summary = $"今天有 {focusBlocks.Count} 段有效专注块";
            var longest = focusBlocks[0];
            summary += $"，最长 {FormatMinutes((long)longest.Duration.TotalSeconds)}（{longest.PrimaryContext} · {longest.PrimaryApp}）";
        }
        else if (longBlocks.Count > 0)
        {
            summary = $"今天有 {longBlocks.Count} 段较长工作块";
            if (interruptionSources.Count > 0)
            {
                var topNames = string.Join("、", interruptionSources.Take(3).Select(s => s.AppName));
                summary += $"，但 {topNames} 插入较多";
            }
            summary += "，导致未能形成有效连续专注。";
        }

        // Build action
        if (interruptionSources.Count > 0)
        {
            var top = interruptionSources[0];
            if (top.Context == "沟通")
                action = $"试试安排一个 45 分钟 Code-Only 块，把 {top.AppName} 检查集中到块前后处理。";
            else if (top.Context == "系统")
                action = $"试试在工作块开始前 5 分钟集中处理文件查找（{top.AppName}）。";
            else if (top.Context == "娱乐")
                action = $"试试把 {top.AppName} 等娱乐站点放到休息时段。";
            else
                action = $"关掉 {top.AppName}，专注一个应用 30 分钟。";
        }

        if (focusBlocks.Count > 0 && !string.IsNullOrEmpty(action))
            action += "\n继续保持已有的专注节奏。";

        return (summary, action);
    }

    // ── Classification helpers ──────────────────────────────────────────────

    private static string ShortName(string processName)
    {
        var n = (processName ?? string.Empty).Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            n = n[..^4];
        return n;
    }

    private static string Normalize(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        return text.EndsWith(".exe", StringComparison.Ordinal)
            ? text[..^4]
            : text;
    }

    internal static string GetAppContext(string shortAppName)
    {
        // Quick lookup for app's typical context based on name.
        // Used for classifying interruptions within work blocks.
        var n = shortAppName.ToLowerInvariant();
        if (IsDevelopmentProcess(n)) return "开发";
        if (IsCommunicationProcess(n)) return "沟通";
        if (IsEntertainmentProcess(n)) return "娱乐";
        if (IsBrowser(n)) return "研究";
        if (IsSystemProcess(n)) return "系统";
        if (IsProductivityProcess(n)) return "效率";
        return "其他";
    }

    private static bool IsDevToolApp(string processName)
    {
        var n = ShortName(processName).ToLowerInvariant();
        return n is "explorer" or "windowsterminal" or "terminal"
            or "powershell" or "pwsh" or "cmd";
    }

    private static bool IsActive(string state) =>
        string.Equals((state ?? string.Empty).Trim(), "Active", StringComparison.OrdinalIgnoreCase);

    private static bool IsBrowser(string process)
        => ContainsAny(process, "msedge", "edge", "chrome", "firefox", "browser");

    private static bool IsDevelopmentProcess(string process)
        => ContainsAny(process,
            "code", "codex", "devenv", "rider", "webstorm", "pycharm", "idea",
            "terminal", "windowsterminal", "powershell", "pwsh", "cmd", "git",
            "github", "dotnet", "quantifiedself", "wuji", "cursor");

    private static bool IsCommunicationProcess(string process)
        => ContainsAny(process,
            "wechat", "weixin", "teams", "slack", "outlook", "mail", "discord", "feishu", "lark");

    private static bool IsEntertainmentProcess(string process)
        => ContainsAny(process,
            "steam", "netease", "spotify", "music", "video", "player", "bilibili", "youtube");

    private static bool IsSystemProcess(string process)
        => ContainsAny(process, "explorer", "taskmgr", "settings", "control");

    private static bool IsProductivityProcess(string process)
        => ContainsAny(process,
            "word", "excel", "powerpoint", "onenote", "notion", "obsidian", "zotero", "typora", "wps");

    private static bool ContainsAny(string text, params string[] tokens)
        => tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

    // ── Formatting ──────────────────────────────────────────────────────────

    internal static string FormatMinutes(long seconds)
    {
        if (seconds <= 0) return "0m";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m";
    }

    private static string FormatSeconds(long seconds)
    {
        if (seconds <= 0) return "0s";
        if (seconds < 120) return $"{seconds}s";
        return $"{seconds / 60}m {seconds % 60}s";
    }

    // ── Nested types ────────────────────────────────────────────────────────

    internal sealed record SwitchRecord(
        string FromApp, string ToApp,
        string FromContext, string ToContext,
        DateTime TimeUtc);

    private sealed class InterruptionCount
    {
        public string AppName { get; init; } = string.Empty;
        public int TotalCount { get; set; }
        public Dictionary<string, int> ContextCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class RawWorkSegment
    {
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public int SwitchCount { get; set; }
        public List<ForegroundSample> Samples { get; } = [];
        public Dictionary<string, int> ContextCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> AppCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
