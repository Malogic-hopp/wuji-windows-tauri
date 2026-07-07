using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.App.Services;

/// <summary>
/// Pure-calculation service that computes focus and context-switch metrics
/// from today's active foreground samples. No I/O — all data is passed in.
///
/// All rules are explicit and explainable; no black-box scoring.
/// </summary>
public static class FocusMetricsCalculator
{
    /// <summary>
    /// Minimum focus session duration to qualify as a "focus session".
    /// </summary>
    public const int DefaultMinimumFocusMinutes = 10;

    /// <summary>
    /// Maximum allowed gap (in minutes) between consecutive active samples
    /// before a focus session is considered broken.
    /// </summary>
    public const int DefaultMaxGapMinutes = 3;

    /// <summary>
    /// Maximum allowed context switches within a focus session.
    /// Sessions exceeding this are marked as fragmented.
    /// </summary>
    public const int DefaultMaxSwitchesPerFocusBlock = 3;

    /// <summary>
    /// Computes focus metrics from today's foreground samples.
    /// Only Active-state samples are used for analysis; Idle and Unknown are treated as breaks.
    /// </summary>
    /// <param name="todaySamples">All foreground samples for today, in any order.</param>
    /// <param name="minimumFocusMinutes">Minimum duration for a focus session.</param>
    /// <param name="maxGapMinutes">Maximum gap that doesn't break a focus session.</param>
    /// <param name="maxSwitchesPerFocusBlock">Maximum switches within a non-fragmented focus session.</param>
    public static FocusMetricsResult Compute(
        IReadOnlyList<ForegroundSample> todaySamples,
        int minimumFocusMinutes = DefaultMinimumFocusMinutes,
        int maxGapMinutes = DefaultMaxGapMinutes,
        int maxSwitchesPerFocusBlock = DefaultMaxSwitchesPerFocusBlock)
    {
        if (todaySamples.Count == 0)
        {
            return new FocusMetricsResult();
        }

        // Filter to Active only, sort by time ascending
        var activeSamples = todaySamples
            .Where(s => string.Equals(s.ActivityState, "Active", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.SampleTimeUtc)
            .ToList();

        if (activeSamples.Count == 0)
        {
            return new FocusMetricsResult();
        }

        var maxGap = TimeSpan.FromMinutes(maxGapMinutes);
        var minFocus = TimeSpan.FromMinutes(minimumFocusMinutes);

        // Phase 1: Detect context switches and build raw segments
        var segments = new List<RawSegment>();
        RawSegment? current = null;
        var rawSwitches = 0;
        var meaningfulSwitches = 0;

        for (var i = 0; i < activeSamples.Count; i++)
        {
            var sample = activeSamples[i];
            var title = sample.WindowTitle ?? string.Empty;
            var context = ClassifyContext(sample);

            // Check for context switch (compare with previous active sample)
            if (i > 0)
            {
                var prev = activeSamples[i - 1];
                var prevTitle = prev.WindowTitle ?? string.Empty;
                if (!string.Equals(sample.ProcessName, prev.ProcessName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(title, prevTitle, StringComparison.Ordinal))
                {
                    rawSwitches++;
                }

                var prevContext = ClassifyContext(prev);
                if (context != prevContext)
                {
                    meaningfulSwitches++;
                }
            }

            if (current is null)
            {
                current = new RawSegment
                {
                    StartUtc = sample.SampleTimeUtc,
                    EndUtc = sample.SampleTimeUtc,
                    SwitchCount = 0,
                    AppCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                };
                current.AppCounts[sample.ProcessName] = 1;
                current.LastApp = sample.ProcessName;
                current.LastTitle = title;
                current.LastContext = context;
                continue;
            }

            var gap = sample.SampleTimeUtc - current.EndUtc;

            // Check if gap exceeds threshold → break the segment
            if (gap > maxGap)
            {
                segments.Add(current);
                current = new RawSegment
                {
                    StartUtc = sample.SampleTimeUtc,
                    EndUtc = sample.SampleTimeUtc,
                    SwitchCount = 0,
                    AppCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                };
                current.AppCounts[sample.ProcessName] = 1;
                current.LastApp = sample.ProcessName;
                current.LastTitle = title;
                current.LastContext = context;
                continue;
            }

            // Same segment: extend end time
            current.EndUtc = sample.SampleTimeUtc;

            // Track app counts
            if (!current.AppCounts.TryGetValue(sample.ProcessName, out _))
            {
                current.AppCounts[sample.ProcessName] = 0;
            }

            current.AppCounts[sample.ProcessName]++;

            // Detect meaningful task-context switches within the segment.
            if (context != current.LastContext)
            {
                current.SwitchCount++;
                current.LastContext = context;
            }

            if (!string.Equals(sample.ProcessName, current.LastApp, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(title, current.LastTitle, StringComparison.Ordinal))
            {
                current.LastApp = sample.ProcessName;
                current.LastTitle = title;
            }
        }

        // Finalize last segment
        if (current is not null)
        {
            segments.Add(current);
        }

        // Phase 2: Classify segments into focus sessions
        var focusSessions = new List<FocusSessionSummary>();
        long fragmentedSeconds = 0;
        FocusSessionSummary? longest = null;

        foreach (var seg in segments)
        {
            var duration = seg.EndUtc - seg.StartUtc;
            var isFragmented = seg.SwitchCount > maxSwitchesPerFocusBlock;

            var dominantApp = seg.AppCounts
                .OrderByDescending(kv => kv.Value)
                .FirstOrDefault().Key ?? string.Empty;

            var session = new FocusSessionSummary
            {
                StartUtc = seg.StartUtc,
                EndUtc = seg.EndUtc,
                DominantApp = dominantApp,
                SwitchCount = seg.SwitchCount,
                IsFragmented = isFragmented
            };

            // Only count segments meeting min duration as focus sessions
            if (duration >= minFocus && seg.SwitchCount <= maxSwitchesPerFocusBlock)
            {
                focusSessions.Add(session);

                if (longest is null || duration > longest.Duration)
                {
                    longest = session;
                }
            }

            // Track fragmented time (high-switch segments regardless of duration)
            if (isFragmented)
            {
                fragmentedSeconds += (long)duration.TotalSeconds;
            }
        }

        return new FocusMetricsResult
        {
            ContextSwitchCount = meaningfulSwitches,
            RawContextSwitchCount = rawSwitches,
            LongestFocusSession = longest,
            FocusSessionCount = focusSessions.Count,
            FragmentedTimeSeconds = fragmentedSeconds,
            FocusSessions = focusSessions
        };
    }

    private static ActivityContext ClassifyContext(ForegroundSample sample)
    {
        var process = Normalize(sample.ProcessName);
        var title = sample.WindowTitle ?? string.Empty;

        if (IsBrowser(process))
        {
            return ClassifyBrowserTitle(title);
        }

        if (IsDevelopmentProcess(process))
        {
            return ActivityContext.Development;
        }

        if (IsCommunicationProcess(process))
        {
            return ActivityContext.Communication;
        }

        if (IsEntertainmentProcess(process))
        {
            return ActivityContext.Entertainment;
        }

        if (IsSystemProcess(process))
        {
            return ActivityContext.System;
        }

        if (IsProductivityProcess(process))
        {
            return ActivityContext.Productivity;
        }

        return ActivityContext.Other;
    }

    private static ActivityContext ClassifyBrowserTitle(string title)
    {
        var text = title.ToLowerInvariant();

        if (ContainsAny(text,
                "youtube", "bilibili", "哔哩哔哩", "xiaohongshu", "小红书", "migu", "咪咕",
                "weibo", "微博", "zhiboba", "直播吧", "netflix", "twitch", "douyin", "抖音",
                "视频", "直播", "游戏"))
        {
            return ActivityContext.Entertainment;
        }

        if (ContainsAny(text,
                "gmail", "outlook", "mail", "teams", "slack", "discord", "wechat", "微信", "飞书"))
        {
            return ActivityContext.Communication;
        }

        if (ContainsAny(text,
                "github", "gitlab", "stack overflow", "stackoverflow", "microsoft learn",
                "docs", "documentation", "api", "nuget", "npm", "localhost", "127.0.0.1",
                "openai", "codex", "developer", "devdocs"))
        {
            return ActivityContext.Development;
        }

        return ActivityContext.Research;
    }

    private static bool IsDevelopmentProcess(string process)
    {
        return ContainsAny(process,
            "code", "codex", "devenv", "rider", "webstorm", "pycharm", "idea",
            "terminal", "windowsterminal", "powershell", "pwsh", "cmd", "git",
            "github", "dotnet", "quantifiedself", "wuji");
    }

    private static bool IsCommunicationProcess(string process)
    {
        return ContainsAny(process,
            "wechat", "weixin", "teams", "slack", "outlook", "mail", "discord", "feishu", "lark");
    }

    private static bool IsEntertainmentProcess(string process)
    {
        return ContainsAny(process,
            "steam", "netease", "spotify", "music", "video", "player", "bilibili", "youtube");
    }

    private static bool IsSystemProcess(string process)
    {
        return ContainsAny(process,
            "explorer", "taskmgr", "settings", "control");
    }

    private static bool IsProductivityProcess(string process)
    {
        return ContainsAny(process,
            "word", "excel", "powerpoint", "onenote", "notion", "obsidian", "zotero", "typora");
    }

    private static bool IsBrowser(string process)
    {
        return ContainsAny(process, "msedge", "edge", "chrome", "firefox", "browser");
    }

    private static string Normalize(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        return text.EndsWith(".exe", StringComparison.Ordinal)
            ? text[..^4]
            : text;
    }

    private static bool ContainsAny(string text, params string[] tokens)
    {
        return tokens.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private enum ActivityContext
    {
        Development,
        Research,
        Communication,
        Entertainment,
        System,
        Productivity,
        Other
    }

    private sealed class RawSegment
    {
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public int SwitchCount { get; set; }
        public string LastApp { get; set; } = string.Empty;
        public string LastTitle { get; set; } = string.Empty;
        public ActivityContext LastContext { get; set; }
        public Dictionary<string, int> AppCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
