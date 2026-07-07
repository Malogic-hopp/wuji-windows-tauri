using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;
using SkiaSharp;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed class DashboardViewModel : ObservableObject
{
    private const int TopAppsLimit = 5;
    private const int TopWindowsLimit = 10;
    private const double TrendBarMaxHeight = 72.0;

    private readonly Func<int, int, CancellationToken, Task<DailyActivitySummary>> _loadSummaryAsync;
    private readonly WeeklyTrendService? _weeklyTrendService;
    private readonly HourActivityHeatmapService? _heatmapService;

    // Cache the last successful summary and trend so old data is preserved on refresh failure.
    private DailyActivitySummary? _lastSummary;
    private WeeklyTrendResult? _lastTrend;

    private string _totalActiveText = "-";
    private string _totalDurationText = "-";
    private string _sampleCountText = "-";
    private string _sessionCountText = "-";
    private string _timeRangeText = "-";
    private string _contextSwitchText = "-";
    private string _longestFocusText = "-";
    private string _summaryText = "暂无今日活动数据。Agent 运行并写入数据后会显示在这里。";
    private string _statusText = "No data loaded.";
    private string _activeTrendText = "";
    private string _yesterdayActiveTrendText = "";
    private string _weekActiveTrendText = "";
    private string _focusTrendText = "";
    private string _switchTrendText = "";
    private string _generatedAtText = "";
    private ISeries[] _activeTrendSeries = CreateTrendSeries([]);
    private Axis[] _activeTrendXAxes = CreateTrendXAxes([]);
    private Axis[] _activeTrendYAxes = CreateTrendYAxes();
    private bool _hasLoadError;
    private bool _isLoading;

    public DashboardViewModel(DailyStatsService dailyStatsService,
        WeeklyTrendService? weeklyTrendService = null,
        HourActivityHeatmapService? heatmapService = null)
        : this((topApps, topWindows, ct) => dailyStatsService.GetTodaySummaryAsync(topApps, topWindows, ct),
               weeklyTrendService, heatmapService)
    {
    }

    public DashboardViewModel(Func<int, int, CancellationToken, Task<DailyActivitySummary>> loadSummaryAsync,
        WeeklyTrendService? weeklyTrendService = null,
        HourActivityHeatmapService? heatmapService = null)
    {
        ArgumentNullException.ThrowIfNull(loadSummaryAsync);
        _loadSummaryAsync = loadSummaryAsync;
        _weeklyTrendService = weeklyTrendService;
        _heatmapService = heatmapService;
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
    }

    public ObservableCollection<AppUsageSummary> TopApps { get; } = new();

    public ObservableCollection<DailyWindowUsageSummary> TopWindows { get; } = new();

    public IAsyncRelayCommand RefreshCommand { get; }

    public string TotalActiveText
    {
        get => _totalActiveText;
        private set => SetProperty(ref _totalActiveText, value);
    }

    public string TotalDurationText
    {
        get => _totalDurationText;
        private set => SetProperty(ref _totalDurationText, value);
    }

    public string SampleCountText
    {
        get => _sampleCountText;
        private set => SetProperty(ref _sampleCountText, value);
    }

    public string SessionCountText
    {
        get => _sessionCountText;
        private set => SetProperty(ref _sessionCountText, value);
    }

    public string TimeRangeText
    {
        get => _timeRangeText;
        private set => SetProperty(ref _timeRangeText, value);
    }

    public string ContextSwitchText
    {
        get => _contextSwitchText;
        private set => SetProperty(ref _contextSwitchText, value);
    }

    public string LongestFocusText
    {
        get => _longestFocusText;
        private set => SetProperty(ref _longestFocusText, value);
    }

    public string ActiveTrendText
    {
        get => _activeTrendText;
        private set => SetProperty(ref _activeTrendText, value);
    }

    public string YesterdayActiveTrendText
    {
        get => _yesterdayActiveTrendText;
        private set => SetProperty(ref _yesterdayActiveTrendText, value);
    }

    public string WeekActiveTrendText
    {
        get => _weekActiveTrendText;
        private set => SetProperty(ref _weekActiveTrendText, value);
    }

    public string FocusTrendText
    {
        get => _focusTrendText;
        private set => SetProperty(ref _focusTrendText, value);
    }

    public string SwitchTrendText
    {
        get => _switchTrendText;
        private set => SetProperty(ref _switchTrendText, value);
    }

    /// <summary>
    /// When the Dashboard stats were last generated, e.g. "2026-07-06 14:30:00".
    /// Empty when no data has been loaded.
    /// </summary>
    public string GeneratedAtText
    {
        get => _generatedAtText;
        private set => SetProperty(ref _generatedAtText, value);
    }

    public ObservableCollection<TrendDayItem> TrendDays { get; } = new();

    public ISeries[] ActiveTrendSeries
    {
        get => _activeTrendSeries;
        private set => SetProperty(ref _activeTrendSeries, value);
    }

    public Axis[] ActiveTrendXAxes
    {
        get => _activeTrendXAxes;
        private set => SetProperty(ref _activeTrendXAxes, value);
    }

    public Axis[] ActiveTrendYAxes
    {
        get => _activeTrendYAxes;
        private set => SetProperty(ref _activeTrendYAxes, value);
    }

    public ObservableCollection<InsightSuggestion> Suggestions { get; } = new();

    private HourActivityHeatmapViewModel _heatmap = new();
    public HourActivityHeatmapViewModel Heatmap
    {
        get => _heatmap;
        private set => SetProperty(ref _heatmap, value);
    }

    /// <summary>
    /// One-sentence natural-language summary of today's activity.
    /// </summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasLoadError
    {
        get => _hasLoadError;
        private set => SetProperty(ref _hasLoadError, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IsLoading = true;
            var summary = await _loadSummaryAsync(TopAppsLimit, TopWindowsLimit, cancellationToken);

            // Update cached summary and clear error state
            _lastSummary = summary;
            HasLoadError = false;

            ApplySummary(summary);
        }
        catch (Exception ex)
        {
            HasLoadError = true;

            // Preserve old data — only clear if there was never a successful load.
            if (_lastSummary is null)
            {
                ClearAll();
            }

            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            StatusText = string.IsNullOrWhiteSpace(safeMessage)
                ? "Dashboard load failed."
                : $"Dashboard load failed: {safeMessage}";
        }
        finally
        {
            IsLoading = false;
        }

        // Load trend in background — trend failure should not block or break summary display
        if (_weeklyTrendService is not null)
        {
            await LoadTrendAsync(cancellationToken);
        }

        // Load heatmap (independent of trend — always attempt if service is available)
        await LoadHeatmapAsync(cancellationToken);
    }

    private async Task LoadTrendAsync(CancellationToken cancellationToken)
    {
        try
        {
            var trend = await _weeklyTrendService!.GetWeeklyTrendAsync(cancellationToken);
            _lastTrend = trend;
            ApplyTrend(trend);

            // Re-generate suggestions now that trend data is available
            if (_lastSummary is not null)
            {
                Suggestions.Clear();
                var suggestions = InsightSuggestionEngine.Generate(_lastSummary, _lastTrend);
                foreach (var s in suggestions)
                {
                    Suggestions.Add(s);
                }
            }
        }
        catch
        {
            // Keep previous trend data on failure
        }

    }

    private async Task LoadHeatmapAsync(CancellationToken cancellationToken)
    {
        if (_heatmapService is null) return;

        try
        {
            var heatmap = await _heatmapService.GetHeatmapAsync(cancellationToken);
            Heatmap = heatmap;
        }
        catch
        {
            // Preserve old heatmap data on refresh failure — never replace
            // with an empty view.
        }
    }

    private void ApplyTrend(WeeklyTrendResult trend)
    {
        ActiveTrendText = trend.ActiveComparisonText;
        YesterdayActiveTrendText = trend.YesterdayActiveComparisonText;
        WeekActiveTrendText = trend.WeekActiveComparisonText;
        FocusTrendText = trend.FocusComparisonText;
        SwitchTrendText = trend.SwitchComparisonText;

        TrendDays.Clear();
        if (trend.Days.Count == 0)
        {
            ActiveTrendSeries = CreateTrendSeries([]);
            ActiveTrendXAxes = CreateTrendXAxes([]);
            ActiveTrendYAxes = CreateTrendYAxes();
            return;
        }

        // Build trend day items with normalized bar heights.
        var maxActive = trend.Days.Max(d => d.ActiveSeconds);
        if (maxActive <= 0) maxActive = 1;

        var today = DateOnly.FromDateTime(DateTime.Now);

        foreach (var day in trend.Days)
        {
            var localDate = DateOnly.FromDateTime(day.Date);
            var ratio = Math.Clamp((double)day.ActiveSeconds / maxActive, 0.0, 1.0);
            var dayLabel = localDate == today
                ? "今天"
                : day.Date.DayOfWeek switch
                {
                    DayOfWeek.Monday => "周一",
                    DayOfWeek.Tuesday => "周二",
                    DayOfWeek.Wednesday => "周三",
                    DayOfWeek.Thursday => "周四",
                    DayOfWeek.Friday => "周五",
                    DayOfWeek.Saturday => "周六",
                    DayOfWeek.Sunday => "周日",
                    _ => localDate.ToString("MM/dd")
                };

            TrendDays.Add(new TrendDayItem
            {
                DayLabel = dayLabel,
                DateLabel = localDate.ToString("MM/dd"),
                ActiveText = FormatDurationLong(day.ActiveSeconds),
                BarWidthRatio = ratio,
                BarHeightRatio = ratio,
                BarHeightPixels = Math.Round(TrendBarMaxHeight * ratio, MidpointRounding.AwayFromZero),
                IsToday = localDate == today
            });
        }

        ActiveTrendSeries = CreateTrendSeries(trend.Days.Select(d => d.ActiveSeconds / 3600.0).ToArray());
        ActiveTrendXAxes = CreateTrendXAxes(TrendDays.Select(d => $"{d.DayLabel}\n{d.DateLabel}").ToArray());
        ActiveTrendYAxes = CreateTrendYAxes();
    }

    private void ApplySummary(DailyActivitySummary summary)
    {
        TotalActiveText = FormatDurationLong(summary.TotalActiveDurationSeconds);
        TotalDurationText = FormatDurationLong(summary.TotalDurationSeconds);
        SampleCountText = summary.SampleCount.ToString("N0");
        SessionCountText = summary.SessionCount.ToString();

        // Time range
        if (summary.FirstSeenAtUtc is { } first && summary.LastSeenAtUtc is { } last)
        {
            var firstLocal = first.ToLocalTime();
            var lastLocal = last.ToLocalTime();
            TimeRangeText = firstLocal.Date == lastLocal.Date
                ? $"{firstLocal:HH:mm} — {lastLocal:HH:mm}"
                : $"{firstLocal:yyyy-MM-dd HH:mm} — {lastLocal:yyyy-MM-dd HH:mm}";
        }
        else
        {
            TimeRangeText = "-";
        }

        // Top apps
        TopApps.Clear();
        foreach (var app in summary.TopApps)
        {
            TopApps.Add(app);
        }

        // Top windows
        TopWindows.Clear();
        foreach (var window in summary.TopWindows)
        {
            TopWindows.Add(window);
        }

        // Focus metrics
        ContextSwitchText = summary.ContextSwitchCount > 0
            ? $"{summary.ContextSwitchCount} switches"
            : "0";

        if (summary.LongestFocusSession is { } lf)
        {
            var dur = lf.Duration;
            LongestFocusText = dur.TotalHours >= 1
                ? $"{(int)dur.TotalHours}h {dur.Minutes}m"
                : $"{dur.Minutes}m {dur.Seconds}s";
        }
        else
        {
            LongestFocusText = "-";
        }

        // Generate insight suggestions
        Suggestions.Clear();
        var suggestions = InsightSuggestionEngine.Generate(summary, _lastTrend);
        foreach (var s in suggestions)
        {
            Suggestions.Add(s);
        }

        // Record generation time
        GeneratedAtText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        // One-sentence summary
        SummaryText = BuildSummaryText(summary);
        StatusText = summary.SessionCount > 0
            ? $"Today: {TotalActiveText} active across {summary.SessionCount} sessions."
            : "No activity recorded for today.";
    }

    private void ClearAll()
    {
        TotalActiveText = "-";
        TotalDurationText = "-";
        SampleCountText = "-";
        SessionCountText = "-";
        TimeRangeText = "-";
        ContextSwitchText = "-";
        LongestFocusText = "-";
        ActiveTrendText = "";
        YesterdayActiveTrendText = "";
        WeekActiveTrendText = "";
        FocusTrendText = "";
        SwitchTrendText = "";
        TopApps.Clear();
        TopWindows.Clear();
        TrendDays.Clear();
        ActiveTrendSeries = CreateTrendSeries([]);
        ActiveTrendXAxes = CreateTrendXAxes([]);
        ActiveTrendYAxes = CreateTrendYAxes();
        Suggestions.Clear();
        Heatmap = new HourActivityHeatmapViewModel();
        GeneratedAtText = "";
        SummaryText = "暂无今日活动数据。Agent 运行并写入数据后会显示在这里。";
    }

    private static ISeries[] CreateTrendSeries(double[] activeHours)
    {
        return
        [
            new ColumnSeries<double>
            {
                Values = activeHours,
                Name = "Active",
                Fill = new SolidColorPaint(new SKColor(15, 118, 110)),
                Stroke = new SolidColorPaint(new SKColor(17, 94, 89)) { StrokeThickness = 1 },
                MaxBarWidth = 28,
                Padding = 8,
                DataLabelsPaint = new SolidColorPaint(new SKColor(82, 96, 109)),
                DataLabelsSize = 11,
                DataLabelsFormatter = point => FormatHoursCompact(point.Coordinate.PrimaryValue)
            }
        ];
    }

    private static Axis[] CreateTrendXAxes(string[] labels)
    {
        return
        [
            new Axis
            {
                Labels = labels,
                TextSize = 11,
                LabelsPaint = new SolidColorPaint(new SKColor(82, 96, 109)),
                SeparatorsPaint = null,
                TicksPaint = null
            }
        ];
    }

    private static Axis[] CreateTrendYAxes()
    {
        return
        [
            new Axis
            {
                MinLimit = 0,
                TextSize = 11,
                Labeler = value => FormatHoursCompact(value),
                LabelsPaint = new SolidColorPaint(new SKColor(82, 96, 109)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(226, 232, 240)) { StrokeThickness = 1 },
                TicksPaint = null
            }
        ];
    }

    private static string FormatHoursCompact(double hours)
    {
        if (hours <= 0)
        {
            return "0m";
        }

        if (hours < 1)
        {
            return $"{Math.Round(hours * 60):0}m";
        }

        return $"{hours:0.#}h";
    }

    private static string BuildSummaryText(DailyActivitySummary summary)
    {
        if (summary.SessionCount == 0)
        {
            return "暂无今日活动数据。Agent 运行并写入数据后会显示在这里。";
        }

        var activeText = FormatDurationLong(summary.TotalActiveDurationSeconds);

        if (summary.TotalActiveDurationSeconds <= 0)
        {
            return $"今日暂未检测到活跃活动，共记录 {summary.SessionCount} 个会话。";
        }

        var topAppNames = summary.TopApps
            .Take(3)
            .Select(a => string.IsNullOrWhiteSpace(a.DisplayName) ? a.ProcessName : a.DisplayName)
            .ToList();

        var appList = topAppNames.Count switch
        {
            0 => string.Empty,
            1 => $"主要在 {topAppNames[0]}",
            _ => $"主要在 {string.Join("、", topAppNames)}"
        };

        var sampleInfo = summary.SampleCount > 0
            ? $"，{summary.SampleCount} 次采样"
            : string.Empty;

        var focusInfo = string.Empty;
        if (summary.LongestFocusSession is { } lf)
        {
            var dur = lf.Duration;
            var focusText = dur.TotalHours >= 1
                ? $"{(int)dur.TotalHours}h {dur.Minutes}m"
                : $"{dur.Minutes}m";

            focusInfo = lf.IsFragmented
                ? $"，最长专注 {focusText}（较分散）"
                : $"，最长专注 {focusText}";
        }
        else if (summary.ContextSwitchCount > 0)
        {
            focusInfo = "，暂未检测到完整专注段";
        }

        var switchInfo = summary.ContextSwitchCount > 20
            ? "，跨任务切换较为频繁"
            : string.Empty;

        return $"今日活跃 {activeText}{appList}{sampleInfo}{focusInfo}{switchInfo}。";
    }

    internal static string FormatDurationLong(long totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "0m";
        }

        var span = TimeSpan.FromSeconds(totalSeconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m {span.Seconds}s";
    }
}
