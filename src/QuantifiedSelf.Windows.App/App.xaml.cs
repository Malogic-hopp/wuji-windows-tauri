using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.App.ViewModels;
using QuantifiedSelf.Windows.Client;
using QuantifiedSelf.Windows.Client.Startup;

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
        var client = WujiClientFactory.Create(
            WujiClientOptions.FromLaunchOptions(_startupLaunchOptions));
        await client.InitializeAsync();

        var appSettings = await client.Settings.ReadAppSettingsOrDefaultAsync();

        if (_startupLaunchOptions.UsePreviewUi)
        {
            ThemeService.ApplyTheme(ThemeService.Parse(appSettings.Theme));
        }

        var samplesViewModel = new SamplesViewModel(client.Activity);
        var sessionsViewModel = new SessionsViewModel(client.Activity);
        var appsViewModel = new AppsViewModel(client.Activity);
        var dashboardViewModel = new DashboardViewModel(client.Activity);
        var insightsViewModel = new InsightsViewModel(client.Activity);
        Resources.Add("BooleanToVisibilityConverter", new BooleanToVisibilityConverter());

        var refreshService = new RefreshService(client.Agent);
        var settingsViewModel = new SettingsViewModel(
            client.Settings,
            client.Agent,
            client.Diagnostics,
            client.Paths,
            client.Startup);
        var viewModel = new MainWindowViewModel(
            client.Agent,
            client.Activity,
            client.Diagnostics,
            samplesViewModel,
            sessionsViewModel,
            appsViewModel,
            settingsViewModel,
            client.Settings,
            dashboardViewModel,
            insightsViewModel,
            client.Startup,
            refreshService,
            trayStateSink: null,
            refreshScheduler: new DispatcherRefreshScheduler(),
            statusPollScheduler: new DispatcherRefreshScheduler());

        Window window = _startupLaunchOptions.UsePreviewUi
            ? new MainWindow(viewModel)
            : new LegacyMainWindow(viewModel);
        if (!client.Context.IsDefaultChannel)
        {
            window.Title = $"{client.Context.ProductDisplayName} - {window.Title}";
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

        window.Closed += async (_, _) =>
        {
            viewModel.StopStatusPolling();
            trayService?.Dispose();
            await client.DisposeAsync();
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
