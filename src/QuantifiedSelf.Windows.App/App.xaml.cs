using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.App.ViewModels;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.Ipc;
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

        // IPC setup
        var userSid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        IAgentIpcClient? ipcClient = null;
        var ipcStatusService = new AgentIpcStatusService();

        try
        {
            var pipeName = new AgentPipeName(userSid);
            ipcStatusService.Initialize(pipeName);
            var ipcOptions = new AgentIpcClientOptions();
            ipcClient = new NamedPipeAgentControlClient(pipeName, ipcOptions);
        }
        catch
        {
            ipcStatusService.RecordIpcFallback("IPC unavailable; using file fallback.");
        }

        var settingsService = new SettingsService(paths, appSettingsStore, agentOptionsStore);
        var statusService = new AgentStatusService(
            paths, runtimeStateStore, healthStateStore, controlFileStore, agentOptionsStore,
            ipcClient, ipcStatusService);
        var processService = new AgentProcessService(paths, runtimeStateStore, controlFileStore, NullLogger<AgentProcessService>.Instance, ipcClient);
        var controlService = new AgentControlService(paths, controlFileStore, statusService, ipcClient, ipcStatusService);
        var overviewDataService = new OverviewDataService(paths);
        var diagnosticsDataService = new DiagnosticsDataService(paths);
        var samplesDataService = new SamplesDataService(paths);
        var sessionsDataService = new SessionsDataService(paths);
        var appsDataService = new AppsDataService(paths);
        var samplesViewModel = new SamplesViewModel(samplesDataService);
        var sessionsViewModel = new SessionsViewModel(sessionsDataService);
        var appsViewModel = new AppsViewModel(appsDataService);
        Resources.Add("BooleanToVisibilityConverter", new BooleanToVisibilityConverter());

        var refreshService = new RefreshService(statusService, processService);
        var settingsViewModel = new SettingsViewModel(settingsService, statusService, controlService, diagnosticsDataService, paths);
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
            ipcStatusService,
            refreshService);

        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Closed += (_, _) => viewModel.StopStatusPolling();
        window.Show();
    }

}
