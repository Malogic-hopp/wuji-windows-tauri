using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuantifiedSelf.Windows.App.Models;
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
        "Dashboard",
        "Apps",
        "Sessions",
        "Samples",
        "Diagnostics",
        "Settings"
    ];

    private readonly AgentProcessService _processService;
    private readonly AgentControlService _controlService;
    private readonly AgentStatusService _statusService;
    private readonly RefreshService? _refreshService;
    private readonly OverviewDataService _overviewDataService;
    private readonly DiagnosticsDataService _diagnosticsDataService;
    private readonly AgentIpcStatusService? _ipcStatusService;
    private readonly SamplesViewModel _samplesViewModel;
    private readonly SessionsViewModel _sessionsViewModel;
    private readonly AppsViewModel _appsViewModel;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly SettingsService _settingsService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _statusPollTimer;
    private CancellationTokenSource? _statusPollCts;
    private long _latestAppliedStatusSequence;
    private AgentCommandAvailability _commandAvailability = AgentCommandAvailability.FromStatus(new AgentStatusSnapshot());
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _pageRefreshGate = new(1, 1);

    private AppSettings _appSettings = new();
    private bool _suppressPagePersistence;

    private string _agentStatusText = "Not running";
    private string _lastHeartbeatText = "-";
    private string _lastSampleText = "-";
    private string _todayTotalText = "-";
    private string _todayActiveText = "-";
    private string _todayIdleText = "-";
    private string _todayUnknownText = "-";
    private string _todaySessionCountText = "-";
    private bool _isBusy;
    private bool _isMaintenance;
    private string _currentPage = "Dashboard";
    private int _selectedTabIndex;
    private string _runtimeStateJson = "{}";
    private string _healthStateJson = "{}";
    private string _controlCommandJson = "{}";
    private string _agentProcessText = "-";
    private string _eventWriterStatusText = "SQLite writer: unknown";
    private string _journalWriterStatusText = "JSONL writer: unknown";
    private string _currentJournalPathText = "-";
    private string _ipcStatusText = "";
    private string _lastEventWriteErrorText = "None";
    private string _lastJournalWriteErrorText = "None";
    private string _currentSessionIdText = "-";
    private string _statusMessage = "Ready";

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
        AgentIpcStatusService? ipcStatusService = null,
        RefreshService? refreshService = null)
    {
        _processService = processService;
        _controlService = controlService;
        _statusService = statusService;
        _overviewDataService = overviewDataService;
        _diagnosticsDataService = diagnosticsDataService;
        _ipcStatusService = ipcStatusService;
        _refreshService = refreshService;
        _samplesViewModel = samplesViewModel;
        _sessionsViewModel = sessionsViewModel;
        _appsViewModel = appsViewModel;
        _settingsViewModel = settingsViewModel;
        _settingsService = settingsService;
        _settingsViewModel.AppSettingsSaved += HandleAppSettingsSaved;

        StartAgentCommand = new AsyncRelayCommand(StartAgentAsync, () => !IsBusy && _commandAvailability.CanStart);
        StopAgentCommand = new AsyncRelayCommand(StopAgentAsync, () => !IsBusy && _commandAvailability.CanStop);
        PauseCollectionCommand = new AsyncRelayCommand(PauseCollectionAsync, () => !IsBusy && _commandAvailability.CanPause);
        ResumeCollectionCommand = new AsyncRelayCommand(ResumeCollectionAsync, () => !IsBusy && _commandAvailability.CanResume);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        OpenSettingsCommand = new RelayCommand(() => SelectedTabIndex = GetPageIndex("Settings"));

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();

        _statusPollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _statusPollTimer.Tick += async (_, _) => await RefreshStatusOnlyAsync();
    }

    public IAsyncRelayCommand StartAgentCommand { get; }

    public IAsyncRelayCommand StopAgentCommand { get; }

    public IAsyncRelayCommand PauseCollectionCommand { get; }

    public IAsyncRelayCommand ResumeCollectionCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ObservableCollection<string> Messages { get; } = new();

    public ObservableCollection<AppUsageSummary> TopApps { get; } = new();

    public ObservableCollection<AppSession> RecentSessions { get; } = new();

    public ObservableCollection<AgentEvent> RecentEvents { get; } = new();

    public ObservableCollection<AgentEvent> RecentErrors { get; } = new();

    public SamplesViewModel SamplesViewModel => _samplesViewModel;

    public SessionsViewModel SessionsViewModel => _sessionsViewModel;

    public AppsViewModel AppsViewModel => _appsViewModel;

    public SettingsViewModel SettingsViewModel => _settingsViewModel;

    public string AgentStatusText
    {
        get => _agentStatusText;
        private set => SetProperty(ref _agentStatusText, value);
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

    public string TodayTotalText
    {
        get => _todayTotalText;
        private set => SetProperty(ref _todayTotalText, value);
    }

    public string TodayActiveText
    {
        get => _todayActiveText;
        private set => SetProperty(ref _todayActiveText, value);
    }

    public string TodayIdleText
    {
        get => _todayIdleText;
        private set => SetProperty(ref _todayIdleText, value);
    }

    public string TodayUnknownText
    {
        get => _todayUnknownText;
        private set => SetProperty(ref _todayUnknownText, value);
    }

    public string TodaySessionCountText
    {
        get => _todaySessionCountText;
        private set => SetProperty(ref _todaySessionCountText, value);
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
            }
        }
    }

    public string CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
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

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _settingsViewModel.LoadAsync(cancellationToken);
        ApplyAppSettings(_settingsViewModel.AppSettings);

        _suppressPagePersistence = true;
        SelectedTabIndex = GetPageIndex(_appSettings.LastSelectedPage);
        _suppressPagePersistence = false;

        _refreshTimer.Start();
        _statusPollTimer.Start();

        await RefreshAsync(cancellationToken);
    }

    internal void StopStatusPolling()
    {
        _statusPollTimer.Stop();
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

    private void ApplyStatusRefreshResult(RefreshResult result)
    {
        // Latest-wins: ignore result if a newer one has already been applied
        if (result.RefreshSequence <= _latestAppliedStatusSequence) return;
        _latestAppliedStatusSequence = result.RefreshSequence;

        // Only apply status if no error occurred
        if (string.IsNullOrWhiteSpace(result.Health.LastRefreshError))
        {
            RefreshCommonStatus(result.Status, result.ProcessInfo);
        }
        // Don't refresh page data — status polling is status-only
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
        }
        catch (Exception ex)
        {
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            if (string.IsNullOrWhiteSpace(safeMessage)) safeMessage = "Page refresh failed.";
            _refreshService?.Health.RecordPageError(safeMessage);
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
            case "Dashboard":
                await RefreshDashboardAsync(status, cancellationToken);
                break;
            case "Apps":
                await AppsViewModel.LoadAsync(cancellationToken);
                break;
            case "Sessions":
                await SessionsViewModel.LoadAsync(cancellationToken);
                break;
            case "Samples":
                await SamplesViewModel.LoadAsync(cancellationToken);
                break;
            case "Diagnostics":
                await RefreshDiagnosticsAsync(status, cancellationToken);
                break;
            case "Settings":
                if (!_settingsViewModel.IsDirty)
                {
                    await _settingsViewModel.LoadAsync(cancellationToken);
                    ApplyAppSettings(_settingsViewModel.AppSettings);
                }
                break;
        }
    }

    private async Task RefreshDashboardAsync(
        AgentStatusSnapshot status,
        CancellationToken cancellationToken)
    {
        var dashboardSummary = await _overviewDataService.GetDashboardSummaryAsync(cancellationToken);
        var topApps = await _overviewDataService.GetTopAppsTodayAsync(5, cancellationToken);
        var recentSessions = await _overviewDataService.GetRecentSessionsAsync(5, cancellationToken);

        TodayTotalText = FormatDuration(dashboardSummary.TotalDurationSeconds);
        TodayActiveText = FormatDuration(dashboardSummary.ActiveDurationSeconds);
        TodayIdleText = FormatDuration(dashboardSummary.IdleDurationSeconds);
        TodayUnknownText = FormatDuration(dashboardSummary.UnknownDurationSeconds);
        TodaySessionCountText = dashboardSummary.SessionCount.ToString();

        ReplaceCollection(TopApps, topApps);
        ReplaceCollection(RecentSessions, recentSessions);
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
    }

    private void RefreshCommonStatus(
        AgentStatusSnapshot status,
        AgentProcessInfo? processInfo)
    {
        _isMaintenance = status.ActualState == AgentActualState.Maintenance;

        // Compute shared availability and notify all control commands
        _commandAvailability = AgentCommandAvailability.FromStatus(status);
        ((AsyncRelayCommand)StartAgentCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)StopAgentCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)PauseCollectionCommand).NotifyCanExecuteChanged();
        ((AsyncRelayCommand)ResumeCollectionCommand).NotifyCanExecuteChanged();

        // Dispatch same status to Settings so buttons stay in sync
        _settingsViewModel.UpdateAgentStatus(status);

        AgentStatusText = status.StatusText;
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
        StatusMessage = status.IsStale ? "Agent heartbeat is stale" : status.StatusText;
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
        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(5, settings.RefreshIntervalSeconds));
    }

    private static AppSettings CloneAppSettings(AppSettings settings)
    {
        return new AppSettings
        {
            AutoStartAgentWhenAppStarts = settings.AutoStartAgentWhenAppStarts,
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
            : "Dashboard";
    }

    private static int GetPageIndex(string? page)
    {
        if (string.IsNullOrWhiteSpace(page))
        {
            return 0;
        }

        for (var index = 0; index < NavigationPages.Count; index++)
        {
            if (string.Equals(NavigationPages[index], page, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
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
