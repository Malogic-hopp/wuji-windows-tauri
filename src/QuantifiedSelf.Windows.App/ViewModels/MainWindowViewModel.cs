using System.Diagnostics;
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
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Core.Serialization;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AgentProcessService _processService;
    private readonly AgentControlService _controlService;
    private readonly AgentStatusService _statusService;
    private readonly OverviewDataService _overviewDataService;
    private readonly SettingsService _settingsService;
    private readonly WindowsAgentPaths _paths;
    private readonly DispatcherTimer _refreshTimer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private AppSettings _appSettings = new();
    private WindowsAgentOptions _agentOptions = new();
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
    private string _currentPage = "Dashboard";
    private int _selectedTabIndex;
    private string _runtimeStateJson = "{}";
    private string _healthStateJson = "{}";
    private string _controlCommandJson = "{}";
    private string _appSettingsJson = "{}";
    private string _agentOptionsJson = "{}";
    private string _agentProcessText = "-";
    private string _statusMessage = "Ready";
    private string _dataRootText = "-";

    public MainWindowViewModel(
        AgentProcessService processService,
        AgentControlService controlService,
        AgentStatusService statusService,
        OverviewDataService overviewDataService,
        SettingsService settingsService,
        WindowsAgentPaths paths)
    {
        _processService = processService;
        _controlService = controlService;
        _statusService = statusService;
        _overviewDataService = overviewDataService;
        _settingsService = settingsService;
        _paths = paths;

        StartAgentCommand = new AsyncRelayCommand(StartAgentAsync, () => !IsBusy);
        StopAgentCommand = new AsyncRelayCommand(StopAgentAsync, () => !IsBusy);
        PauseCollectionCommand = new AsyncRelayCommand(PauseCollectionAsync, () => !IsBusy);
        ResumeCollectionCommand = new AsyncRelayCommand(ResumeCollectionAsync, () => !IsBusy);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        OpenSettingsCommand = new RelayCommand(() => SelectedTabIndex = 2);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
    }

    public IAsyncRelayCommand StartAgentCommand { get; }

    public IAsyncRelayCommand StopAgentCommand { get; }

    public IAsyncRelayCommand PauseCollectionCommand { get; }

    public IAsyncRelayCommand ResumeCollectionCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public ObservableCollection<string> Messages { get; } = new();

    public ObservableCollection<AppUsageSummary> TopApps { get; } = new();

    public ObservableCollection<AppSession> RecentSessions { get; } = new();

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
                CurrentPage = value switch
                {
                    0 => "Dashboard",
                    1 => "Diagnostics",
                    2 => "Settings",
                    _ => "Dashboard"
                };

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

    public string AppSettingsJson
    {
        get => _appSettingsJson;
        private set => SetProperty(ref _appSettingsJson, value);
    }

    public string AgentOptionsJson
    {
        get => _agentOptionsJson;
        private set => SetProperty(ref _agentOptionsJson, value);
    }

    public string AgentProcessText
    {
        get => _agentProcessText;
        private set => SetProperty(ref _agentProcessText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string DataRootText
    {
        get => _dataRootText;
        private set => SetProperty(ref _dataRootText, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _appSettings = await _settingsService.ReadAppSettingsAsync(cancellationToken);
        _agentOptions = await _settingsService.ReadAgentOptionsAsync(cancellationToken);

        _suppressPagePersistence = true;
        SelectedTabIndex = _appSettings.LastSelectedPage switch
        {
            "Diagnostics" => 1,
            "Settings" => 2,
            _ => 0
        };
        _suppressPagePersistence = false;

        _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(5, _appSettings.RefreshIntervalSeconds));
        _refreshTimer.Start();

        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            IsBusy = true;

            var status = await _statusService.GetStatusAsync(cancellationToken);
            var processInfo = await _processService.GetAgentProcessInfoAsync(cancellationToken);
            var appSettings = await _settingsService.ReadAppSettingsAsync(cancellationToken);
            var agentOptions = await _settingsService.ReadAgentOptionsAsync(cancellationToken);
            var currentCommand = await _controlService.ReadCurrentCommandAsync(cancellationToken);
            var dashboardSummary = await _overviewDataService.GetDashboardSummaryAsync(cancellationToken);
            var topApps = await _overviewDataService.GetTopAppsTodayAsync(5, cancellationToken);
            var recentSessions = await _overviewDataService.GetRecentSessionsAsync(5, cancellationToken);

            _appSettings = appSettings;
            _agentOptions = agentOptions;

            DataRootText = _paths.Root;
            AgentStatusText = status.StatusText;
            LastHeartbeatText = status.LastHeartbeatText;
            LastSampleText = status.LastSampleText;
            TodayTotalText = FormatDuration(dashboardSummary.TotalDurationSeconds);
            TodayActiveText = FormatDuration(dashboardSummary.ActiveDurationSeconds);
            TodayIdleText = FormatDuration(dashboardSummary.IdleDurationSeconds);
            TodayUnknownText = FormatDuration(dashboardSummary.UnknownDurationSeconds);
            TodaySessionCountText = dashboardSummary.SessionCount.ToString();
            AgentProcessText = processInfo is null
                ? "Agent process not detected"
                : processInfo.StartedAtUtc.HasValue
                    ? $"PID {processInfo.ProcessId}, started {processInfo.StartedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
                    : $"PID {processInfo.ProcessId}, started unknown";

            RuntimeStateJson = Serialize(status.RuntimeState);
            HealthStateJson = Serialize(status.HealthState);
            ControlCommandJson = currentCommand is null ? "{}" : Serialize(currentCommand);
            AppSettingsJson = Serialize(appSettings);
            AgentOptionsJson = Serialize(agentOptions);
            StatusMessage = status.IsStale ? "Agent heartbeat is stale" : status.StatusText;
            CurrentPage = SelectedTabIndex switch
            {
                0 => "Dashboard",
                1 => "Diagnostics",
                2 => "Settings",
                _ => "Dashboard"
            };

            ReplaceCollection(TopApps, topApps);
            ReplaceCollection(RecentSessions, recentSessions);
            Messages.Clear();
            if (status.IsStale)
            {
                Messages.Add("Agent heartbeat is stale.");
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
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            Messages.Clear();
            Messages.Add(ex.Message);
        }
        finally
        {
            IsBusy = false;
            _refreshGate.Release();
        }
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

    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = _paths.Root,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            Messages.Add(ex.Message);
        }
    }
}
