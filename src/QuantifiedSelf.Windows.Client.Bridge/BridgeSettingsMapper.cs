using QuantifiedSelf.Windows.ApplicationLayer.Settings;
using QuantifiedSelf.Windows.Client.Bridge.Generated;

namespace QuantifiedSelf.Windows.Client.Bridge;

internal static class BridgeSettingsMapper
{
    private static readonly HashSet<string> AllowedFields =
    [
        "appSettings.theme",
        "appSettings.refreshIntervalSeconds",
        "appSettings.autoStartAgentWhenAppStarts",
        "agentOptions.samplingIntervalSeconds",
        "agentOptions.idleThresholdSeconds",
        "agentOptions.heartbeatIntervalSeconds",
        "agentOptions.staleThresholdSeconds",
        "agentOptions.retentionDays",
        "agentOptions.enableJsonlJournal",
        "agentOptions.enableAgentEventJournal",
        "agentOptions.enableSessionMerge",
        "agentOptions.maskWindowTitles"
    ];

    public static SettingsSnapshot ToContract(ClientSettingsSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new SettingsSnapshot
        {
            AppSettings = new SettingsAppSettings
            {
                Theme = ToContractTheme(source.AppSettings.Theme),
                RefreshIntervalSeconds = source.AppSettings.RefreshIntervalSeconds,
                AutoStartAgentWhenAppStarts = source.AppSettings.AutoStartAgentWhenAppStarts
            },
            AgentOptions = new SettingsAgentOptions
            {
                SamplingIntervalSeconds = source.AgentOptions.SamplingIntervalSeconds,
                IdleThresholdSeconds = source.AgentOptions.IdleThresholdSeconds,
                HeartbeatIntervalSeconds = source.AgentOptions.HeartbeatIntervalSeconds,
                StaleThresholdSeconds = source.AgentOptions.StaleThresholdSeconds,
                RetentionDays = source.AgentOptions.RetentionDays,
                EnableJsonlJournal = source.AgentOptions.EnableJsonlJournal,
                EnableAgentEventJournal = source.AgentOptions.EnableAgentEventJournal,
                EnableSessionMerge = source.AgentOptions.EnableSessionMerge,
                MaskWindowTitles = source.AgentOptions.MaskWindowTitles
            }
        };
    }

    public static ClientSettingsSnapshot ToApplication(SettingsSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ClientSettingsSnapshot(
            new ClientAppSettings(
                ToApplicationTheme(source.AppSettings.Theme),
                source.AppSettings.RefreshIntervalSeconds,
                source.AppSettings.AutoStartAgentWhenAppStarts),
            new ClientAgentOptions(
                source.AgentOptions.SamplingIntervalSeconds,
                source.AgentOptions.IdleThresholdSeconds,
                source.AgentOptions.HeartbeatIntervalSeconds,
                source.AgentOptions.StaleThresholdSeconds,
                source.AgentOptions.RetentionDays,
                source.AgentOptions.EnableJsonlJournal,
                source.AgentOptions.EnableAgentEventJournal,
                source.AgentOptions.EnableSessionMerge,
                source.AgentOptions.MaskWindowTitles));
    }

    public static IReadOnlyList<SettingsFieldError> ToFieldErrors(
        IReadOnlyList<ClientSettingsValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        return issues.Select(issue =>
        {
            var field = AllowedFields.Contains(issue.FieldName) ? issue.FieldName : "settings";
            return new SettingsFieldError
            {
                Field = field,
                Message = "设置值无效。"
            };
        }).ToArray();
    }

    private static SettingsTheme ToContractTheme(string value) => value switch
    {
        "Dark" => SettingsTheme.Dark,
        "HighContrast" => SettingsTheme.HighContrast,
        _ => SettingsTheme.Light
    };

    private static string ToApplicationTheme(SettingsTheme value) => value switch
    {
        SettingsTheme.Dark => "Dark",
        SettingsTheme.HighContrast => "HighContrast",
        _ => "Light"
    };

}
