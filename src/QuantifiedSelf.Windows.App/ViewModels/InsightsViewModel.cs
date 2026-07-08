using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed class InsightsViewModel : ObservableObject
{
    private readonly Func<DateOnly, CancellationToken, Task<FocusInterruptionInsight>> _loadInsightAsync;

    private bool _isLoading;
    private bool _hasLoadError;
    private bool _hasInsightData;
    private int _insightDataCount;
    private DateTime? _selectedDate = DateTime.Today;
    private string _selectedDateText = DateTime.Today.ToString("yyyy-MM-dd");
    private string _statusText = "Ready.";
    private string _summaryText = "暂无今日洞察数据。Agent 运行并写入数据后会显示在这里。";
    private string _actionText = string.Empty;
    private string _rawHopsText = "-";
    private string _meaningfulSwitchesText = "-";
    private string _longestBlockText = "-";
    private string _topInterruptionText = "-";
    private string _activeSampleText = "-";
    private string _estimatedActiveText = "-";

    public InsightsViewModel(FocusInterruptionInsightService insightService)
        : this(insightService.GetInsightAsync)
    {
    }

    internal InsightsViewModel(Func<DateOnly, CancellationToken, Task<FocusInterruptionInsight>> loadInsightAsync)
    {
        _loadInsightAsync = loadInsightAsync;
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsLoading);
        PreviousDayCommand = new AsyncRelayCommand(LoadPreviousDayAsync, () => !IsLoading);
        TodayCommand = new AsyncRelayCommand(LoadTodayAsync, () => !IsLoading);
        NextDayCommand = new AsyncRelayCommand(LoadNextDayAsync, () => !IsLoading && CanMoveNextDay());
    }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand PreviousDayCommand { get; }

    public IAsyncRelayCommand TodayCommand { get; }

    public IAsyncRelayCommand NextDayCommand { get; }

    public DateTime? SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (SetProperty(ref _selectedDate, value))
            {
                SelectedDateText = GetSelectedDate().ToString("yyyy-MM-dd");
                NextDayCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SelectedDateText
    {
        get => _selectedDateText;
        private set => SetProperty(ref _selectedDateText, value);
    }

    // ── Stat cards ──

    public string RawHopsText
    {
        get => _rawHopsText;
        private set => SetProperty(ref _rawHopsText, value);
    }

    public string TaskSwitchesText
    {
        get => _meaningfulSwitchesText;
        private set => SetProperty(ref _meaningfulSwitchesText, value);
    }

    public string LongestBlockText
    {
        get => _longestBlockText;
        private set => SetProperty(ref _longestBlockText, value);
    }

    public string TopInterruptionText
    {
        get => _topInterruptionText;
        private set => SetProperty(ref _topInterruptionText, value);
    }

    public string ActiveSampleText
    {
        get => _activeSampleText;
        private set => SetProperty(ref _activeSampleText, value);
    }

    public string EstimatedActiveText
    {
        get => _estimatedActiveText;
        private set => SetProperty(ref _estimatedActiveText, value);
    }

    // ── Main content ──

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public string ActionText
    {
        get => _actionText;
        private set => SetProperty(ref _actionText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // ── Collections ──

    public ObservableCollection<WorkBlockInsight> WorkBlocks { get; } = [];

    public ObservableCollection<InterruptionSourceInsight> TopInterruptionSources { get; } = [];

    public ObservableCollection<ContextTransitionInsight> TopContextTransitions { get; } = [];

    // ── State ──

    public bool HasLoadError
    {
        get => _hasLoadError;
        private set => SetProperty(ref _hasLoadError, value);
    }

    public bool HasInsightData
    {
        get => _hasInsightData;
        private set => SetProperty(ref _hasInsightData, value);
    }

    public int InsightDataCount
    {
        get => _insightDataCount;
        private set => SetProperty(ref _insightDataCount, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
                PreviousDayCommand.NotifyCanExecuteChanged();
                TodayCommand.NotifyCanExecuteChanged();
                NextDayCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IsLoading = true;
            var selectedDate = GetSelectedDate();
            var insight = await _loadInsightAsync(selectedDate, cancellationToken);

            HasLoadError = false;
            ApplyInsight(insight);
        }
        catch (Exception ex)
        {
            HasLoadError = true;
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            StatusText = string.IsNullOrWhiteSpace(safeMessage)
                ? "Insights load failed."
                : $"Insights load failed: {safeMessage}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadPreviousDayAsync(CancellationToken cancellationToken = default)
    {
        SelectedDate = GetSelectedDate().ToDateTime(TimeOnly.MinValue).AddDays(-1);
        await LoadAsync(cancellationToken);
    }

    private async Task LoadTodayAsync(CancellationToken cancellationToken = default)
    {
        SelectedDate = DateTime.Today;
        await LoadAsync(cancellationToken);
    }

    private async Task LoadNextDayAsync(CancellationToken cancellationToken = default)
    {
        if (!CanMoveNextDay())
            return;

        SelectedDate = GetSelectedDate().ToDateTime(TimeOnly.MinValue).AddDays(1);
        await LoadAsync(cancellationToken);
    }

    private bool CanMoveNextDay() =>
        GetSelectedDate() < DateOnly.FromDateTime(DateTime.Today);

    private DateOnly GetSelectedDate() =>
        DateOnly.FromDateTime((SelectedDate ?? DateTime.Today).Date);

    private void ApplyInsight(FocusInterruptionInsight insight)
    {
        SelectedDate = insight.Date.ToDateTime(TimeOnly.MinValue);

        // Stat cards
        RawHopsText = insight.ActiveSampleCount > 0
            ? insight.RawToolHopCount.ToString("N0")
            : "-";
        TaskSwitchesText = insight.ActiveSampleCount > 0
            ? insight.MeaningfulContextSwitchCount.ToString("N0")
            : "-";
        LongestBlockText = insight.LongestWorkBlockText;
        TopInterruptionText = insight.TopInterruptionText;
        ActiveSampleText = insight.ActiveSampleCount.ToString("N0");

        EstimatedActiveText = insight.EstimatedActiveTime.TotalHours >= 1
            ? $"{(int)insight.EstimatedActiveTime.TotalHours}h {insight.EstimatedActiveTime.Minutes}m"
            : $"{insight.EstimatedActiveTime.Minutes}m";

        // Main content
        SummaryText = string.IsNullOrWhiteSpace(insight.SummaryText)
            ? "暂无今日洞察数据。Agent 运行并写入数据后会显示在这里。"
            : insight.SummaryText;
        ActionText = insight.ActionText;
        HasInsightData = insight.ActiveSampleCount > 0;
        InsightDataCount = HasInsightData ? 1 : 0;

        // Collections
        WorkBlocks.Clear();
        foreach (var wb in insight.WorkBlocks)
            WorkBlocks.Add(wb);

        TopInterruptionSources.Clear();
        foreach (var src in insight.TopInterruptionSources)
            TopInterruptionSources.Add(src);

        TopContextTransitions.Clear();
        foreach (var ctx in insight.TopContextTransitions)
            TopContextTransitions.Add(ctx);

        // Status
        StatusText = insight.ActiveSampleCount > 0
            ? $"Insights loaded for {SelectedDateText}. {insight.ActiveSampleCount:N0} active samples."
            : $"No active data for {SelectedDateText}.";
    }
}
