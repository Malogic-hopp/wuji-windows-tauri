using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Activity;
using QuantifiedSelf.Windows.ApplicationLayer.Settings;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.App.ViewModels;
using QuantifiedSelf.Windows.Client.Agent;
using QuantifiedSelf.Windows.Client.Settings;
using QuantifiedSelf.Windows.Client.Startup;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.Database;
using QuantifiedSelf.Windows.Infrastructure.Ipc;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using QuantifiedSelf.Windows.Infrastructure.Settings;

namespace QuantifiedSelf.Windows.App;

public partial class App : Application
{
    private bool _isExitRequested;
    private StartupLaunchOptions _startupLaunchOptions = StartupLaunchOptions.Parse([]);

    internal StartupLaunchOptions StartupLaunchOptions => _startupLaunchOptions;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _startupLaunchOptions = StartupLaunchOptions.Parse(e.Args);
        var runtimeChannel = RuntimeChannel.Parse(_startupLaunchOptions.ChannelName);

        var paths = new WindowsAgentPaths(channelName: runtimeChannel.Name);
        paths.EnsureDirectories();
        var runtimeStateStore = new RuntimeStateStore();
        var healthStateStore = new AgentHealthStateStore();
        var controlFileStore = new AgentControlFileStore();
        var appSettingsStore = new AppSettingsStore();
        var agentOptionsStore = new WindowsAgentOptionsStore();
        var settingsStore = new WindowsSettingsStoreAdapter(
            paths, appSettingsStore, agentOptionsStore);
        var settingsService = new SettingsService(settingsStore, settingsStore);

        AppSettings appSettings;
        try
        {
            appSettings = await settingsService.ReadAppSettingsAsync();
        }
        catch
        {
            appSettings = new AppSettings();
        }

        if (_startupLaunchOptions.UsePreviewUi)
        {
            ThemeService.ApplyTheme(ThemeService.Parse(appSettings.Theme));
        }

        // IPC setup
        var userSid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        IAgentTransport? ipcClient = null;
        var ipcStatusService = new AgentTransportHealthService();

        try
        {
            var pipeName = new AgentPipeName(userSid, runtimeChannel.Name);
            ipcStatusService.Initialize(pipeName);
            var ipcOptions = new AgentIpcClientOptions();
            ipcClient = new NamedPipeAgentControlClient(pipeName, ipcOptions);
        }
        catch
        {
            ipcStatusService.RecordIpcFallback("IPC unavailable; using file fallback.");
        }

        var agentState = new FileAgentStateAdapter(
            paths, runtimeStateStore, healthStateStore, controlFileStore, agentOptionsStore);
        var processController = new WindowsAgentProcessController(
            paths,
            runtimeStateStore,
            NullLogger<WindowsAgentProcessController>.Instance,
            showAgentConsole: _startupLaunchOptions.ShowAgentConsole,
            channelName: runtimeChannel.Name);
        var statusService = new AgentStatusService(
            agentState, agentState, agentState, agentState, processController,
            ipcClient, ipcStatusService);
        var processService = new AgentProcessService(
            processController, agentState, agentState, ipcClient);
        var controlService = new AgentControlService(
            agentState, statusService, ipcClient, ipcStatusService);
        var activityQueries = new SqliteActivityQueryAdapter(paths);
        var overviewDataService = new OverviewDataService(activityQueries, activityQueries);
        var diagnosticsDataService = new DiagnosticsDataService(activityQueries);
        var samplesDataService = new SamplesDataService(activityQueries);
        var sessionsDataService = new SessionsDataService(activityQueries);
        var appsDataService = new AppsDataService(activityQueries);
        var dailyStatsService = new DailyStatsService(activityQueries, activityQueries);
        var weeklyTrendService = new WeeklyTrendService(dailyStatsService);
        var heatmapService = new HourActivityHeatmapService(activityQueries);
        var samplesViewModel = new SamplesViewModel(samplesDataService);
        var sessionsViewModel = new SessionsViewModel(sessionsDataService);
        var appsViewModel = new AppsViewModel(appsDataService);
        var dashboardViewModel = new DashboardViewModel(dailyStatsService, weeklyTrendService, heatmapService);
        var insightService = new FocusInterruptionInsightService(activityQueries);
        var insightsViewModel = new InsightsViewModel(insightService);
        Resources.Add("BooleanToVisibilityConverter", new BooleanToVisibilityConverter());

        var refreshService = new RefreshService(statusService, processService);
        var startupRegistry = new RegistryStartupRegistry();
        var startupCommandBuilder = new StartupCommandBuilder(channelName: runtimeChannel.Name);
        var startupRegistrationService = new StartupRegistrationService(
            startupRegistry,
            startupCommandBuilder,
            runtimeChannel.StartupRegistryValueName);
        var settingsViewModel = new SettingsViewModel(settingsService, statusService, controlService, diagnosticsDataService, paths);
        settingsViewModel.StartupRegistrationService = startupRegistrationService;
        var viewModel = new MainWindowViewModel(
            processService,
            controlService,
            statusService,
            overviewDataService,
            diagnosticsDataService,
            samplesViewModel,
            sessionsViewModel,
            appsViewModel,
            settingsViewModel,
            settingsService,
            dashboardViewModel,
            insightsViewModel,
            ipcStatusService,
            refreshService,
            trayStateSink: null,
            refreshScheduler: new DispatcherRefreshScheduler(),
            statusPollScheduler: new DispatcherRefreshScheduler()); // set via TrayStateSink after tray creation

        // Inject startup registration service and launch options for Diagnostics display
        viewModel.StartupRegistrationService = startupRegistrationService;
        viewModel.StartupLaunchOptions = _startupLaunchOptions;

        Window window = _startupLaunchOptions.UsePreviewUi
            ? new MainWindow(viewModel)
            : new LegacyMainWindow(viewModel);
        if (!runtimeChannel.IsDefault)
        {
            window.Title = $"{runtimeChannel.ProductDisplayName} - {window.Title}";
        }
        MainWindow = window;

        // Tray icon setup — create before window.Closed so it can be disposed there
        var trayAdapter = new NotifyIconTrayIconAdapter();
        TrayService? trayService = null;

        // Save settings reference for lifecycle events to check CloseToTray / MinimizeToTray at runtime
        var closeToTray = appSettings.CloseToTray;
        var minimizeToTray = appSettings.MinimizeToTray;
        settingsViewModel.AppSettingsSaved += (settings) =>
        {
            closeToTray = settings.CloseToTray;
            minimizeToTray = settings.MinimizeToTray;
        };

        trayService = new TrayService(
            trayAdapter,
            Dispatcher.CurrentDispatcher,
            showMainWindow: () =>
            {
                window.Show();
                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;
                window.Activate();
            },
            exitApp: () =>
            {
                _isExitRequested = true;
                trayService?.Dispose();
                Shutdown();
            },
            startAgent: () =>
            {
                if (viewModel.StartAgentCommand.CanExecute(null))
                    viewModel.StartAgentCommand.Execute(null);
            },
            pauseAgent: () =>
            {
                if (viewModel.PauseCollectionCommand.CanExecute(null))
                    viewModel.PauseCollectionCommand.Execute(null);
            },
            resumeAgent: () =>
            {
                if (viewModel.ResumeCollectionCommand.CanExecute(null))
                    viewModel.ResumeCollectionCommand.Execute(null);
            },
            stopAgent: () =>
            {
                if (viewModel.StopAgentCommand.CanExecute(null))
                    viewModel.StopAgentCommand.Execute(null);
            });

        // CloseToTray: intercept close, hide instead of exit
        window.Closing += (_, args) =>
        {
            if (!_isExitRequested && closeToTray)
            {
                args.Cancel = true;
                window.Hide();
            }
        };

        // MinimizeToTray: hide on minimize
        window.StateChanged += (_, _) =>
        {
            if (!_isExitRequested && minimizeToTray && window.WindowState == WindowState.Minimized && window.IsVisible)
            {
                window.Hide();
            }
        };

        window.Closed += (_, _) =>
        {
            viewModel.StopStatusPolling();
            trayService?.Dispose();
        };
        viewModel.TrayStateSink = trayService;

        // Decide whether to show the main window based on startup launch mode.
        // AutoStart-hidden (--from-autostart --start-hidden): start services,
        // create tray, but skip window.Show() so the user isn't interrupted.
        var startupPolicy = WindowStartupPolicy.Decide(_startupLaunchOptions);
        if (startupPolicy.ShouldShowMainWindowOnLaunch)
        {
            window.Show();
        }
        else
        {
            // Window.Loaded won't fire, so explicitly initialize (starts status polling, timers, etc.)
            _ = viewModel.InitializeAsync();
        }
    }

}
