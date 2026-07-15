using QuantifiedSelf.Windows.ApplicationLayer.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Activity;
using QuantifiedSelf.Windows.ApplicationLayer.Settings;
using QuantifiedSelf.Windows.Client.Startup;

namespace QuantifiedSelf.Windows.Client;

public interface IWujiClient : IAsyncDisposable
{
    IAgentClient Agent { get; }

    IActivityClient Activity { get; }

    IDiagnosticsClient Diagnostics { get; }

    ISettingsClient Settings { get; }

    IStartupClient Startup { get; }

    WujiClientContext Context { get; }

    WujiClientPaths Paths { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IAgentClient
{
    IAgentStatusService Status { get; }

    IAgentControlService Control { get; }

    IAgentProcessService Process { get; }

    IAgentTransportHealthService TransportHealth { get; }
}

public interface IActivityClient
{
    IOverviewDataService Overview { get; }

    ISamplesDataService Samples { get; }

    ISessionsDataService Sessions { get; }

    IAppsDataService Apps { get; }

    IDailyStatsService DailyStats { get; }

    IWeeklyTrendService WeeklyTrend { get; }

    IHourActivityHeatmapService Heatmap { get; }

    IFocusInterruptionInsightService Insights { get; }
}

public interface IDiagnosticsClient : IDiagnosticsDataService;

public interface ISettingsClient : ISettingsService
{
    Task<Core.Options.AppSettings> ReadAppSettingsOrDefaultAsync(
        CancellationToken cancellationToken = default);
}

public interface IStartupClient : IStartupRegistrationService
{
    StartupLaunchOptions LaunchOptions { get; }
}
