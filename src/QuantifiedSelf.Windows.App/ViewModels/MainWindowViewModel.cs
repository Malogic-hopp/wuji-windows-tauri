using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuantifiedSelf.Windows.ApplicationLayer.Models;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Core.Serialization;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public static IReadOnlyList<string> NavigationPages { get; } =
    [
        "Today",
        "Timeline",
        "Insights",
        "Trends",
        "Privacy",
        "Settings",
        "Diagnostics"
    ];

    private readonly AgentProcessService _processService;
    private readonly AgentControlService _controlService;
    private readonly AgentStatusService _statusService;
    private readonly RefreshService? _refreshService;
    private readonly OverviewDataService _overviewDataService;
    private readonly DiagnosticsDataService _diagnosticsDataService;
    private readonly AgentIpcStatusService? _ipcStatusService;
    private ITrayStateSink? _trayStateSink;
    private readonly SamplesViewModel _samplesViewModel;
    private readonly SessionsViewModel _sessionsViewModel;
    private readonly AppsViewModel _appsViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly SettingsService _settingsService;
    private readonly DashboardViewModel _dashboardViewModel;
    private readonly InsightsViewModel _insightsViewModel;
    private readonly TodayPageViewModel _todayPageViewModel;
    private readonly TimelinePageViewModel _timelinePageViewModel;
    private readonly TrendsPageViewModel _trendsPageViewModel;
    private readonly PrivacyPageViewModel _privacyPageViewModel;
    private readonly IRefreshScheduler _refreshScheduler;
    private readonly IRefreshScheduler _statusPollScheduler;
    private CancellationTokenSource? _statusPollCts;
    private long _latestAppliedStatusSequence;
    private AgentCommandAvailability _commandAvailability = AgentCommandAvailability.FromStatus(new AgentStatusSnapshot());
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _pageRefreshGate = new(1, 1);

    private AppSettings _appSettings = new();
    private bool _suppressPagePersistence;
    private bool _isInitialized;
    internal bool AutoStartAgentWasTriggered { get; set; }
    private readonly NavigationItemViewModel[] _primaryNavigationItems;
    private readonly NavigationItemViewModel[] _secondaryNavigationItems;

    private string _agentStatusText = "Not running";
    private string _lastHeartbeatText = "-";
    private string _lastSampleText = "-";
    private bool _isBusy;
    private bool _isMaintenance;
    private string _currentPage = "Today";
    private int _selectedTabIndex;
    private object? _currentPageContent;
    private string _currentPageTitle = "今天";
    private string _currentPageSubtitle = "回顾今天发生了什么，先看结论，再看证据。";
    private string _currentDateText = DateTime.Now.ToString("M月d日");
    private string _runtimeStateJson = "{}";
    private string _healthStateJson = "{}";
    private string _controlCommandJson = "{}";
    private string _agentProcessText = "-";
    private string _eventWriterStatusText = "SQLite writer: unknown";
    private string _journalWriterStatusText = "JSONL writer: unknown";
    private string _currentJournalPathText = "-";
    private string _ipcStatusText = "";
    private string _refreshHealthText = "";
    private string _lastEventWriteErrorText = "None";
    private string _lastJournalWriteErrorText = "None";
    private string _currentSessionIdText = "-";
    private string _statusMessage = "Ready";
    private IStartupRegistrationService? _startupRegistrationService;
    private string _lastTickPhaseText = "-";
    private string _lastTickDurationText = "-";
    private string _lastCaptureDurationText = "-";
    private string _lastPersistDurationText = "-";
    private string _lastMaintenanceDurationText = "-";
    private string _lastTickErrorText = "None";
    private string _loginStartupStatusText = "Unknown";
    private string _launchModeText = "Manual";
    private string _startupRegistrationSummary = "Unknown";
    private string _lastStartupRegistrationErrorText = "None";
    private StartupLaunchOptions _startupLaunchOptions = StartupLaunchOptions.Parse([]);

    public MainWindowViewModel(
        AgentProcessService processService,
        AgentControlService controlService,
        AgentStatusService statusService,
        OverviewDataService overviewDataService,
        DiagnosticsDataService diagnosticsDataService,
        SamplesViewModel samplesViewModel,
        SessionsViewModel sessionsViewModel,
        AppsViewModel appsViewModel,
        SettingsViewModel settingsViewModel,
        SettingsService settingsService,
        DashboardViewModel dashboardViewModel,
        InsightsViewModel insightsViewModel,
        AgentIpcStatusService? ipcStatusService = null,
        RefreshService? refreshService = null,
        ITrayStateSink? trayStateSink = null,
        IRefreshScheduler? refreshScheduler = null,
        IRefreshScheduler? statusPollScheduler = null)
    {
        _processService = processService;
        _controlService = controlService;
        _statusService = statusService;
        _overviewDataService = overviewDataService;
        _diagnosticsDataService = diagnosticsDataService;
        _ipcStatusService = ipcStatusService;
        _refreshService = refreshService;
        _trayStateSink = trayStateSink;
        _samplesViewModel = samplesViewModel;
        _sessionsViewModel = sessionsViewModel;
        _appsViewModel = appsViewModel;
        _settingsViewModel = settingsViewModel;
        _settingsService = settingsService;
        _dashboardViewModel = dashboardViewModel;
        _insightsViewModel = insightsViewModel;
        _todayPageViewModel = new TodayPageViewModel(_dashboardViewModel);
        _timelinePageViewModel = new TimelinePageViewModel(_appsViewModel, _sessionsViewModel, _samplesViewModel);
        _trendsPageViewModel = new TrendsPageViewModel(_dashboardViewModel);
        _privacyPageViewModel = new PrivacyPageViewModel(_settingsViewModel);
        _refreshScheduler = refreshScheduler ?? new DispatcherRefreshScheduler();
        _statusPollScheduler = statusPollScheduler ?? new DispatcherRefreshScheduler();
        _settingsViewModel.AppSettingsSaved += HandleAppSettingsSaved;

        _primaryNavigationItems =
        [
            new NavigationItemViewModel("Today", "今天", "查看今日活动、节奏和应用分布。"),
            new NavigationItemViewModel("Timeline", "时间线", "按应用、会话和原始记录回放。"),
            new NavigationItemViewModel("Insights", "洞察", "基于应用会话和活跃记录的可追溯分析。"),
            new NavigationItemViewModel("Trends", "趋势", "查看活跃趋势与活动热力图。")
        ];

        _secondaryNavigationItems =
        [
            new NavigationItemViewModel("Privacy", "数据与隐私", "本机存储、排除规则与保留周期。"),
            new NavigationItemViewModel("Settings", "设置", "采样、启动、通知和外观。")
        ];

        StartAgentCommand = new AsyncRelayCommand(StartAgentAsync, () => !IsBusy && _commandAvailability.CanStart);
        StopAgentCommand = new AsyncRelayCommand(StopAgentAsync, () => !IsBusy && _commandAvailability.CanStop);
        PauseCollectionCommand = new AsyncRelayCommand(PauseCollectionAsync, () => !IsBusy && _commandAvailability.CanPause);
        ResumeCollectionCommand = new AsyncRelayCommand(ResumeCollectionAsync, () => !IsBusy && _commandAvailability.CanResume);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        PrimaryAgentActionCommand = new AsyncRelayCommand(ExecutePrimaryAgentActionAsync, CanExecutePrimaryAgentAction);
        NavigateCommand = new RelayCommand<string?>(NavigateToPage);
        NavigateToTimelineCommand = new RelayCommand<HeatmapCellViewModel?>(NavigateToTimeline);
        OpenSettingsCommand = new RelayCommand(() => SelectedTabIndex = GetPageIndex("Settings"));
        OpenDiagnosticsCommand = new RelayCommand(() => SelectedTabIndex = GetPageIndex("Diagnostics"));
        UpdatePagePresentation();
    }

    public IAsyncRelayCommand StartAgentCommand { get; }

    public IAsyncRelayCommand StopAgentCommand { get; }

    public IAsyncRelayCommand PauseCollectionCommand { get; }

    public IAsyncRelayCommand ResumeCollectionCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand PrimaryAgentActionCommand { get; }

    public ICommand NavigateCommand { get; }

    public IRelayCommand<HeatmapCellViewModel?> NavigateToTimelineCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand OpenDiagnosticsCommand { get; }

    public IReadOnlyList<NavigationItemViewModel> PrimaryNavigationItems => _primaryNavigationItems;

    public IReadOnlyList<NavigationItemViewModel> SecondaryNavigationItems => _secondaryNavigationItems;

    public ObservableCollection<string> Messages { get; } = new();

    public ObservableCollection<AgentEvent> RecentEvents { get; } = new();

    public ObservableCollection<AgentEvent> RecentErrors { get; } = new();

    public SamplesViewModel SamplesViewModel => _samplesViewModel;

    public SessionsViewModel SessionsViewModel => _sessionsViewModel;

    public AppsViewModel AppsViewModel => _appsViewModel;

    public SettingsViewModel SettingsViewModel => _settingsViewModel;

    public DashboardViewModel DashboardViewModel => _dashboardViewModel;

    public InsightsViewModel InsightsViewModel => _insightsViewModel;

    public TodayPageViewModel TodayPageViewModel => _todayPageViewModel;

    public TimelinePageViewModel TimelinePageViewModel => _timelinePageViewModel;

    public TrendsPageViewModel TrendsPageViewModel => _trendsPageViewModel;

    public PrivacyPageViewModel PrivacyPageViewModel => _privacyPageViewModel;

    internal ITrayStateSink? TrayStateSink
    {
        get => _trayStateSink;
        set => _trayStateSink = value;
    }

    internal IStartupRegistrationService? StartupRegistrationService
    {
        get => _startupRegistrationService;
        set => _startupRegistrationService = value;
    }

    /// <summary>
    /// Sets the startup launch options parsed during App startup.
    /// Also updates LaunchModeText so Diagnostics displays the correct launch mode.
    /// </summary>
    internal StartupLaunchOptions StartupLaunchOptions
    {
        get => _startupLaunchOptions;
        set
        {
            _startupLaunchOptions = value;
            LaunchModeText = value.Mode switch
            {
                LaunchMode.AutoStart => "AutoStart",
                _ => "Manual"
            };
        }
    }

    public string AgentStatusText
    {
        get => _agentStatusText;
        private set => SetProperty(ref _agentStatusText, value);
    }

    private string _agentStatusDotBrushKey = "AccentBrush";
    public string AgentStatusDotBrushKey
    {
        get => _agentStatusDotBrushKey;
        private set => SetProperty(ref _agentStatusDotBrushKey, value);
    }

    public string LastHeartbeatText
    {
        get => _lastHeartbeatText;
        private set => SetProperty(ref _lastHeartbeatText, value);
    }

    public string LastSampleText
    {
        get => _lastSampleText;
        private set => SetProperty(ref _lastSampleText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                (StartAgentCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                (StopAgentCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                (PauseCollectionCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                (ResumeCollectionCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                (RefreshCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                (PrimaryAgentActionCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(PrimaryAgentActionText));
            }
        }
    }

    public string CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                UpdatePagePresentation();
            }
        }
    }

    public string CurrentPageTitle
    {
        get => _currentPageTitle;
        private set => SetProperty(ref _currentPageTitle, value);
    }

    public string CurrentPageSubtitle
    {
        get => _currentPageSubtitle;
        private set => SetProperty(ref _currentPageSubtitle, value);
    }

    public string CurrentDateText
    {
        get => _currentDateText;
        private set => SetProperty(ref _currentDateText, value);
    }

    public object? CurrentPageContent
    {
        get => _currentPageContent;
        private set => SetProperty(ref _currentPageContent, value);
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
            {
                CurrentPage = GetPageForIndex(value);

                if (!_suppressPagePersistence)
                {
                    _appSettings.LastSelectedPage = CurrentPage;
                    _ = PersistAppSettingsAsync();
                }
            }
        }
    }

    public string RuntimeStateJson
    {
        get => _runtimeStateJson;
        private set => SetProperty(ref _runtimeStateJson, value);
    }

    public string HealthStateJson
    {
        get => _healthStateJson;
        private set => SetProperty(ref _healthStateJson, value);
    }

    public string ControlCommandJson
    {
        get => _controlCommandJson;
        private set => SetProperty(ref _controlCommandJson, value);
    }

    public string AgentProcessText
    {
        get => _agentProcessText;
        private set => SetProperty(ref _agentProcessText, value);
    }

    public string EventWriterStatusText
    {
        get => _eventWriterStatusText;
        private set => SetProperty(ref _eventWriterStatusText, value);
    }

    public string JournalWriterStatusText
    {
        get => _journalWriterStatusText;
        private set => SetProperty(ref _journalWriterStatusText, value);
    }

    public string CurrentJournalPathText
    {
        get => _currentJournalPathText;
        private set => SetProperty(ref _currentJournalPathText, value);
    }

    public string IpcStatusText
    {
        get => _ipcStatusText;
        private set => SetProperty(ref _ipcStatusText, value);
    }

    public string LastTickPhaseText
    {
        get => _lastTickPhaseText;
        private set => SetProperty(ref _lastTickPhaseText, value);
    }

    public string LastTickDurationText
    {
        get => _lastTickDurationText;
        private set => SetProperty(ref _lastTickDurationText, value);
    }

    public string LastCaptureDurationText
    {
        get => _lastCaptureDurationText;
        private set => SetProperty(ref _lastCaptureDurationText, value);
    }

    public string LastPersistDurationText
    {
        get => _lastPersistDurationText;
        private set => SetProperty(ref _lastPersistDurationText, value);
    }

    public string LastMaintenanceDurationText
    {
        get => _lastMaintenanceDurationText;
        private set => SetProperty(ref _lastMaintenanceDurationText, value);
    }

    public string LastTickErrorText
    {
        get => _lastTickErrorText;
        private set => SetProperty(ref _lastTickErrorText, value);
    }

    public string RefreshHealthText
    {
        get => _refreshHealthText;
        private set => SetProperty(ref _refreshHealthText, value);
    }

    public string LastEventWriteErrorText
    {
        get => _lastEventWriteErrorText;
        private set => SetProperty(ref _lastEventWriteErrorText, value);
    }

    public string LastJournalWriteErrorText
    {
        get => _lastJournalWriteErrorText;
        private set => SetProperty(ref _lastJournalWriteErrorText, value);
    }

    public string CurrentSessionIdText
    {
        get => _currentSessionIdText;
        private set => SetProperty(ref _currentSessionIdText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string PrimaryAgentActionText => GetPrimaryAgentActionText(GetPrimaryAgentActionKind());

    /// <summary>
    /// Diagnostics display: current login startup status.
    /// Values: "Enabled", "Disabled", "Mismatch", "Error", "Unavailable", or "Unknown".
    /// Only refreshed on Diagnostics page refresh, not on 2-second status polling.
    /// </summary>
    public string LoginStartupStatusText
    {
        get => _loginStartupStatusText;
        private set => SetProperty(ref _loginStartupStatusText, value);
    }

    /// <summary>
    /// Diagnostics display: launch mode. Values: "Manual" or "AutoStart".
    /// Set once at App startup from parsed StartupLaunchOptions, never re-evaluated.
    /// </summary>
    public string LaunchModeText
    {
        get => _launchModeText;
        private set => SetProperty(ref _launchModeText, value);
    }

    /// <summary>
    /// Diagnostics display: safe human-readable summary of startup registration.
    /// Examples: "Registered to current app", "Not registered",
    /// "Registered command needs repair", "Registration unavailable",
    /// "Registration unavailable in current launch mode".
    /// Never contains full paths, SIDs, or raw registry exception text.
    /// </summary>
    public string StartupRegistrationSummary
    {
        get => _startupRegistrationSummary;
        private set => SetProperty(ref _startupRegistrationSummary, value);
    }

    /// <summary>
    /// Diagnostics display: safe short error text for the last startup registration error.
    /// "None" if no error. Never contains full paths, SIDs, or raw registry exception text.
    /// </summary>
    public string LastStartupRegistrationErrorText
    {
        get => _lastStartupRegistrationErrorText;
        private set => SetProperty(ref _lastStartupRegistrationErrorText, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Prevent double initialization (e.g. explicit call during hidden startup
        // plus Loaded event firing when the window is later shown via tray).
        if (_isInitialized) return;
        _isInitialized = true;

        await _settingsViewModel.LoadAsync(cancellationToken);
        ApplyAppSettings(_settingsViewModel.AppSettings);

        _suppressPagePersistence = true;
        SelectedTabIndex = GetPageIndex(_appSettings.LastSelectedPage);
        _suppressPagePersistence = false;

        _refreshScheduler.Start(TimeSpan.FromSeconds(Math.Max(5, _appSettings.RefreshIntervalSeconds)), _ => RefreshAsync());
        _statusPollScheduler.Start(TimeSpan.FromSeconds(2), _ => RefreshStatusOnlyAsync());

        UpdatePagePresentation();
        await RefreshAsync(cancellationToken);

        // Auto-start Agent if configured and allowed — same guard as tray/manual commands.
        if (_appSettings.AutoStartAgentWhenAppStarts
            && StartAgentCommand.CanExecute(null))
        {
            AutoStartAgentWasTriggered = true;
            _ = StartAgentAsync();
        }
    }

    /// <summary>
    /// Refreshes startup registration status from the registration service.
    /// Only called from RefreshDiagnosticsAsync (full page refresh), never from 2-second status polling.
    /// All display text is pre-sanitized and safe for UI — no paths, SIDs, or raw exception text.
    /// </summary>
    internal async Task RefreshStartupRegistrationAsync()
    {
        if (_startupRegistrationService is null)
        {
            LoginStartupStatusText = "Unknown";
            StartupRegistrationSummary = "Startup registration service not available";
            LastStartupRegistrationErrorText = "None";
            return;
        }

        try
        {
            var status = await _startupRegistrationService.GetStatusAsync();
            var display = StartupRegistrationDisplayModel.FromStatus(status, _startupLaunchOptions.Mode);
            LoginStartupStatusText = display.LoginStartupStatusText;
            StartupRegistrationSummary = display.StartupRegistrationSummary;
            LastStartupRegistrationErrorText = display.LastStartupRegistrationErrorText;
        }
        catch
        {
            LoginStartupStatusText = "Error";
            StartupRegistrationSummary = "Registration unavailable";
            LastStartupRegistrationErrorText = "Unable to read startup registration status";
        }
    }

    internal void StopStatusPolling()
    {
        _statusPollScheduler.Stop();
        _statusPollCts?.Cancel();
        _statusPollCts?.Dispose();
        _statusPollCts = null;
    }

    private async Task RefreshStatusOnlyAsync()
    {
        if (_refreshService is null) return;
        await PerformStatusPollAsync();
    }

    /// <summary>
    /// Internal hook for tests to trigger a single status poll cycle without a real timer.
    /// </summary>
    internal async Task PerformStatusPollAsync()
    {
        if (_refreshService is null) return;

        // Cancel any in-flight status poll (latest-wins)
        _statusPollCts?.Cancel();
        _statusPollCts?.Dispose();
        _statusPollCts = new CancellationTokenSource();

        try
        {
            var currentPage = GetPageForIndex(SelectedTabIndex);
            var result = await _refreshService.RefreshStatusAsync(currentPage, _statusPollCts.Token);

            if (result.PageRefreshSkipped) return;

            ApplyStatusRefreshResult(result);
        }
        catch (OperationCanceledException)
        {
            // Expected when superseded by a newer poll
        }
        catch (Exception ex)
        {
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            StatusMessage = string.IsNullOrWhiteSpace(safeMessage)
                ? "Refresh failed."
                : $"Refresh failed: {safeMessage}";
            Messages.Clear();
            Messages.Add(StatusMessage);
        }
    }

    internal void ApplyStatusRefreshResult(RefreshResult result)
    {
        // Latest-wins: ignore result if a newer one has already been applied
        if (result.RefreshSequence <= _latestAppliedStatusSequence) return;
        _latestAppliedStatusSequence = result.RefreshSequence;

        // Only apply status if no status error occurred (page errors don't block status)
        if (string.IsNullOrWhiteSpace(result.Health.LastStatusRefreshError))
        {
            RefreshCommonStatus(result.Status, result.ProcessInfo);
        }
        // Don't refresh page data — status polling is status-only

        // Update refresh health display if user is on Diagnostics
        if (GetPageForIndex(SelectedTabIndex) == "Diagnostics")
        {
            UpdateRefreshHealthPresentation();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_refreshService is not null)
        {
            await RefreshWithServiceAsync(cancellationToken);
        }
        else
        {
            await RefreshLegacyAsync(cancellationToken);
        }
    }

    private async Task RefreshWithServiceAsync(CancellationToken cancellationToken)
    {
        // Phase 1: fetch + apply status under outer gate
        AgentStatusSnapshot? statusToApply = null;
        AgentProcessInfo? processToApply = null;
        bool hasStatusError = false;
        string? statusError = null;

        if (await _refreshGate.WaitAsync(0, cancellationToken))
        {
            try
            {
                var currentPage = GetPageForIndex(SelectedTabIndex);
                var result = await _refreshService!.RefreshAsync(currentPage, cancellationToken);

                if (!result.PageRefreshSkipped)
                {
                    statusToApply = result.Status;
                    processToApply = result.ProcessInfo;
                    statusError = result.Health.LastStatusRefreshError;
                    hasStatusError = !string.IsNullOrWhiteSpace(statusError);

                    if (!hasStatusError)
                    {
                        if (result.RefreshSequence > _latestAppliedStatusSequence)
                        {
                            _latestAppliedStatusSequence = result.RefreshSequence;
                            RefreshCommonStatus(result.Status, result.ProcessInfo);
                        }
                    }
                    else
                    {
                        StatusMessage = statusError!.StartsWith("Refresh failed", StringComparison.OrdinalIgnoreCase)
                            ? statusError
                            : $"Refresh failed: {statusError}";
                        Messages.Clear();
                        Messages.Add(StatusMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
                StatusMessage = string.IsNullOrWhiteSpace(safeMessage) ? "Refresh failed." : $"Refresh failed: {safeMessage}";
                Messages.Clear(); Messages.Add(StatusMessage);
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        // Phase 2: page refresh under _pageRefreshGate (outside outer gate)
        if (statusToApply is not null && !hasStatusError)
        {
            await RefreshCurrentPageDataWithGateAsync(statusToApply, cancellationToken);
        }
    }

    private async Task RefreshLegacyAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsBusy = true;

            var status = await _statusService.GetStatusAsync(cancellationToken);
            var processInfo = await _processService.GetAgentProcessInfoAsync(cancellationToken);

            RefreshCommonStatus(status, processInfo);
            await RefreshCurrentPageDataWithGateAsync(status, cancellationToken);
        }
        catch (Exception ex)
        {
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            StatusMessage = string.IsNullOrWhiteSpace(safeMessage)
                ? "Refresh failed."
                : $"Refresh failed: {safeMessage}";
            Messages.Clear();
            Messages.Add(StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshCurrentPageDataWithGateAsync(
        AgentStatusSnapshot status,
        CancellationToken cancellationToken)
    {
        if (!await _pageRefreshGate.WaitAsync(0, cancellationToken))
        {
            _refreshService?.Health.RecordPageSkipped();
            return;
        }

        try
        {
            if (_refreshService is not null) _refreshService.Health.IsPageRefreshing = true;
            await RefreshCurrentPageDataAsync(status, cancellationToken);
            _refreshService?.Health.RecordPageSuccess(GetPageForIndex(SelectedTabIndex));
            UpdateRefreshHealthPresentation();
        }
        catch (Exception ex)
        {
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            if (string.IsNullOrWhiteSpace(safeMessage)) safeMessage = "Page refresh failed.";
            _refreshService?.Health.RecordPageError(safeMessage);
            UpdateRefreshHealthPresentation();
        }
        finally
        {
            _pageRefreshGate.Release();
        }
    }

    private async Task RefreshCurrentPageDataAsync(
        AgentStatusSnapshot status,
        CancellationToken cancellationToken)
    {
        CurrentPage = GetPageForIndex(SelectedTabIndex);

        switch (CurrentPage)
        {
            case "Today":
                await RefreshDashboardAsync(status, cancellationToken);
                break;
            case "Timeline":
                await _timelinePageViewModel.LoadAsync(cancellationToken);
                break;
            case "Insights":
                await _insightsViewModel.LoadAsync(cancellationToken);
                break;
            case "Trends":
                await RefreshDashboardAsync(status, cancellationToken);
                break;
            case "Privacy":
                await _privacyPageViewModel.RefreshAsync(cancellationToken);
                ApplyAppSettings(_settingsViewModel.AppSettings);
                break;
            case "Settings":
                if (!_settingsViewModel.IsDirty)
                {
                    await _settingsViewModel.LoadAsync(cancellationToken);
                    ApplyAppSettings(_settingsViewModel.AppSettings);
                }
                break;
            case "Diagnostics":
                await RefreshDiagnosticsAsync(status, cancellationToken);
                break;
            default:
                break;
        }
    }

    private async Task RefreshDashboardAsync(
        AgentStatusSnapshot status,
        CancellationToken cancellationToken)
    {
        await _dashboardViewModel.LoadAsync(cancellationToken);
        RefreshMessages(status);
    }

    private async Task RefreshDiagnosticsAsync(
        AgentStatusSnapshot status,
        CancellationToken cancellationToken)
    {
        var recentEvents = await _diagnosticsDataService.GetRecentEventsAsync(20, cancellationToken);
        var recentErrors = await _diagnosticsDataService.GetRecentErrorsAsync(10, cancellationToken);

        CurrentJournalPathText = _diagnosticsDataService.GetCurrentJournalPath(DateTime.UtcNow);
        EventWriterStatusText = (status.HealthState?.EventWriteErrorCount ?? 0) > 0
            ? $"SQLite writer degraded ({status.HealthState?.EventWriteErrorCount ?? 0} errors)"
            : "SQLite writer healthy";
        JournalWriterStatusText = (status.HealthState?.JournalWriteErrorCount ?? 0) > 0
            ? $"JSONL writer degraded ({status.HealthState?.JournalWriteErrorCount ?? 0} errors)"
            : "JSONL writer healthy";
        LastEventWriteErrorText = status.HealthState?.LastEventWriteError is null
            ? "None"
            : $"{status.HealthState?.LastEventWriteErrorUtc:yyyy-MM-dd HH:mm:ss} UTC - {status.HealthState?.LastEventWriteError}";
        LastJournalWriteErrorText = status.HealthState?.LastJournalWriteError is null
            ? "None"
            : $"{status.HealthState?.LastJournalWriteErrorUtc:yyyy-MM-dd HH:mm:ss} UTC - {status.HealthState?.LastJournalWriteError}";
        CurrentSessionIdText = status.HealthState?.CurrentSessionId?.ToString() ?? "-";

        // Tick-level diagnostics
        LastTickPhaseText = status.HealthState?.LastTickPhase ?? "-";
        LastTickDurationText = status.HealthState?.LastTickDurationMs is { } d
            ? $"{d:F0} ms"
            : "-";
        LastCaptureDurationText = status.HealthState?.LastCaptureDurationMs is { } cd
            ? $"{cd:F0} ms"
            : "-";
        LastPersistDurationText = status.HealthState?.LastPersistDurationMs is { } pd
            ? $"{pd:F0} ms"
            : "-";
        LastMaintenanceDurationText = status.HealthState?.LastMaintenanceDurationMs is { } md
            ? $"{md:F0} ms"
            : "-";
        LastTickErrorText = status.HealthState?.LastErrorCode is null
            ? "None"
            : $"[{status.HealthState.LastErrorCode}] {status.HealthState.LastErrorMessage ?? "-"}";

        ReplaceCollection(RecentEvents, recentEvents);
        ReplaceCollection(RecentErrors, recentErrors);

        // IPC status — safe display, no FullPipeName/SID/paths/raw exceptions
        if (_ipcStatusService is not null)
        {
            var statusText = _ipcStatusService.GetDisplayStatusText();
            var pipe = _ipcStatusService.DisplayPipeName ?? "Current user pipe";
            var success = _ipcStatusService.LastIpcSuccessUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
            var error = _ipcStatusService.LastIpcError is not null
                ? DiagnosticMessageSanitizer.CreateSafeText(_ipcStatusService.LastIpcError, 120)
                : "None";
            var fallback = _ipcStatusService.LastFallbackUsedUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
            var source = _ipcStatusService.LastCommandSource;

            IpcStatusText = $"IPC: {statusText}  |  Pipe: {pipe}  |  Source: {source}  |  Last success: {success}  |  Last error: {error}  |  Last fallback: {fallback}";
        }
        else
        {
            IpcStatusText = "IPC: not configured.";
        }

        UpdateRefreshHealthPresentation();

        // Refresh startup registration status (only on Diagnostics page refresh, not 2s polling)
        await RefreshStartupRegistrationAsync();
    }

    internal void UpdateRefreshHealthPresentation()
    {
        if (_refreshService is null)
        {
            RefreshHealthText = "Refresh loop: not configured.";
            return;
        }

        var h = _refreshService.Health;

        // Determine loop status
        var loopStatus = "Ready";
        if (h.IsStatusRefreshing || h.IsPageRefreshing)
            loopStatus = "Refreshing";
        else if (!string.IsNullOrWhiteSpace(h.LastStatusRefreshError) || !string.IsNullOrWhiteSpace(h.LastPageRefreshError))
            loopStatus = "Degraded";
        else if (h.LastStatusRefreshSuccessUtc is not null || h.LastPageRefreshSuccessUtc is not null)
            loopStatus = "Healthy";

        var statusSuccess = h.LastStatusRefreshSuccessUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        var statusError = string.IsNullOrWhiteSpace(h.LastStatusRefreshError)
            ? "None"
            : DiagnosticMessageSanitizer.CreateSafeText(h.LastStatusRefreshError, 120);
        var statusSkipped = h.SkippedStatusRefreshCount;

        var pageSuccess = h.LastPageRefreshSuccessUtc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
        var pageError = string.IsNullOrWhiteSpace(h.LastPageRefreshError)
            ? "None"
            : DiagnosticMessageSanitizer.CreateSafeText(h.LastPageRefreshError, 120);
        var pageSkipped = h.SkippedPageRefreshCount;
        var pageLast = h.LastPageRefreshPage ?? "-";

        var statusInterval = "2s";
        var pageInterval = $"{_appSettings.RefreshIntervalSeconds}s";

        RefreshHealthText = $"Refresh loop: {loopStatus}  |  "
            + $"Status: last={statusSuccess}, error={statusError}, skipped={statusSkipped}  |  "
            + $"Page: last={pageSuccess}, error={pageError}, skipped={pageSkipped}, page={pageLast}  |  "
            + $"Intervals: status polling {statusInterval}, page refresh {pageInterval}";
    }

    private void RefreshCommonStatus(
        AgentStatusSnapshot status,
        AgentProcessInfo? processInfo)
    {
        _isMaintenance = status.ActualState == AgentActualState.Maintenance;

        // Compute shared availability — only notify if it actually changed
        var newAvailability = AgentCommandAvailability.FromStatus(status);
        var availabilityChanged =
            newAvailability.CanStart != _commandAvailability.CanStart ||
            newAvailability.CanStop != _commandAvailability.CanStop ||
            newAvailability.CanPause != _commandAvailability.CanPause ||
            newAvailability.CanResume != _commandAvailability.CanResume;

        _commandAvailability = newAvailability;

        if (availabilityChanged)
        {
            ((AsyncRelayCommand)StartAgentCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)StopAgentCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)PauseCollectionCommand).NotifyCanExecuteChanged();
            ((AsyncRelayCommand)ResumeCollectionCommand).NotifyCanExecuteChanged();
            (PrimaryAgentActionCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(PrimaryAgentActionText));
        }

        // Dispatch same status to Settings so buttons stay in sync
        _settingsViewModel.UpdateAgentStatus(status);

        // Sync tray tooltip and menu availability
        if (_trayStateSink is not null)
        {
            var trayState = TrayMenuState.From(status, _commandAvailability,
                _ipcStatusService?.LastCommandSource);
            _trayStateSink.UpdateState(trayState);
        }

        AgentStatusText = GetAgentStatusBadgeText(status);
        AgentStatusDotBrushKey = GetAgentStatusDotBrushKey(status.ActualState);
        LastHeartbeatText = status.LastHeartbeatText;
        LastSampleText = status.LastSampleText;
        AgentProcessText = processInfo is null
            ? "Agent process not detected"
            : processInfo.StartedAtUtc.HasValue
                ? $"PID {processInfo.ProcessId}, started {processInfo.StartedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                : $"PID {processInfo.ProcessId}, started unknown";
        RuntimeStateJson = Serialize(status.RuntimeState);
        HealthStateJson = Serialize(status.HealthState);
        ControlCommandJson = string.IsNullOrWhiteSpace(status.CurrentControlCommandText)
            ? "{}"
            : status.CurrentControlCommandText;
        StatusMessage = GetAgentStatusDetailText(status);
    }

    private void RefreshMessages(AgentStatusSnapshot status)
    {
        Messages.Clear();
        if (status.IsStale)
        {
            Messages.Add("Agent heartbeat is stale.");
        }
        else if (status.ActualState == AgentActualState.Maintenance)
        {
            Messages.Add("Agent is performing maintenance. Control commands are temporarily unavailable.");
        }
        else if (status.ActualState == AgentActualState.Stopped)
        {
            Messages.Add("Agent stopped.");
        }
        else if (!status.IsRunning)
        {
            Messages.Add("Agent is not running.");
        }
        else
        {
            Messages.Add("Agent is alive and updating runtime_state.json.");
        }
    }

    private void HandleAppSettingsSaved(AppSettings settings)
    {
        ApplyAppSettings(settings);
    }

    private void ApplyAppSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _appSettings = CloneAppSettings(settings);
        _refreshScheduler.UpdateInterval(TimeSpan.FromSeconds(Math.Max(5, settings.RefreshIntervalSeconds)));
    }

    private static AppSettings CloneAppSettings(AppSettings settings)
    {
        return new AppSettings
        {
            AutoStartAgentWhenAppStarts = settings.AutoStartAgentWhenAppStarts,
            StartAppOnWindowsLogin = settings.StartAppOnWindowsLogin,
            MinimizeToTray = settings.MinimizeToTray,
            CloseToTray = settings.CloseToTray,
            RefreshIntervalSeconds = settings.RefreshIntervalSeconds,
            Theme = settings.Theme,
            LastSelectedPage = settings.LastSelectedPage
        };
    }

    private async Task StartAgentAsync()
    {
        try
        {
            IsBusy = true;
            await _processService.StartAgentAsync();
            await RefreshAsync();
            Messages.Add("Agent started.");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            Messages.Add(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopAgentAsync()
    {
        try
        {
            IsBusy = true;
            var stopped = await _processService.StopAgentGracefullyAsync();
            if (!stopped)
            {
                await _processService.KillAgentAsFallbackAsync();
            }

            await RefreshAsync();
            Messages.Add(stopped ? "Stop requested." : "Stop fallback used.");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            Messages.Add(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PauseCollectionAsync()
    {
        await SendControlCommandAsync(() => _controlService.RequestPauseAsync());
    }

    private async Task ResumeCollectionAsync()
    {
        await SendControlCommandAsync(() => _controlService.RequestResumeAsync());
    }

    private async Task SendControlCommandAsync(Func<Task<QuantifiedSelf.Windows.Core.Control.AgentCommandResult>> request)
    {
        try
        {
            IsBusy = true;
            var result = await request();
            await RefreshAsync();
            Messages.Add($"{result.ActualState}: {result.Message}");
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            Messages.Add(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Serialize<T>(T? value)
    {
        return value is null ? "{}" : JsonSerializer.Serialize(value, JsonSerializationOptions.Default);
    }

    private static string FormatDuration(int totalSeconds)
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

    private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items)
        {
            collection.Add(item);
        }
    }

    private static string GetPageForIndex(int index)
    {
        return index >= 0 && index < NavigationPages.Count
            ? NavigationPages[index]
            : "Today";
    }

    private static int GetPageIndex(string? page)
    {
        if (string.IsNullOrWhiteSpace(page))
        {
            return 0;
        }

        page = NormalizePageKey(page);
        for (var index = 0; index < NavigationPages.Count; index++)
        {
            if (string.Equals(NavigationPages[index], page, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private static string NormalizePageKey(string page)
    {
        return page.Trim() switch
        {
            "Dashboard" => "Today",
            "Apps" or "Sessions" or "Samples" => "Timeline",
            "DataPrivacy" or "Data & Privacy" => "Privacy",
            _ => page.Trim()
        };
    }

    private bool CanExecutePrimaryAgentAction()
    {
        return !IsBusy;
    }

    private async Task ExecutePrimaryAgentActionAsync()
    {
        switch (GetPrimaryAgentActionKind())
        {
            case PrimaryAgentActionKind.Start:
                await StartAgentAsync();
                break;
            case PrimaryAgentActionKind.Resume:
                await ResumeCollectionAsync();
                break;
            case PrimaryAgentActionKind.Pause:
                await PauseCollectionAsync();
                break;
            case PrimaryAgentActionKind.Stop:
                await StopAgentAsync();
                break;
            default:
                await RefreshAsync();
                break;
        }
    }

    private PrimaryAgentActionKind GetPrimaryAgentActionKind()
    {
        if (_commandAvailability.CanStart)
        {
            return PrimaryAgentActionKind.Start;
        }

        if (_commandAvailability.CanResume)
        {
            return PrimaryAgentActionKind.Resume;
        }

        if (_commandAvailability.CanPause)
        {
            return PrimaryAgentActionKind.Pause;
        }

        if (_commandAvailability.CanStop)
        {
            return PrimaryAgentActionKind.Stop;
        }

        return PrimaryAgentActionKind.Refresh;
    }

    private static string GetPrimaryAgentActionText(PrimaryAgentActionKind kind)
    {
        return kind switch
        {
            PrimaryAgentActionKind.Start => "开始记录",
            PrimaryAgentActionKind.Resume => "恢复记录",
            PrimaryAgentActionKind.Pause => "暂停记录",
            PrimaryAgentActionKind.Stop => "停止服务",
            _ => "刷新状态"
        };
    }

    private static string GetAgentStatusBadgeText(AgentStatusSnapshot status)
    {
        return status.ActualState switch
        {
            AgentActualState.NotRunning or AgentActualState.Stopped => "未运行",
            AgentActualState.Starting => "正在启动",
            AgentActualState.Running => "正在记录",
            AgentActualState.Pausing => "正在暂停",
            AgentActualState.Paused => "已暂停",
            AgentActualState.Resuming => "正在恢复",
            AgentActualState.Stopping => "正在停止",
            AgentActualState.Stale => "记录超时",
            AgentActualState.Error => "状态异常",
            AgentActualState.Maintenance => "维护中",
            _ => status.StatusText
        };
    }

    private static string GetAgentStatusDotBrushKey(AgentActualState state)
    {
        return state switch
        {
            AgentActualState.Running => "AccentBrush",
            AgentActualState.Starting => "AccentBrush",
            AgentActualState.Resuming => "AccentBrush",
            AgentActualState.Paused => "WarningBrush",
            AgentActualState.Pausing => "WarningBrush",
            AgentActualState.Stopping => "WarningBrush",
            AgentActualState.Maintenance => "WarningBrush",
            AgentActualState.Stale => "WarningBrush",
            AgentActualState.Error => "DangerBrush",
            AgentActualState.NotRunning or AgentActualState.Stopped => "IdleBrush",
            _ => "IdleBrush"
        };
    }

    private static string GetAgentStatusDetailText(AgentStatusSnapshot status)
    {
        if (status.IsStale)
        {
            return "最近心跳已超时，建议查看运行诊断。";
        }

        return status.ActualState switch
        {
            AgentActualState.NotRunning or AgentActualState.Stopped => "后台记录未运行。",
            AgentActualState.Starting => "后台记录正在启动。",
            AgentActualState.Running => "后台正在记录活动。",
            AgentActualState.Pausing => "正在暂停记录。",
            AgentActualState.Paused => "记录已暂停。",
            AgentActualState.Resuming => "正在恢复记录。",
            AgentActualState.Stopping => "正在停止后台服务。",
            AgentActualState.Error => "后台状态异常，建议查看运行诊断。",
            AgentActualState.Maintenance => "Agent 正在执行维护任务。",
            _ => status.StatusText
        };
    }

    private enum PrimaryAgentActionKind
    {
        Start,
        Resume,
        Pause,
        Stop,
        Refresh
    }

    private void NavigateToPage(string? pageKey)
    {
        var index = GetPageIndex(pageKey);
        if (SelectedTabIndex == index)
        {
            UpdatePagePresentation();
            return;
        }

        SelectedTabIndex = index;
    }

    private void NavigateToTimeline(HeatmapCellViewModel? cell)
    {
        if (cell is null)
        {
            return;
        }

        _timelinePageViewModel.NavigateTo(cell.Date, cell.Hour);
        SelectedTabIndex = GetPageIndex("Timeline");
    }

    private void UpdatePagePresentation()
    {
        var page = CurrentPage;
        CurrentPageContent = page switch
        {
            "Today" => _dashboardViewModel,
            "Timeline" => _timelinePageViewModel,
            "Insights" => _insightsViewModel,
            "Trends" => _trendsPageViewModel,
            "Privacy" => _privacyPageViewModel,
            "Settings" => _settingsViewModel,
            "Diagnostics" => this,
            _ => _dashboardViewModel
        };

        CurrentDateText = DateTime.Now.ToString("M月d日");

        (var title, var subtitle) = page switch
        {
            "Today" => ("今天", "先看结论，再回到证据和时间线。"),
            "Timeline" => ("时间线", "按应用、会话与原始采样回放一天。"),
            "Insights" => ("洞察", "基于应用会话和活跃记录的可追溯分析。"),
            "Trends" => ("趋势", "观察最近 7 天、30 天和 12 周的变化。"),
            "Privacy" => ("数据与隐私", "本机存储、排除规则和保留周期都在这里。"),
            "Settings" => ("设置", "采样、启动、通知与外观配置。"),
            "Diagnostics" => ("运行诊断", "查看状态文件、健康检查和最近事件。"),
            _ => ("WUJI", "查看今天的时间分配与工作节奏。")
        };

        CurrentPageTitle = title;
        CurrentPageSubtitle = subtitle;
        UpdateNavigationSelection(page);
    }

    private void UpdateNavigationSelection(string page)
    {
        foreach (var item in _primaryNavigationItems)
        {
            item.IsSelected = string.Equals(item.Key, page, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var item in _secondaryNavigationItems)
        {
            item.IsSelected = string.Equals(item.Key, page, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task PersistAppSettingsAsync()
    {
        try
        {
            await _settingsService.SaveAppSettingsAsync(_appSettings);
        }
        catch
        {
            // Best effort persistence for tab selection and UI preferences.
        }
    }
}
