namespace QuantifiedSelf.Windows.Core.Options;

public sealed class WindowsAgentOptions
{
    public int SamplingIntervalSeconds { get; set; } = 3;

    public int IdleThresholdSeconds { get; set; } = 60;

    public int IdleSummaryIntervalMinutes { get; set; } = 5;

    public int RetentionDays { get; set; } = 30;

    public int HeartbeatIntervalSeconds { get; set; } = 3;

    public int StaleThresholdSeconds { get; set; } = 15;

    public bool UseMockCapture { get; set; }

    public bool EnableJsonlJournal { get; set; } = true;

    public bool EnableSessionMerge { get; set; } = true;

    public bool MaskWindowTitles { get; set; } = true;

    public List<string> ExcludedProcesses { get; set; } = new()
    {
        "KeePass",
        "1Password",
        "Bitwarden"
    };

    public List<string> ExcludedTitlePatterns { get; set; } = new();
}
