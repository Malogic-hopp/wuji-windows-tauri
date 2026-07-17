using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Settings;
using QuantifiedSelf.Windows.Core.Options;

namespace QuantifiedSelf.Windows.ApplicationLayer.Settings;

public sealed class SettingsService : ISettingsService
{
    private static readonly HashSet<string> ClientAgentFields =
    [
        "samplingIntervalSeconds",
        "idleThresholdSeconds",
        "heartbeatIntervalSeconds",
        "staleThresholdSeconds",
        "retentionDays"
    ];

    private readonly IAppSettingsStore _appSettingsStore;
    private readonly IAgentOptionsStore _agentOptionsStore;
    private readonly AppSettingsValidator _appSettingsValidator;
    private readonly AgentOptionsValidator _agentOptionsValidator;

    public SettingsService(
        IAppSettingsStore appSettingsStore,
        IAgentOptionsStore agentOptionsStore,
        AppSettingsValidator? appSettingsValidator = null,
        AgentOptionsValidator? agentOptionsValidator = null)
    {
        _appSettingsStore = appSettingsStore ?? throw new ArgumentNullException(nameof(appSettingsStore));
        _agentOptionsStore = agentOptionsStore ?? throw new ArgumentNullException(nameof(agentOptionsStore));
        _appSettingsValidator = appSettingsValidator ?? new AppSettingsValidator();
        _agentOptionsValidator = agentOptionsValidator ?? new AgentOptionsValidator();
    }

    public async Task<ClientSettingsSnapshot> GetClientSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var appSettingsTask = ReadAppSettingsAsync(cancellationToken);
        var agentOptionsTask = ReadAgentOptionsAsync(cancellationToken);
        await Task.WhenAll(appSettingsTask, agentOptionsTask).ConfigureAwait(false);
        return ToClientSettings(
            await appSettingsTask.ConfigureAwait(false),
            await agentOptionsTask.ConfigureAwait(false));
    }

    public ClientSettingsSnapshot GetDefaultClientSettings() =>
        ToClientSettings(new AppSettings(), new WindowsAgentOptions());

    public async Task<ClientSettingsUpdateResult> UpdateClientSettingsAsync(
        ClientSettingsSnapshot settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(settings.AppSettings);
        ArgumentNullException.ThrowIfNull(settings.AgentOptions);

        var currentAppSettingsTask = ReadAppSettingsAsync(cancellationToken);
        var currentAgentOptionsTask = ReadAgentOptionsAsync(cancellationToken);
        await Task.WhenAll(currentAppSettingsTask, currentAgentOptionsTask).ConfigureAwait(false);
        var currentAppSettings = await currentAppSettingsTask.ConfigureAwait(false);
        var currentAgentOptions = await currentAgentOptionsTask.ConfigureAwait(false);

        var issues = new List<ClientSettingsValidationIssue>();
        var appSettings = BuildAppSettings(settings.AppSettings, currentAppSettings, issues);
        var agentOptions = BuildAgentOptions(settings.AgentOptions, currentAgentOptions, issues);

        var appValidation = _appSettingsValidator.Validate(appSettings);
        issues.AddRange(appValidation.Issues.Select(issue => new ClientSettingsValidationIssue(
            $"appSettings.{issue.FieldName}",
            issue.Message)));

        var agentValidation = _agentOptionsValidator.Validate(agentOptions);
        issues.AddRange(agentValidation.Issues
            .Where(issue => ClientAgentFields.Contains(issue.FieldName))
            .Select(issue => new ClientSettingsValidationIssue(
                $"agentOptions.{issue.FieldName}",
                issue.Message)));

        if (issues.Count > 0)
        {
            return ClientSettingsUpdateResult.ValidationFailure(issues);
        }

        await _agentOptionsStore.WriteWithBackupAsync(
            agentOptions,
            cancellationToken).ConfigureAwait(false);
        try
        {
            await _appSettingsStore.WriteAsync(
                appValidation.NormalizedSettings,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _agentOptionsStore.RestoreBackupAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original save failure. The Bridge maps it to a safe internal error.
            }

            throw;
        }

        return ClientSettingsUpdateResult.Success(ToClientSettings(
            appValidation.NormalizedSettings,
            agentOptions));
    }

    public async Task<AppSettings> ReadAppSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await _appSettingsStore.ReadAsync(cancellationToken) ?? new AppSettings();
    }

    public Task SaveAppSettingsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return _appSettingsStore.WriteAsync(settings, cancellationToken);
    }

    public async Task<WindowsAgentOptions> ReadAgentOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        return await _agentOptionsStore.ReadAsync(cancellationToken) ?? new WindowsAgentOptions();
    }

    public Task SaveAgentOptionsAsync(
        WindowsAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _agentOptionsStore.WriteAsync(options, cancellationToken);
    }

    public Task SaveAgentOptionsWithBackupAsync(
        WindowsAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _agentOptionsStore.WriteWithBackupAsync(options, cancellationToken);
    }

    public Task RestoreAgentOptionsBackupAsync(CancellationToken cancellationToken = default)
    {
        return _agentOptionsStore.RestoreBackupAsync(cancellationToken);
    }

    private ClientSettingsSnapshot ToClientSettings(
        AppSettings appSettings,
        WindowsAgentOptions agentOptions)
    {
        var defaultAppSettings = new AppSettings();
        var defaultAgentOptions = new WindowsAgentOptions();
        var appValidation = _appSettingsValidator.Validate(appSettings);
        var appIssueFields = appValidation.Issues.Select(issue => issue.FieldName).ToHashSet(StringComparer.Ordinal);
        var agentValidation = _agentOptionsValidator.Validate(agentOptions);
        var agentIssueFields = agentValidation.Issues.Select(issue => issue.FieldName).ToHashSet(StringComparer.Ordinal);
        var heartbeatOrStaleInvalid = agentIssueFields.Contains("heartbeatIntervalSeconds")
            || agentIssueFields.Contains("staleThresholdSeconds");

        return new ClientSettingsSnapshot(
            new ClientAppSettings(
                appValidation.NormalizedSettings.Theme,
                appIssueFields.Contains("refreshIntervalSeconds")
                    ? defaultAppSettings.RefreshIntervalSeconds
                    : appSettings.RefreshIntervalSeconds,
                appSettings.AutoStartAgentWhenAppStarts),
            new ClientAgentOptions(
                agentIssueFields.Contains("samplingIntervalSeconds")
                    ? defaultAgentOptions.SamplingIntervalSeconds
                    : agentOptions.SamplingIntervalSeconds,
                agentIssueFields.Contains("idleThresholdSeconds")
                    ? defaultAgentOptions.IdleThresholdSeconds
                    : agentOptions.IdleThresholdSeconds,
                heartbeatOrStaleInvalid
                    ? defaultAgentOptions.HeartbeatIntervalSeconds
                    : agentOptions.HeartbeatIntervalSeconds,
                heartbeatOrStaleInvalid
                    ? defaultAgentOptions.StaleThresholdSeconds
                    : agentOptions.StaleThresholdSeconds,
                agentIssueFields.Contains("retentionDays")
                    ? defaultAgentOptions.RetentionDays
                    : agentOptions.RetentionDays,
                agentOptions.EnableJsonlJournal,
                agentOptions.EnableAgentEventJournal,
                agentOptions.EnableSessionMerge,
                agentOptions.MaskWindowTitles));
    }

    private static AppSettings BuildAppSettings(
        ClientAppSettings update,
        AppSettings current,
        ICollection<ClientSettingsValidationIssue> issues)
    {
        return new AppSettings
        {
            Theme = update.Theme,
            RefreshIntervalSeconds = ToInt(
                update.RefreshIntervalSeconds,
                current.RefreshIntervalSeconds,
                "appSettings.refreshIntervalSeconds",
                issues),
            AutoStartAgentWhenAppStarts = update.AutoStartAgentWhenAppStarts,
            StartAppOnWindowsLogin = current.StartAppOnWindowsLogin,
            MinimizeToTray = current.MinimizeToTray,
            CloseToTray = current.CloseToTray,
            LastSelectedPage = current.LastSelectedPage
        };
    }

    private static WindowsAgentOptions BuildAgentOptions(
        ClientAgentOptions update,
        WindowsAgentOptions current,
        ICollection<ClientSettingsValidationIssue> issues)
    {
        return new WindowsAgentOptions
        {
            SamplingIntervalSeconds = ToInt(update.SamplingIntervalSeconds, current.SamplingIntervalSeconds,
                "agentOptions.samplingIntervalSeconds", issues),
            IdleThresholdSeconds = ToInt(update.IdleThresholdSeconds, current.IdleThresholdSeconds,
                "agentOptions.idleThresholdSeconds", issues),
            HeartbeatIntervalSeconds = ToInt(update.HeartbeatIntervalSeconds, current.HeartbeatIntervalSeconds,
                "agentOptions.heartbeatIntervalSeconds", issues),
            StaleThresholdSeconds = ToInt(update.StaleThresholdSeconds, current.StaleThresholdSeconds,
                "agentOptions.staleThresholdSeconds", issues),
            RetentionDays = ToInt(update.RetentionDays, current.RetentionDays,
                "agentOptions.retentionDays", issues),
            EnableJsonlJournal = update.EnableJsonlJournal,
            EnableAgentEventJournal = update.EnableAgentEventJournal,
            EnableSessionMerge = update.EnableSessionMerge,
            MaskWindowTitles = update.MaskWindowTitles,
            IdleSummaryIntervalMinutes = current.IdleSummaryIntervalMinutes,
            UseMockCapture = current.UseMockCapture,
            ExcludedProcesses = current.ExcludedProcesses,
            ExcludedTitlePatterns = current.ExcludedTitlePatterns
        };
    }

    private static int ToInt(
        long value,
        int fallback,
        string fieldName,
        ICollection<ClientSettingsValidationIssue> issues)
    {
        if (value is >= int.MinValue and <= int.MaxValue)
        {
            return (int)value;
        }

        issues.Add(new ClientSettingsValidationIssue(fieldName, "must be a 32-bit integer."));
        return fallback;
    }
}
