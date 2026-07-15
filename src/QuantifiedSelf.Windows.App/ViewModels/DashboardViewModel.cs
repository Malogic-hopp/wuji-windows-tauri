using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuantifiedSelf.Windows.ApplicationLayer.Analytics;
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
    private string _stableWorkText = "分析引擎升级后可用";
    private string _totalActiveTrendText = "";
    private string _contextSwitchTrendText = "";
    private string _longestFocusTimeRangeText = "";
    private string _summaryText = "暂无今日活动数据。Agent 运行并写入数据后会显示在这里。";
    private string _statusText = "暂无数据。";
    private string _activeTrendText = "";
    private string _yesterdayActiveTrendText = "";
    private string _weekActiveTrendText = "";
    private string _focusTrendText = "";
    private string _switchTrendText = "";
    private string _generatedAtText = "";
    private ISeries[] _activeTrendSeries = CreateTrendSeries([]);
    private Axis[] _activeTrendXAxes = CreateTrendXAxes([]);
    private Axis[] _activeTrendYAxes = CreateTrendYAxes();
    private ISeries[] _topAppsSeries = [];
    private Axis[] _topAppsXAxes = [];
    private Axis[] _topAppsYAxes = [];
    private ISeries[] _hourlyActiveSeries = [];
    private Axis[] _hourlyActiveXAxes = [];
    private Axis[] _hourlyActiveYAxes = [];
    private ISeries[] _appShareSeries = [];
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

    private string _appCountText = "-";
    public string AppCountText
    {
        get => _appCountText;
        private set => SetProperty(ref _appCountText, value);
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

    /// <summary>
    /// Stable work duration — currently a placeholder until ContextSegment/WorkBlock analysis is available.
    /// </summary>
    public string StableWorkText
    {
        get => _stableWorkText;
        private set => SetProperty(ref _stableWorkText, value);
    }

    public string TotalActiveTrendText
    {
        get => _totalActiveTrendText;
        private set => SetProperty(ref _totalActiveTrendText, value);
    }

    public string ContextSwitchTrendText
    {
        get => _contextSwitchTrendText;
        private set => SetProperty(ref _contextSwitchTrendText, value);
    }

    public string LongestFocusTimeRangeText
    {
        get => _longestFocusTimeRangeText;
        private set => SetProperty(ref _longestFocusTimeRangeText, value);
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

    public ISeries[] TopAppsSeries
    {
        get => _topAppsSeries;
        private set => SetProperty(ref _topAppsSeries, value);
    }

    public Axis[] TopAppsXAxes
    {
        get => _topAppsXAxes;
        private set => SetProperty(ref _topAppsXAxes, value);
    }

    public Axis[] TopAppsYAxes
    {
        get => _topAppsYAxes;
        private set => SetProperty(ref _topAppsYAxes, value);
    }

    public ISeries[] HourlyActiveSeries
    {
        get => _hourlyActiveSeries;
        private set => SetProperty(ref _hourlyActiveSeries, value);
    }

    public Axis[] HourlyActiveXAxes
    {
        get => _hourlyActiveXAxes;
        private set => SetProperty(ref _hourlyActiveXAxes, value);
    }

    public Axis[] HourlyActiveYAxes
    {
        get => _hourlyActiveYAxes;
        private set => SetProperty(ref _hourlyActiveYAxes, value);
    }

    public ISeries[] AppShareSeries
    {
        get => _appShareSeries;
        private set => SetProperty(ref _appShareSeries, value);
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
        private set
        {
            if (SetProperty(ref _hasLoadError, value)) OnPropertyChanged(nameof(State));
        }
    }

    private bool _hasAnyActivity;

    public bool HasAnyActivity
    {
        get => _hasAnyActivity;
        private set
        {
            if (SetProperty(ref _hasAnyActivity, value)) OnPropertyChanged(nameof(State));
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(State));
            }
        }
    }

    public PageState State => IsLoading
        ? PageState.Loading
        : HasLoadError ? PageState.Error
        : HasAnyActivity ? PageState.Ready
        : PageState.Empty;

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
                ? "今天概览加载失败。"
                : $"今天概览加载失败：{safeMessage}";
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

        var activeHours = trend.Days.Select(d => d.ActiveSeconds / 3600.0).ToArray();
        ActiveTrendSeries = CreateTrendSeries(activeHours);
        ActiveTrendXAxes = CreateTrendXAxes(TrendDays.Select(d => $"{d.DayLabel}\n{d.DateLabel}").ToArray());
        ActiveTrendYAxes = CreateTrendYAxes(activeHours.Length == 0 ? null : activeHours.Max());
    }

    private void ApplySummary(DailyActivitySummary summary)
    {
        TotalActiveText = FormatDurationLong(summary.TotalActiveDurationSeconds);
        TotalDurationText = FormatDurationLong(summary.TotalDurationSeconds);
        SampleCountText = summary.SampleCount.ToString("N0");
        SessionCountText = summary.SessionCount.ToString();
        AppCountText = summary.TopApps?.Count.ToString() ?? "-";
        HasAnyActivity = summary.SessionCount > 0 || summary.SampleCount > 0 || summary.TotalActiveDurationSeconds > 0;

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
        if (summary.TopApps is not null)
        {
            foreach (var app in summary.TopApps)
            {
                TopApps.Add(app);
            }
        }

        // Top windows
        TopWindows.Clear();
        foreach (var window in summary.TopWindows)
        {
            TopWindows.Add(window);
        }

        // App switching count (NOT task/context switching — ContextSegment is not yet implemented)
        ContextSwitchText = summary.ContextSwitchCount > 0
            ? $"{summary.ContextSwitchCount} 次应用切换"
            : "0 次应用切换";

        if (summary.LongestFocusSession is { } lf)
        {
            var dur = lf.Duration;
            LongestFocusText = FormatDurationLong((long)dur.TotalSeconds);
            LongestFocusTimeRangeText = $"{lf.StartUtc.ToLocalTime():HH:mm}—{lf.EndUtc.ToLocalTime():HH:mm}";
        }
        else
        {
            LongestFocusText = "-";
            LongestFocusTimeRangeText = "";
        }

        // Trend comparison text (simplified — compares against cached trend data)
        if (_lastTrend is not null && _lastTrend.Days.Count >= 2)
        {
            var todayIdx = _lastTrend.Days.Count - 1;
            var today = _lastTrend.Days[todayIdx];
            var avgOthers = _lastTrend.Days.Take(todayIdx).Select(d => d.ActiveSeconds).DefaultIfEmpty(0).Average();
            var diff = today.ActiveSeconds - (long)avgOthers;
            TotalActiveTrendText = diff switch
            {
                > 60 => $"较近{_lastTrend.Days.Count - 1}日多 {FormatDurationLong(diff)}",
                < -60 => $"较近{_lastTrend.Days.Count - 1}日少 {FormatDurationLong(-diff)}",
                _ => "与近期持平"
            };
        }
        else
        {
            TotalActiveTrendText = "";
        }

        // App switch frequency description (NOT task/context switch — data model not yet available)
        ContextSwitchTrendText = summary.ContextSwitchCount switch
        {
            > 30 => "应用切换较频繁",
            > 15 => "应用切换适中",
            > 0 => "应用切换较少",
            _ => ""
        };

        // Stable work: placeholder until ContextSegment analysis is available
        StableWorkText = GetStableWorkPlaceholder(summary);

        // Generate insight suggestions
        Suggestions.Clear();
        var suggestions = InsightSuggestionEngine.Generate(summary, _lastTrend);
        foreach (var s in suggestions)
        {
            Suggestions.Add(s);
        }

        // Build chart series
        BuildTopAppsChart(summary.TopApps ?? []);
        BuildHourlyActiveChart(summary.HourlyActivity);
        BuildAppShareChart(summary.TopApps ?? [], summary.TotalActiveDurationSeconds);

        // Record generation time
        GeneratedAtText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        // One-sentence summary
        SummaryText = BuildSummaryText(summary);
        StatusText = summary.SessionCount > 0
            ? $"已记录 {summary.SessionCount} 个应用会话，{summary.ContextSwitchCount} 次应用切换。"
            : "今天还没有足够的活动数据。";
    }

    private void ClearAll()
    {
        TotalActiveText = "-";
        TotalDurationText = "-";
        SampleCountText = "-";
        SessionCountText = "-";
        AppCountText = "-";
        TimeRangeText = "-";
        ContextSwitchText = "-";
        LongestFocusText = "-";
        StableWorkText = "分析引擎升级后可用";
        TotalActiveTrendText = "";
        ContextSwitchTrendText = "";
        LongestFocusTimeRangeText = "";
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
        TopAppsSeries = [];
        TopAppsXAxes = [];
        TopAppsYAxes = [];
        HourlyActiveSeries = [];
        HourlyActiveXAxes = [];
        HourlyActiveYAxes = [];
        AppShareSeries = [];
        Suggestions.Clear();
        Heatmap = new HourActivityHeatmapViewModel();
        GeneratedAtText = "";
        SummaryText = "暂无今日活动数据。Agent 运行并写入数据后会显示在这里。";
    }

    /// <summary>
    /// Returns a placeholder for stable work until ContextSegment/WorkBlock analysis is available.
    /// </summary>
    private static string GetStableWorkPlaceholder(DailyActivitySummary summary)
    {
        if (summary.TotalActiveDurationSeconds <= 0) return "暂无数据";
        // Temporary: estimate stable work as active time minus idle-heavy sessions
        // In the future, this will be replaced by actual WorkBlock analysis
        return "分析引擎升级后可用";
    }

    private static ISeries[] CreateTrendSeries(double[] activeHours)
    {
        return
        [
            new ColumnSeries<double>
            {
                Values = activeHours,
                Name = "Active",
                AnimationsSpeed = TimeSpan.Zero,
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
                TicksPaint = null,
                AnimationsSpeed = TimeSpan.Zero
            }
        ];
    }

    private static Axis[] CreateTrendYAxes(double? maxActiveHours = null)
    {
        var maxLimit = maxActiveHours is > 0
            ? Math.Max(1.0, maxActiveHours.Value * 1.22)
            : (double?)null;

        return
        [
            new Axis
            {
                MinLimit = 0,
                MaxLimit = maxLimit,
                TextSize = 11,
                Labeler = value => FormatHoursCompact(value),
                LabelsPaint = new SolidColorPaint(new SKColor(82, 96, 109)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(226, 232, 240)) { StrokeThickness = 1 },
                TicksPaint = null,
                AnimationsSpeed = TimeSpan.Zero
            }
        ];
    }

    private static string FormatHoursCompact(double hours)
    {
        if (hours <= 0)
        {
            return "0分";
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
            1 => $"；主要在 {topAppNames[0]}",
            _ => $"；主要在 {string.Join("、", topAppNames)}"
        };

        var sampleInfo = summary.SampleCount > 0
            ? $"；{summary.SampleCount:N0} 次采样"
            : string.Empty;

        // Longest session info (app-session based, NOT "focus" or "work block")
        var sessionInfo = string.Empty;
        if (summary.LongestFocusSession is { } lf)
        {
            var dur = lf.Duration;
            var sessionText = FormatDurationLong((long)dur.TotalSeconds);
            sessionInfo = $"；最长会话 {sessionText}";
        }

        // App switch count — factual, not interpreted as "task switching"
        var switchInfo = summary.ContextSwitchCount > 0
            ? $"；{summary.ContextSwitchCount} 次应用切换"
            : string.Empty;

        return $"今日活跃 {activeText}{appList}{sampleInfo}{sessionInfo}{switchInfo}。";
    }

    internal static string FormatDurationLong(long totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "0m";
        }

        var span = TimeSpan.FromSeconds(totalSeconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}小时 {span.Minutes}分"
            : span.Minutes > 0
                ? $"{span.Minutes}分 {span.Seconds}秒"
                : $"{span.Seconds}秒";
    }

    // ── Top Apps Horizontal Bar Chart ──

    private static readonly SKColor[] TopAppBarColors =
    [
        new(15, 118, 110),
        new(63, 143, 136),
        new(106, 166, 160),
        new(140, 185, 180),
        new(173, 203, 199),
    ];

    private void BuildTopAppsChart(IReadOnlyList<AppUsageSummary> topApps)
    {
        if (topApps.Count == 0)
        {
            TopAppsSeries = [];
            TopAppsXAxes = [];
            TopAppsYAxes = [];
            return;
        }

        // RowSeries draws from bottom to top, so reverse to put #1 at top
        var ordered = topApps.Take(TopAppsLimit).Reverse().ToList();
        var values = ordered.Select(a => (double)a.ActiveDurationSeconds).ToArray();
        var labels = ordered.Select(a => GetAppDisplayLabel(a)).ToArray();
        var maxValue = values.Length > 0 ? values.Max() : 0.0;

        TopAppsSeries =
        [
            new MultiColorRowSeries(TopAppBarColors.Take(values.Length).Reverse().ToArray())
            {
                Values = values,
                Name = "活跃时长",
                AnimationsSpeed = TimeSpan.Zero,
                MaxBarWidth = 24,
                Padding = 6,
                XToolTipLabelFormatter = point =>
                {
                    // RowSeries: SecondaryValue = category index, PrimaryValue = data value
                    var index = (int)Math.Round(point.Coordinate.SecondaryValue);
                    var label = index >= 0 && index < labels.Length ? labels[index] : "?";
                    return $"{label} · {FormatDurationLong((long)point.Coordinate.PrimaryValue)}";
                },
                YToolTipLabelFormatter = _ => string.Empty,
                DataLabelsPaint = new SolidColorPaint(new SKColor(82, 96, 109)),
                DataLabelsSize = 11,
                DataLabelsFormatter = point => FormatDurationLong((long)point.Coordinate.PrimaryValue),
                DataLabelsPosition = LiveChartsCore.Measure.DataLabelsPosition.End
            }
        ];

        TopAppsXAxes =
        [
            new Axis
            {
                MinLimit = 0,
                MaxLimit = Math.Max(1.0, maxValue * 1.15),
                TextSize = 11,
                Labeler = value => FormatDurationLong((long)value),
                LabelsPaint = new SolidColorPaint(new SKColor(82, 96, 109)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(226, 232, 240)) { StrokeThickness = 1 },
                TicksPaint = null,
                AnimationsSpeed = TimeSpan.Zero
            }
        ];

        TopAppsYAxes =
        [
            new Axis
            {
                Labels = labels,
                TextSize = 12,
                LabelsPaint = new SolidColorPaint(new SKColor(16, 42, 67)),
                SeparatorsPaint = null,
                TicksPaint = null,
                AnimationsSpeed = TimeSpan.Zero
            }
        ];
    }

    private static string GetAppDisplayLabel(AppUsageSummary app)
    {
        return string.IsNullOrWhiteSpace(app.DisplayName) ? app.ProcessName : app.DisplayName;
    }

    // ── Today 24h Active Column Chart ──

    private void BuildHourlyActiveChart(IReadOnlyList<HourlyActivity> hourly)
    {
        if (hourly.Count == 0)
        {
            HourlyActiveSeries = [];
            HourlyActiveXAxes = [];
            HourlyActiveYAxes = [];
            return;
        }

        var activeValues = hourly.Select(h => h.ActiveSeconds).ToArray();
        var maxValue = activeValues.Length > 0 ? activeValues.Max() : 0.0;
        var xLabels = hourly.Select(h => h.Hour % 3 == 0 ? $"{h.Hour}:00" : "").ToArray();

        HourlyActiveSeries =
        [
            new ColumnSeries<double>
            {
                Values = activeValues,
                Name = "Active",
                AnimationsSpeed = TimeSpan.Zero,
                Fill = new SolidColorPaint(new SKColor(15, 118, 110)),
                Stroke = null,
                MaxBarWidth = 18,
                Padding = 2,
                XToolTipLabelFormatter = point =>
                {
                    // ColumnSeries: SecondaryValue = category index (0-23 = hour)
                    var hour = (int)Math.Round(point.Coordinate.SecondaryValue);
                    return $"{hour}:00 · Active {FormatDurationCompact(point.Coordinate.PrimaryValue)}";
                },
                YToolTipLabelFormatter = _ => string.Empty
            }
        ];

        HourlyActiveXAxes =
        [
            new Axis
            {
                Labels = xLabels,
                TextSize = 10,
                LabelsPaint = new SolidColorPaint(new SKColor(82, 96, 109)),
                SeparatorsPaint = null,
                TicksPaint = null,
                AnimationsSpeed = TimeSpan.Zero
            }
        ];

        HourlyActiveYAxes =
        [
            new Axis
            {
                MinLimit = 0,
                MaxLimit = Math.Max(1.0, maxValue * 1.15),
                TextSize = 11,
                Labeler = value => FormatDurationCompact(value),
                LabelsPaint = new SolidColorPaint(new SKColor(82, 96, 109)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(226, 232, 240)) { StrokeThickness = 1 },
                TicksPaint = null,
                AnimationsSpeed = TimeSpan.Zero
            }
        ];
    }

    private static string FormatDurationCompact(double totalSeconds)
    {
        if (totalSeconds <= 0) return "0s";
        if (totalSeconds < 60) return $"{totalSeconds:0}s";
        if (totalSeconds < 3600) return $"{totalSeconds / 60:0}m";
        return $"{totalSeconds / 3600:0.#}h";
    }

    // ── App Share Donut Chart ──

    private void BuildAppShareChart(IReadOnlyList<AppUsageSummary> topApps, long totalActiveSeconds)
    {
        if (topApps.Count == 0 || totalActiveSeconds <= 0)
        {
            AppShareSeries = [];
            return;
        }

        var top5 = topApps.Take(5).ToList();
        var top5Total = top5.Sum(a => (long)a.ActiveDurationSeconds);

        // Safety: if top5 somehow exceeds the real total, clamp and skip Other
        if (top5Total > totalActiveSeconds) top5Total = totalActiveSeconds;
        var otherSeconds = totalActiveSeconds - top5Total;

        var seriesList = new List<ISeries>();

        for (var i = 0; i < top5.Count; i++)
        {
            var app = top5[i];
            var label = GetAppDisplayLabel(app);
            seriesList.Add(new PieSeries<double>
            {
                Values = [app.ActiveDurationSeconds],
                Name = label,
                AnimationsSpeed = TimeSpan.Zero,
                Pushout = 0,
                Fill = new SolidColorPaint(TopAppBarColors[i]),
                Stroke = new SolidColorPaint(new SKColor(255, 255, 255)) { StrokeThickness = 2 },
                DataLabelsPosition = LiveChartsCore.Measure.PolarLabelsPosition.Outer,
                DataLabelsPaint = new SolidColorPaint(new SKColor(82, 96, 109)),
                DataLabelsSize = 11,
                DataLabelsFormatter = _ => label,
                ToolTipLabelFormatter = _ =>
                    $"{label} · {FormatDurationLong(app.ActiveDurationSeconds)}"
            });
        }

        if (otherSeconds > 0)
        {
            seriesList.Add(new PieSeries<double>
            {
                Values = [otherSeconds],
                Name = "其他",
                AnimationsSpeed = TimeSpan.Zero,
                Pushout = 0,
                Fill = new SolidColorPaint(new SKColor(148, 163, 184)), // #94A3B8 — Other
                Stroke = new SolidColorPaint(new SKColor(255, 255, 255)) { StrokeThickness = 2 },
                DataLabelsPosition = LiveChartsCore.Measure.PolarLabelsPosition.Outer,
                DataLabelsPaint = new SolidColorPaint(new SKColor(82, 96, 109)),
                DataLabelsSize = 11,
                DataLabelsFormatter = _ => "其他",
                ToolTipLabelFormatter = _ => $"其他 · {FormatDurationLong(otherSeconds)}"
            });
        }

        AppShareSeries = seriesList.ToArray();
    }
}
