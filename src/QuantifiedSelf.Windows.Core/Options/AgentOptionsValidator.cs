using System.IO;
using System.Linq;

namespace QuantifiedSelf.Windows.Core.Options;

public sealed class AgentOptionsValidator
{
    public const int SamplingIntervalSecondsMin = 1;
    public const int SamplingIntervalSecondsMax = 60;
    public const int IdleThresholdSecondsMin = 10;
    public const int IdleThresholdSecondsMax = 3600;
    public const int HeartbeatIntervalSecondsMin = 1;
    public const int HeartbeatIntervalSecondsMax = 60;
    public const int StaleThresholdSecondsMin = 5;
    public const int StaleThresholdSecondsMax = 600;
    public const int RetentionDaysMin = 1;
    public const int RetentionDaysMax = 3650;

    public AgentOptionsValidationResult Validate(WindowsAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var normalizedExcludedProcesses = NormalizeExcludedProcesses(options.ExcludedProcesses, out var excludedProcessesIssues);
        var normalizedExcludedTitlePatterns = NormalizeExcludedTitlePatterns(options.ExcludedTitlePatterns);

        var issues = new List<AgentOptionsValidationIssue>();
        issues.AddRange(excludedProcessesIssues);

        ValidateRange(
            options.SamplingIntervalSeconds,
            SamplingIntervalSecondsMin,
            SamplingIntervalSecondsMax,
            "samplingIntervalSeconds",
            issues);

        ValidateRange(
            options.IdleThresholdSeconds,
            IdleThresholdSecondsMin,
            IdleThresholdSecondsMax,
            "idleThresholdSeconds",
            issues);

        ValidateRange(
            options.HeartbeatIntervalSeconds,
            HeartbeatIntervalSecondsMin,
            HeartbeatIntervalSecondsMax,
            "heartbeatIntervalSeconds",
            issues);

        ValidateRange(
            options.StaleThresholdSeconds,
            StaleThresholdSecondsMin,
            StaleThresholdSecondsMax,
            "staleThresholdSeconds",
            issues);

        ValidateRange(
            options.RetentionDays,
            RetentionDaysMin,
            RetentionDaysMax,
            "retentionDays",
            issues);

        if (IsWithinRange(options.HeartbeatIntervalSeconds, HeartbeatIntervalSecondsMin, HeartbeatIntervalSecondsMax)
            && IsWithinRange(options.StaleThresholdSeconds, StaleThresholdSecondsMin, StaleThresholdSecondsMax)
            && options.StaleThresholdSeconds <= options.HeartbeatIntervalSeconds)
        {
            issues.Add(new AgentOptionsValidationIssue(
                "staleThresholdSeconds",
                "must be greater than heartbeatIntervalSeconds."));
        }

        var normalized = new WindowsAgentOptions
        {
            SamplingIntervalSeconds = options.SamplingIntervalSeconds,
            IdleThresholdSeconds = options.IdleThresholdSeconds,
            IdleSummaryIntervalMinutes = options.IdleSummaryIntervalMinutes,
            RetentionDays = options.RetentionDays,
            HeartbeatIntervalSeconds = options.HeartbeatIntervalSeconds,
            StaleThresholdSeconds = options.StaleThresholdSeconds,
            UseMockCapture = options.UseMockCapture,
            EnableJsonlJournal = options.EnableJsonlJournal,
            EnableAgentEventJournal = options.EnableAgentEventJournal,
            EnableSessionMerge = options.EnableSessionMerge,
            MaskWindowTitles = options.MaskWindowTitles,
            ExcludedProcesses = normalizedExcludedProcesses,
            ExcludedTitlePatterns = normalizedExcludedTitlePatterns
        };

        return new AgentOptionsValidationResult(normalized, issues);
    }

    private static void ValidateRange(
        int value,
        int minimum,
        int maximum,
        string fieldName,
        ICollection<AgentOptionsValidationIssue> issues)
    {
        if (!IsWithinRange(value, minimum, maximum))
        {
            issues.Add(new AgentOptionsValidationIssue(
                fieldName,
                $"must be between {minimum} and {maximum}."));
        }
    }

    private static bool IsWithinRange(int value, int minimum, int maximum)
    {
        return value >= minimum && value <= maximum;
    }

    private static List<string> NormalizeExcludedProcesses(
        IEnumerable<string>? rawValues,
        out List<AgentOptionsValidationIssue> issues)
    {
        issues = new List<AgentOptionsValidationIssue>();
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (rawValues is null)
        {
            return normalized;
        }

        var index = 0;
        foreach (var rawValue in rawValues)
        {
            index++;

            if (string.IsNullOrWhiteSpace(rawValue))
            {
                issues.Add(new AgentOptionsValidationIssue(
                    "excludedProcesses",
                    $"item {index} is empty."));
                continue;
            }

            var trimmed = rawValue.Trim();
            if (LooksLikePath(trimmed))
            {
                issues.Add(new AgentOptionsValidationIssue(
                    "excludedProcesses",
                    $"item {index} must be a process name, not a path."));
                continue;
            }

            var normalizedItem = NormalizeProcessName(trimmed);
            if (string.IsNullOrWhiteSpace(normalizedItem))
            {
                issues.Add(new AgentOptionsValidationIssue(
                    "excludedProcesses",
                    $"item {index} is empty after normalization."));
                continue;
            }

            if (seen.Add(normalizedItem))
            {
                normalized.Add(normalizedItem);
            }
        }

        return normalized;
    }

    private static List<string> NormalizeExcludedTitlePatterns(IEnumerable<string>? rawValues)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (rawValues is null)
        {
            return normalized;
        }

        foreach (var rawValue in rawValues)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var trimmed = rawValue.Trim();
            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized;
    }

    private static string NormalizeProcessName(string value)
    {
        var normalized = value.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized.Trim();
    }

    private static bool LooksLikePath(string value)
    {
        if (Path.IsPathRooted(value))
        {
            return true;
        }

        return value.Contains(Path.DirectorySeparatorChar)
            || value.Contains(Path.AltDirectorySeparatorChar)
            || value.Contains(':');
    }
}

public sealed record AgentOptionsValidationIssue(string FieldName, string Message)
{
    public string SafeText => string.IsNullOrWhiteSpace(FieldName)
        ? Message
        : $"{FieldName}: {Message}";
}

public sealed class AgentOptionsValidationResult
{
    public AgentOptionsValidationResult(WindowsAgentOptions normalizedOptions, IReadOnlyList<AgentOptionsValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(normalizedOptions);
        ArgumentNullException.ThrowIfNull(issues);

        NormalizedOptions = normalizedOptions;
        Issues = issues;
    }

    public WindowsAgentOptions NormalizedOptions { get; }

    public IReadOnlyList<AgentOptionsValidationIssue> Issues { get; }

    public IReadOnlyList<string> Errors => Issues
        .Select(issue => issue.SafeText)
        .ToArray();

    public bool IsValid => Issues.Count == 0;

    public string SafeMessageText => IsValid
        ? "Agent options are valid."
        : string.Join(Environment.NewLine, Issues.Select(issue => issue.SafeText));
}
