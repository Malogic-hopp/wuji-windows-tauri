using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.ApplicationLayer.Activity;

/// <summary>
/// Read-only service that computes 7-day trend data from DailyStatsService.
/// Fetches each day in parallel, fills missing days with zero, and compares today
/// against the 7-day average. All operations are read-only — no SQLite writes.
/// </summary>
public sealed class WeeklyTrendService : IWeeklyTrendService
{
    private readonly IDailyStatsService _dailyStatsService;
    private readonly TimeProvider _timeProvider;

    public WeeklyTrendService(
        IDailyStatsService dailyStatsService,
        TimeProvider? timeProvider = null)
    {
        _dailyStatsService = dailyStatsService ?? throw new ArgumentNullException(nameof(dailyStatsService));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns 7-day trend data: 7 daily points (oldest first, today last),
    /// plus today-vs-average comparison text.
    /// Query errors are caught per-day — failed days become zero points.
    /// </summary>
    public async Task<WeeklyTrendResult> GetWeeklyTrendAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        var historyDays = Enumerable.Range(0, 14)
            .Select(i => today.AddDays(i - 13))
            .ToList(); // oldest first: today-13 ... today

        // Fetch enough history for 7-day chart, yesterday comparison, and week-to-date comparison.
        var tasks = historyDays.Select(d => FetchTrendPointAsync(d, cancellationToken));
        var historyPoints = await Task.WhenAll(tasks);
        var points = historyPoints.TakeLast(7).ToArray();

        var result = new WeeklyTrendResult { Days = points };

        // Compute 7-day averages for chart metadata and suggestions.
        var activeValues = points.Select(p => (double)p.ActiveSeconds).ToList();
        var focusValues = points.Select(p => (double)p.FocusSeconds).ToList();
        var switchValues = points.Select(p => (double)p.ContextSwitchCount).ToList();

        result.AverageActiveSeconds = activeValues.Average();
        result.AverageFocusSeconds = focusValues.Average();
        result.AverageSwitchCount = switchValues.Average();

        // Compare today against completed days. Today's partial day should be
        // shown as progress, not judged as a full-day regression.
        var todayPoint = points[6];
        var completedPoints = historyPoints.Where(p => DateOnly.FromDateTime(p.Date) < today).TakeLast(7).ToList();
        var completedActiveAverage = completedPoints.Select(p => (double)p.ActiveSeconds).Average();
        result.ActiveComparisonText = BuildActiveProgressText(todayPoint.ActiveSeconds, completedActiveAverage);
        result.YesterdayActiveComparisonText = BuildYesterdayActiveComparisonText(historyPoints, today);
        result.WeekActiveComparisonText = BuildWeekActiveComparisonText(historyPoints, today);
        result.FocusComparisonText = BuildComparisonText(
            "最长专注", todayPoint.FocusSeconds, result.AverageFocusSeconds);
        result.SwitchComparisonText = BuildSwitchComparisonText(
            todayPoint.ContextSwitchCount, result.AverageSwitchCount);

        return result;
    }

    private static string BuildYesterdayActiveComparisonText(
        IReadOnlyList<DailyTrendPoint> historyPoints,
        DateOnly today)
    {
        var yesterday = today.AddDays(-1);
        var yesterdayPoint = historyPoints.FirstOrDefault(p => DateOnly.FromDateTime(p.Date) == yesterday);
        if (yesterdayPoint is null)
        {
            return "昨日活跃暂无对比数据";
        }

        var priorSeven = historyPoints
            .Where(p =>
            {
                var date = DateOnly.FromDateTime(p.Date);
                return date < yesterday;
            })
            .TakeLast(7)
            .ToList();

        if (priorSeven.Count == 0)
        {
            return $"昨日活跃 {FormatDuration(yesterdayPoint.ActiveSeconds)}，暂无此前 7 天日均对比";
        }

        var average = priorSeven.Select(p => (double)p.ActiveSeconds).Average();
        if (average <= 0)
        {
            return yesterdayPoint.ActiveSeconds > 0
                ? $"昨日活跃 {FormatDuration(yesterdayPoint.ActiveSeconds)}，暂无此前 7 天日均对比"
                : "昨日活跃暂无此前 7 天日均对比";
        }

        return BuildCompletedPeriodComparisonText(
            "昨日活跃",
            yesterdayPoint.ActiveSeconds,
            "此前 7 天日均",
            average);
    }

    private static string BuildWeekActiveComparisonText(
        IReadOnlyList<DailyTrendPoint> historyPoints,
        DateOnly today)
    {
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var thisWeekStart = today.AddDays(-daysSinceMonday);
        var lastWeekStart = thisWeekStart.AddDays(-7);
        var lastWeekSamePeriodEnd = lastWeekStart.AddDays(daysSinceMonday);

        var thisWeekSeconds = SumActiveSeconds(historyPoints, thisWeekStart, today);
        var lastWeekSeconds = SumActiveSeconds(historyPoints, lastWeekStart, lastWeekSamePeriodEnd);

        if (lastWeekSeconds <= 0)
        {
            return thisWeekSeconds > 0
                ? $"本周至今活跃 {FormatDuration(thisWeekSeconds)}，暂无上周同期对比"
                : "本周至今活跃暂无上周同期对比";
        }

        return BuildCompletedPeriodComparisonText(
            "本周至今活跃",
            thisWeekSeconds,
            "上周同期",
            lastWeekSeconds);
    }

    private static long SumActiveSeconds(
        IReadOnlyList<DailyTrendPoint> points,
        DateOnly start,
        DateOnly endInclusive)
    {
        return points
            .Where(p =>
            {
                var date = DateOnly.FromDateTime(p.Date);
                return date >= start && date <= endInclusive;
            })
            .Sum(p => p.ActiveSeconds);
    }

    private static string BuildCompletedPeriodComparisonText(
        string subject,
        double value,
        string baselineName,
        double baseline)
    {
        var valueText = FormatDuration((long)value);
        var baselineText = FormatDuration((long)baseline);
        var delta = value - baseline;

        if (Math.Abs(delta) < 60)
        {
            return $"{subject} {valueText}，接近{baselineName} {baselineText}";
        }

        var direction = delta > 0 ? "多" : "少";
        return $"{subject} {valueText}，比{baselineName} {baselineText} {direction} {FormatDuration((long)Math.Abs(delta))}";
    }

    private async Task<DailyTrendPoint> FetchTrendPointAsync(DateOnly date, CancellationToken ct)
    {
        try
        {
            var summary = await _dailyStatsService.GetSummaryForDateAsync(date, 1, 0, ct);

            var topAppName = summary.TopApps.Count > 0
                ? (string.IsNullOrWhiteSpace(summary.TopApps[0].DisplayName)
                    ? summary.TopApps[0].ProcessName
                    : summary.TopApps[0].DisplayName)
                : null;

            var focusSeconds = summary.LongestFocusSession is { } lf
                ? (long)lf.Duration.TotalSeconds
                : 0L;

            return new DailyTrendPoint
            {
                Date = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local),
                ActiveSeconds = summary.TotalActiveDurationSeconds,
                FocusSeconds = focusSeconds,
                ContextSwitchCount = summary.ContextSwitchCount,
                SessionCount = summary.SessionCount,
                TopAppName = topAppName
            };
        }
        catch
        {
            // Failed day → zero point (don't break the whole trend)
            return new DailyTrendPoint
            {
                Date = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)
            };
        }
    }

    private static string BuildComparisonText(string metric, double todayValue, double average)
    {
        if (average <= 0)
        {
            return $"今日{metric}暂无对比数据";
        }

        var ratio = todayValue / average;

        if (ratio >= 1.3)
            return $"今日{metric}显著高于近 7 天平均";
        if (ratio >= 1.1)
            return $"今日{metric}略高于近 7 天平均";
        if (ratio <= 0.7)
            return $"今日{metric}显著低于近 7 天平均";
        if (ratio <= 0.9)
            return $"今日{metric}略低于近 7 天平均";

        return $"今日{metric}接近近 7 天平均";
    }

    private static string BuildActiveProgressText(double todaySeconds, double completedAverageSeconds)
    {
        if (completedAverageSeconds <= 0)
        {
            return todaySeconds > 0
                ? $"今日已活跃 {FormatDuration((long)todaySeconds)}，暂无完整日均对比"
                : "今日活跃暂无完整日均对比";
        }

        var averageText = FormatDuration((long)completedAverageSeconds);
        var todayText = FormatDuration((long)todaySeconds);
        var delta = todaySeconds - completedAverageSeconds;

        if (delta >= 0)
        {
            return $"今日已活跃 {todayText}，已超过此前 6 天日均 {averageText}";
        }

        return $"今日已活跃 {todayText}，距此前 6 天日均 {averageText} 还差 {FormatDuration((long)-delta)}";
    }

    private static string BuildSwitchComparisonText(double todayValue, double average)
    {
        if (average <= 0)
            return "今日切换暂无对比数据";

        var ratio = todayValue / average;

        // For switches, higher is worse
        if (ratio >= 1.3)
            return "今日切换显著多于近 7 天平均，注意分散";
        if (ratio >= 1.1)
            return "今日切换略多于近 7 天平均";
        if (ratio <= 0.7)
            return "今日切换显著少于近 7 天平均，较为专注";
        if (ratio <= 0.9)
            return "今日切换略少于近 7 天平均";

        return "今日切换次数接近近 7 天平均";
    }

    private static string FormatDuration(long totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "0m";
        }

        var span = TimeSpan.FromSeconds(totalSeconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m";
    }
}
