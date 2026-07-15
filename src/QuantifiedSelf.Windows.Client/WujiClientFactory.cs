using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Activity;
using QuantifiedSelf.Windows.ApplicationLayer.Settings;
using QuantifiedSelf.Windows.Client.Agent;
using QuantifiedSelf.Windows.Client.Settings;
using QuantifiedSelf.Windows.Client.Startup;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.Database;
using QuantifiedSelf.Windows.Infrastructure.Ipc;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using QuantifiedSelf.Windows.Infrastructure.Settings;

namespace QuantifiedSelf.Windows.Client;

public static class WujiClientFactory
{
    public static IWujiClient Create(WujiClientOptions? options = null)
    {
        options ??= new WujiClientOptions();

        var runtimeChannel = RuntimeChannel.Parse(options.ChannelName);
        var paths = new WindowsAgentPaths(options.DataRootPath, runtimeChannel.Name);
        var runtimeStateStore = new RuntimeStateStore();
        var healthStateStore = new AgentHealthStateStore();
        var controlFileStore = new AgentControlFileStore();
        var appSettingsStore = new AppSettingsStore();
        var agentOptionsStore = new WindowsAgentOptionsStore();

        var settingsStore = new WindowsSettingsStoreAdapter(
            paths, appSettingsStore, agentOptionsStore);
        var settingsService = new SettingsService(settingsStore, settingsStore);

        var transportHealth = new AgentTransportHealthService();
        IAgentTransport? transport = null;

        try
        {
            var userIdentity = options.UserIdentity
                ?? WindowsIdentity.GetCurrent().User?.Value
                ?? Environment.UserName;
            var pipeName = new AgentPipeName(userIdentity, runtimeChannel.Name);
            transportHealth.Initialize(pipeName);
            transport = new NamedPipeAgentControlClient(pipeName, new AgentIpcClientOptions());
        }
        catch
        {
            transportHealth.RecordIpcFallback("IPC unavailable; using file fallback.");
        }

        var agentState = new FileAgentStateAdapter(
            paths, runtimeStateStore, healthStateStore, controlFileStore, agentOptionsStore);
        var processController = new WindowsAgentProcessController(
            paths,
            runtimeStateStore,
            NullLogger<WindowsAgentProcessController>.Instance,
            options.ShowAgentConsole,
            runtimeChannel.Name);
        var statusService = new AgentStatusService(
            agentState,
            agentState,
            agentState,
            agentState,
            processController,
            transport,
            transportHealth);
        var processService = new AgentProcessService(
            processController, agentState, agentState, transport);
        var controlService = new AgentControlService(
            agentState, statusService, transport, transportHealth);

        var activityQueries = new SqliteActivityQueryAdapter(paths);
        var overview = new OverviewDataService(activityQueries, activityQueries);
        var diagnostics = new DiagnosticsDataService(activityQueries);
        var samples = new SamplesDataService(activityQueries);
        var sessions = new SessionsDataService(activityQueries);
        var apps = new AppsDataService(activityQueries);
        var dailyStats = new DailyStatsService(activityQueries, activityQueries);
        var weeklyTrend = new WeeklyTrendService(dailyStats);
        var heatmap = new HourActivityHeatmapService(activityQueries);
        var insights = new FocusInterruptionInsightService(activityQueries);

        var startupRegistry = new RegistryStartupRegistry();
        var commandBuilder = options.ProcessPathProvider is null
            ? new StartupCommandBuilder(runtimeChannel.Name)
            : new StartupCommandBuilder(options.ProcessPathProvider, runtimeChannel.Name);
        var startupRegistration = new StartupRegistrationService(
            startupRegistry,
            commandBuilder,
            runtimeChannel.StartupRegistryValueName);

        return new WujiClient(
            paths,
            new AgentClient(statusService, controlService, processService, transportHealth),
            new ActivityClient(
                overview,
                samples,
                sessions,
                apps,
                dailyStats,
                weeklyTrend,
                heatmap,
                insights),
            new DiagnosticsClient(diagnostics),
            new SettingsClient(settingsService),
            new StartupClient(startupRegistration, options.LaunchOptions),
            new WujiClientContext(
                runtimeChannel.Name,
                runtimeChannel.ProductDisplayName,
                runtimeChannel.IsDefault));
    }
}
