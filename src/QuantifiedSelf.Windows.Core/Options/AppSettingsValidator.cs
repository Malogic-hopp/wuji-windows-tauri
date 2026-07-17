namespace QuantifiedSelf.Windows.Core.Options;

public sealed class AppSettingsValidator
{
    public const int RefreshIntervalSecondsMin = 5;
    public const int RefreshIntervalSecondsMax = 300;

    private static readonly IReadOnlyDictionary<string, string> KnownThemes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Light"] = "Light",
            ["Dark"] = "Dark",
            ["HighContrast"] = "HighContrast"
        };

    public AppSettingsValidationResult Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var issues = new List<AppSettingsValidationIssue>();
        if (settings.RefreshIntervalSeconds is < RefreshIntervalSecondsMin or > RefreshIntervalSecondsMax)
        {
            issues.Add(new AppSettingsValidationIssue(
                "refreshIntervalSeconds",
                $"must be between {RefreshIntervalSecondsMin} and {RefreshIntervalSecondsMax}."));
        }

        var theme = settings.Theme?.Trim() ?? string.Empty;
        if (!KnownThemes.TryGetValue(theme, out var normalizedTheme))
        {
            issues.Add(new AppSettingsValidationIssue(
                "theme",
                "must be Light, Dark, or HighContrast."));
            normalizedTheme = new AppSettings().Theme;
        }

        return new AppSettingsValidationResult(
            new AppSettings
            {
                AutoStartAgentWhenAppStarts = settings.AutoStartAgentWhenAppStarts,
                StartAppOnWindowsLogin = settings.StartAppOnWindowsLogin,
                MinimizeToTray = settings.MinimizeToTray,
                CloseToTray = settings.CloseToTray,
                RefreshIntervalSeconds = settings.RefreshIntervalSeconds,
                Theme = normalizedTheme,
                LastSelectedPage = settings.LastSelectedPage
            },
            issues);
    }
}

public sealed record AppSettingsValidationIssue(string FieldName, string Message);

public sealed class AppSettingsValidationResult
{
    public AppSettingsValidationResult(
        AppSettings normalizedSettings,
        IReadOnlyList<AppSettingsValidationIssue> issues)
    {
        NormalizedSettings = normalizedSettings ?? throw new ArgumentNullException(nameof(normalizedSettings));
        Issues = issues ?? throw new ArgumentNullException(nameof(issues));
    }

    public AppSettings NormalizedSettings { get; }

    public IReadOnlyList<AppSettingsValidationIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;
}
