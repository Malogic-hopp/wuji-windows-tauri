namespace QuantifiedSelf.Windows.ApplicationLayer.Settings;

public sealed record ClientSettingsSnapshot(
    ClientAppSettings AppSettings,
    ClientAgentOptions AgentOptions);

public sealed record ClientAppSettings(
    string Theme,
    long RefreshIntervalSeconds,
    bool AutoStartAgentWhenAppStarts);

public sealed record ClientAgentOptions(
    long SamplingIntervalSeconds,
    long IdleThresholdSeconds,
    long HeartbeatIntervalSeconds,
    long StaleThresholdSeconds,
    long RetentionDays,
    bool EnableJsonlJournal,
    bool EnableAgentEventJournal,
    bool EnableSessionMerge,
    bool MaskWindowTitles);

public sealed record ClientSettingsValidationIssue(string FieldName, string Message);

public sealed class ClientSettingsUpdateResult
{
    private ClientSettingsUpdateResult(
        ClientSettingsSnapshot? settings,
        IReadOnlyList<ClientSettingsValidationIssue> issues)
    {
        Settings = settings;
        Issues = issues;
    }

    public bool IsValid => Issues.Count == 0;

    public ClientSettingsSnapshot? Settings { get; }

    public IReadOnlyList<ClientSettingsValidationIssue> Issues { get; }

    public static ClientSettingsUpdateResult Success(ClientSettingsSnapshot settings) =>
        new(settings ?? throw new ArgumentNullException(nameof(settings)), []);

    public static ClientSettingsUpdateResult ValidationFailure(
        IReadOnlyList<ClientSettingsValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (issues.Count == 0)
        {
            throw new ArgumentException("At least one validation issue is required.", nameof(issues));
        }

        return new ClientSettingsUpdateResult(null, issues);
    }
}
