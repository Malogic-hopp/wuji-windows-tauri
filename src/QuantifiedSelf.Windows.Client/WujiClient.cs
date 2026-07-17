using QuantifiedSelf.Windows.ApplicationLayer.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Activity;
using QuantifiedSelf.Windows.ApplicationLayer.Settings;
using QuantifiedSelf.Windows.Client.Startup;
using QuantifiedSelf.Windows.Core.Paths;

namespace QuantifiedSelf.Windows.Client;

internal sealed class WujiClient : IWujiClient
{
    private readonly WindowsAgentPaths _windowsPaths;
    private bool _initialized;
    private bool _disposed;

    public WujiClient(
        WindowsAgentPaths windowsPaths,
        IAgentClient agent,
        IActivityClient activity,
        IDiagnosticsClient diagnostics,
        ISettingsClient settings,
        IStartupClient startup,
        WujiClientContext context)
    {
        _windowsPaths = windowsPaths;
        Agent = agent;
        Activity = activity;
        Diagnostics = diagnostics;
        Settings = settings;
        Startup = startup;
        Context = context;
        Paths = new WujiClientPaths(windowsPaths);
    }

    public IAgentClient Agent { get; }

    public IActivityClient Activity { get; }

    public IDiagnosticsClient Diagnostics { get; }

    public ISettingsClient Settings { get; }

    public IStartupClient Startup { get; }

    public WujiClientContext Context { get; }

    public WujiClientPaths Paths { get; }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_initialized)
        {
            _windowsPaths.EnsureDirectories();
            _initialized = true;
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class AgentClient(
    IAgentStatusService status,
    IAgentControlService control,
    IAgentProcessService process,
    IAgentTransportHealthService transportHealth) : IAgentClient
{
    public IAgentStatusService Status { get; } = status;
    public IAgentControlService Control { get; } = control;
    public IAgentProcessService Process { get; } = process;
    public IAgentTransportHealthService TransportHealth { get; } = transportHealth;
}

internal sealed class ActivityClient(
    IOverviewDataService overview,
    ISamplesDataService samples,
    ISessionsDataService sessions,
    IAppsDataService apps,
    IDailyStatsService dailyStats,
    IWeeklyTrendService weeklyTrend,
    IHourActivityHeatmapService heatmap,
    IFocusInterruptionInsightService insights) : IActivityClient
{
    public IOverviewDataService Overview { get; } = overview;
    public ISamplesDataService Samples { get; } = samples;
    public ISessionsDataService Sessions { get; } = sessions;
    public IAppsDataService Apps { get; } = apps;
    public IDailyStatsService DailyStats { get; } = dailyStats;
    public IWeeklyTrendService WeeklyTrend { get; } = weeklyTrend;
    public IHourActivityHeatmapService Heatmap { get; } = heatmap;
    public IFocusInterruptionInsightService Insights { get; } = insights;
}

internal sealed class DiagnosticsClient(IDiagnosticsDataService service) : IDiagnosticsClient
{
    public Task<IReadOnlyList<Core.Events.AgentEvent>> GetRecentEventsAsync(
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        service.GetRecentEventsAsync(limit, cancellationToken);

    public Task<IReadOnlyList<Core.Events.AgentEvent>> GetRecentErrorsAsync(
        int limit = 10,
        CancellationToken cancellationToken = default) =>
        service.GetRecentErrorsAsync(limit, cancellationToken);

    public string GetCurrentJournalPath(DateTime? utcNow = null) =>
        service.GetCurrentJournalPath(utcNow);
}

internal sealed class SettingsClient(ISettingsService service) : ISettingsClient
{
    public Task<ClientSettingsSnapshot> GetClientSettingsAsync(
        CancellationToken cancellationToken = default) =>
        service.GetClientSettingsAsync(cancellationToken);

    public ClientSettingsSnapshot GetDefaultClientSettings() =>
        service.GetDefaultClientSettings();

    public Task<ClientSettingsUpdateResult> UpdateClientSettingsAsync(
        ClientSettingsSnapshot settings,
        CancellationToken cancellationToken = default) =>
        service.UpdateClientSettingsAsync(settings, cancellationToken);

    public async Task<Core.Options.AppSettings> ReadAppSettingsOrDefaultAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await service.ReadAppSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new Core.Options.AppSettings();
        }
    }

    public Task<Core.Options.AppSettings> ReadAppSettingsAsync(CancellationToken cancellationToken = default) =>
        service.ReadAppSettingsAsync(cancellationToken);

    public Task SaveAppSettingsAsync(
        Core.Options.AppSettings settings,
        CancellationToken cancellationToken = default) =>
        service.SaveAppSettingsAsync(settings, cancellationToken);

    public Task<Core.Options.WindowsAgentOptions> ReadAgentOptionsAsync(
        CancellationToken cancellationToken = default) =>
        service.ReadAgentOptionsAsync(cancellationToken);

    public Task SaveAgentOptionsAsync(
        Core.Options.WindowsAgentOptions options,
        CancellationToken cancellationToken = default) =>
        service.SaveAgentOptionsAsync(options, cancellationToken);

    public Task SaveAgentOptionsWithBackupAsync(
        Core.Options.WindowsAgentOptions options,
        CancellationToken cancellationToken = default) =>
        service.SaveAgentOptionsWithBackupAsync(options, cancellationToken);

    public Task RestoreAgentOptionsBackupAsync(CancellationToken cancellationToken = default) =>
        service.RestoreAgentOptionsBackupAsync(cancellationToken);
}

internal sealed class StartupClient(
    IStartupRegistrationService service,
    StartupLaunchOptions launchOptions) : IStartupClient
{
    public StartupLaunchOptions LaunchOptions { get; } = launchOptions;

    public Task<StartupRegistrationStatus> RegisterAsync() => service.RegisterAsync();

    public Task<StartupRegistrationStatus> UnregisterAsync() => service.UnregisterAsync();

    public Task<StartupRegistrationStatus> GetStatusAsync() => service.GetStatusAsync();
}
