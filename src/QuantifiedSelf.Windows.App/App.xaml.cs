using System.Windows;
using Microsoft.Extensions.Logging.Abstractions;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.App.ViewModels;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using QuantifiedSelf.Windows.Infrastructure.Settings;

namespace QuantifiedSelf.Windows.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = new WindowsAgentPaths();
        paths.EnsureDirectories();
        var runtimeStateStore = new RuntimeStateStore();
        var healthStateStore = new AgentHealthStateStore();
        var controlFileStore = new AgentControlFileStore();
        var appSettingsStore = new AppSettingsStore();
        var agentOptionsStore = new WindowsAgentOptionsStore();

        var settingsService = new SettingsService(paths, appSettingsStore, agentOptionsStore);
        var statusService = new AgentStatusService(paths, runtimeStateStore, healthStateStore, controlFileStore, agentOptionsStore);
        var processService = new AgentProcessService(paths, runtimeStateStore, controlFileStore, NullLogger<AgentProcessService>.Instance);
        var controlService = new AgentControlService(paths, controlFileStore, statusService);
        var overviewDataService = new OverviewDataService(paths);
        var diagnosticsDataService = new DiagnosticsDataService(paths);
        var samplesDataService = new SamplesDataService(paths);
        var sessionsDataService = new SessionsDataService(paths);
        var appsDataService = new AppsDataService(paths);
        var samplesViewModel = new SamplesViewModel(samplesDataService);
        var sessionsViewModel = new SessionsViewModel(sessionsDataService);
        var appsViewModel = new AppsViewModel(appsDataService);
        var viewModel = new MainWindowViewModel(
            processService,
            controlService,
            statusService,
            overviewDataService,
            diagnosticsDataService,
            samplesViewModel,
            sessionsViewModel,
            appsViewModel,
            settingsService,
            paths);

        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
    }

}
