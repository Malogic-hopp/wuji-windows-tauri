using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.App.Services;

/// <summary>
/// Pure-logic rule engine that generates gentle, actionable insight suggestions
/// from today's activity summary and 7-day trend data.
///
/// All rules are deterministic, threshold-based, and produce Chinese-language
/// suggestions. No network calls, no AI, no user profiling.
/// </summary>
public static class InsightSuggestionEngine
{
    // ── Thresholds (documented, testable) ──

    /// <summary>Minimum active seconds today before any suggestion triggers.</summary>
    public const long MinActiveSecondsForInsight = 600; // 10 min

    /// <summary>Minimum active seconds before warning about missing continuous focus.</summary>
    public const long MinActiveSecondsForLowFocus = 30 * 60; // 30 min

    /// <summary>Context switches above this absolute count triggers review.</summary>
    public const int HighSwitchAbsoluteThreshold = 20;

    /// <summary>Switch count vs 7-day average ratio considered "high".</summary>
    public const double HighSwitchRatio = 1.3;

    /// <summary>Minimum focus session duration (seconds) considered adequate.</summary>
    public const long AdequateFocusSeconds = 15 * 60; // 15 min

    /// <summary>App usage today vs 7-day average ratio considered a "spike".</summary>
    public const double AppSpikeRatio = 1.5;

    /// <summary>Local hour after which start is considered "late".</summary>
    public const int LateStartHour = 10;

    /// <summary>Maximum suggestions returned.</summary>
    public const int MaxSuggestions = 3;

    /// <summary>
    /// Generates up to <see cref="MaxSuggestions"/> insight suggestions.
    /// Returns an empty list when there is insufficient data or no rules fire.
    /// </summary>
    /// <param name="today">Today's activity summary.</param>
    /// <param name="trend">7-day trend result (may be null).</param>
    public static IReadOnlyList<InsightSuggestion> Generate(
        DailyActivitySummary today,
        WeeklyTrendResult? trend)
    {
        var suggestions = new List<InsightSuggestion>();

        // Guard: not enough data to say anything meaningful
        if (today.TotalActiveDurationSeconds < MinActiveSecondsForInsight
            && today.SessionCount == 0)
        {
            return suggestions;
        }

        // ── Rule 1: High context switch ──
        var switchSuggestion = CheckHighSwitch(today, trend);
        if (switchSuggestion is not null) suggestions.Add(switchSuggestion);

        // ── Rule 2: Low focus (active but unfocused) ──
        var focusSuggestion = CheckLowFocus(today, trend);
        if (focusSuggestion is not null) suggestions.Add(focusSuggestion);

        // ── Rule 3: App usage spike ──
        var appSpikeSuggestion = CheckAppUsageSpike(today, trend);
        if (appSpikeSuggestion is not null) suggestions.Add(appSpikeSuggestion);

        // ── Rule 4: Late start today ──
        var scheduleSuggestion = CheckLateStart(today);
        if (scheduleSuggestion is not null) suggestions.Add(scheduleSuggestion);

        // ── Rule 5: Positive feedback ──
        var positiveSuggestion = CheckPositiveFocus(today, trend);
        if (positiveSuggestion is not null) suggestions.Add(positiveSuggestion);

        // Cap to max
        return suggestions.Take(MaxSuggestions).ToList();
    }

    // ── Rule implementations ──

    private static InsightSuggestion? CheckHighSwitch(DailyActivitySummary today, WeeklyTrendResult? trend)
    {
        if (today.ContextSwitchCount < HighSwitchAbsoluteThreshold)
            return null;

        var exceedsAverage = trend is not null
            && trend.AverageSwitchCount > 0
            && today.ContextSwitchCount >= trend.AverageSwitchCount * HighSwitchRatio;

        if (!exceedsAverage && today.ContextSwitchCount < HighSwitchAbsoluteThreshold * 2)
            return null;

        return new InsightSuggestion
        {
            Severity = "Warning",
            Category = "Switch",
            Title = "今天跨任务切换较频繁",
            Message = "今天跨任务语境切换较多，可以尝试安排一个 25 分钟单任务块。",
            EvidenceText = $"今天跨任务语境切换 {today.ContextSwitchCount} 次"
                + (today.RawContextSwitchCount > today.ContextSwitchCount
                    ? $"（原始工具跳转 {today.RawContextSwitchCount} 次）"
                    : "")
                + (trend is not null && trend.AverageSwitchCount > 0
                    ? $"（近 7 天平均 {trend.AverageSwitchCount:F0} 次）"
                    : ""),
            ActionText = "试试：关掉非必要的通知，专注于一个应用 25 分钟。"
        };
    }

    private static InsightSuggestion? CheckLowFocus(DailyActivitySummary today, WeeklyTrendResult? trend)
    {
        // Only trigger after enough active time to judge focus quality.
        if (today.TotalActiveDurationSeconds < MinActiveSecondsForLowFocus)
            return null;

        var hasFocus = today.LongestFocusSession is not null
            && today.LongestFocusSession.Duration.TotalSeconds >= AdequateFocusSeconds;

        if (hasFocus)
            return null;

        return new InsightSuggestion
        {
            Severity = "Warning",
            Category = "Focus",
            Title = "今天缺少连续专注",
            Message = "今天缺少较长连续专注段，建议挑一个低打扰时间段做深度任务。",
            EvidenceText = $"今日活跃 {FormatMinutes(today.TotalActiveDurationSeconds)}，"
                + (today.LongestFocusSession is { } lf
                    ? $"最长连续专注仅 {FormatMinutes((long)lf.Duration.TotalSeconds)}"
                    : "未检测到完整专注段"),
            ActionText = "试试：找一个 30 分钟内不会被中断的时间块处理最重要的事。"
        };
    }

    private static InsightSuggestion? CheckAppUsageSpike(DailyActivitySummary today, WeeklyTrendResult? trend)
    {
        if (trend is null || today.TopApps.Count == 0)
            return null;

        // Check if today's top app has a significantly higher share than usual
        var topApp = today.TopApps[0];
        if (topApp.ActiveDurationSeconds < 300) // at least 5 min
            return null;

        // Find this app in the trend's top apps across the week
        var appName = string.IsNullOrWhiteSpace(topApp.DisplayName)
            ? topApp.ProcessName
            : topApp.DisplayName;

        var avgAcrossWeek = trend.Days
            .Select(d => d.TopAppName)
            .Where(n => n is not null && string.Equals(n, appName, StringComparison.OrdinalIgnoreCase))
            .Count();

        // If this app appeared as top app on most days, it's normal — not a spike
        if (avgAcrossWeek >= 4)
            return null;

        // If the absolute active time on this app is high compared to typical daily active
        if (trend.AverageActiveSeconds > 0
            && topApp.ActiveDurationSeconds >= trend.AverageActiveSeconds * AppSpikeRatio)
        {
            return new InsightSuggestion
            {
                Severity = "Neutral",
                Category = "AppUsage",
                Title = $"今日 {appName} 使用较多",
                Message = $"今天 {appName} 的活跃时长明显高于近期平均，可以复盘是否符合预期。",
                EvidenceText = $"{appName} 今日活跃 {FormatMinutes(topApp.ActiveDurationSeconds)}，"
                    + $"近 7 天日均活跃 {FormatMinutes((long)trend.AverageActiveSeconds)}",
                ActionText = "如果这是计划内的深度工作，很好；如果是被动消耗，可以考虑设个时间限制。"
            };
        }

        return null;
    }

    private static InsightSuggestion? CheckLateStart(DailyActivitySummary today)
    {
        if (today.FirstSeenAtUtc is not { } firstUtc)
            return null;

        var firstLocal = firstUtc.ToLocalTime();
        if (firstLocal.Hour < LateStartHour)
            return null;

        return new InsightSuggestion
        {
            Severity = "Neutral",
            Category = "Schedule",
            Title = "今天开始较晚",
            Message = $"今天最早活动在 {firstLocal:HH:mm}，开始明显偏晚，建议明天提前安排第一块工作。",
            EvidenceText = $"今日最早记录时间 {firstLocal:HH:mm}",
            ActionText = "试试：明天安排一个 9:00-9:30 的固定启动时间块。"
        };
    }

    private static InsightSuggestion? CheckPositiveFocus(DailyActivitySummary today, WeeklyTrendResult? trend)
    {
        if (today.LongestFocusSession is not { } lf)
            return null;

        var focusSeconds = (long)lf.Duration.TotalSeconds;
        if (focusSeconds < AdequateFocusSeconds)
            return null;

        var exceedsAverage = trend is not null
            && trend.AverageFocusSeconds > 0
            && focusSeconds >= trend.AverageFocusSeconds * HighSwitchRatio; // reuse 1.3 ratio

        if (!exceedsAverage)
        {
            // Still give positive feedback for any adequate focus session
            if (focusSeconds < 30 * 60) // at least 30 min of focus
                return null;
        }

        var appName = string.IsNullOrWhiteSpace(lf.DominantApp) ? "一个应用" : lf.DominantApp;

        return new InsightSuggestion
        {
            Severity = "Positive",
            Category = "Focus",
            Title = "今天有不错的专注段",
            Message = $"今天最长专注段达到 {FormatMinutes(focusSeconds)}，主要在 {appName} 上。保持这个节奏！",
            EvidenceText = $"最长专注段 {FormatMinutes(focusSeconds)}"
                + (trend is not null && trend.AverageFocusSeconds > 0
                    ? $"（近 7 天平均 {FormatMinutes((long)trend.AverageFocusSeconds)}）"
                    : ""),
            ActionText = "继续保持这个节奏，明天可以尝试把最困难的任务安排在这个时间段。"
        };
    }

    internal static string FormatMinutes(long seconds)
    {
        if (seconds <= 0) return "0 分钟";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分钟"
            : $"{span.Minutes} 分钟";
    }
}
