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
        "Insights",
        "Settings"
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
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _statusPollTimer;
    private CancellationTokenSource? _statusPollCts;
    private long _latestAppliedStatusSequence;
    private AgentCommandAvailability _commandAvailability = AgentCommandAvailability.FromStatus(new AgentStatusSnapshot());
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _pageRefreshGate = new(1, 1);

    private AppSettings _appSettings = new();
    private bool _suppressPagePersistence;
    private bool _isInitialized;
    internal bool AutoStartAgentWasTriggered { get; set; }

    private string _agentStatusText = "Not running";
    private string _lastHeartbeatText = "-";
    private string _lastSampleText = "-";
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
        ITrayStateSink? trayStateSink = null)
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

    public ObservableCollection<AgentEvent> RecentEvents { get; } = new();

    public ObservableCollection<AgentEvent> RecentErrors { get; } = new();

    public SamplesViewModel SamplesViewModel => _samplesViewModel;

    public SessionsViewModel SessionsViewModel => _sessionsViewModel;

    public AppsViewModel AppsViewModel => _appsViewModel;

    public SettingsViewModel SettingsViewModel => _settingsViewModel;

    public DashboardViewModel DashboardViewModel => _dashboardViewModel;

    public InsightsViewModel InsightsViewModel => _insightsViewModel;

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

        _refreshTimer.Start();
        _statusPollTimer.Start();

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
            case "Insights":
                await _insightsViewModel.LoadAsync(cancellationToken);
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
