using System.Text.Json;
using QuantifiedSelf.Windows.Bridge.ContractGen;
using QuantifiedSelf.Windows.Client.Bridge.Generated;

namespace QuantifiedSelf.Windows.Client.Bridge.Tests;

[Trait("Category", "Fast")]
public sealed class BridgeContractTests
{
    private static readonly string[] UiAssemblyNames =
    [
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "System.Windows.Forms",
        "LiveChartsCore",
        "LiveChartsCore.SkiaSharpView.WPF",
        "SkiaSharp"
    ];

    [Fact]
    public void BridgeAssembly_ReferencesNoDesktopUiFrameworks()
    {
        var references = typeof(BridgeHost).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .Where(name => name is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var uiAssemblyName in UiAssemblyNames)
        {
            Assert.DoesNotContain(uiAssemblyName, references);
        }
    }

    [Fact]
    public void GeneratedArtifacts_MatchCanonicalSchema()
    {
        var drift = ContractGenerator.FindDrift(FindRepositoryRoot());

        Assert.Empty(drift);
    }

    [Fact]
    public void Schema_ContainsStageFiveAMethodsAndStableErrors()
    {
        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "contracts",
            "wuji-bridge",
            "v1",
            "bridge.schema.json");
        using var document = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var root = document.RootElement;

        Assert.Equal("1.0", root.GetProperty("x-wuji-api-version").GetString());
        Assert.Equal(
            [
                "bridge.hello",
                "client.initialize",
                "agent.getStatus",
                "agent.start",
                "agent.pause",
                "agent.resume",
                "agent.stop",
                "activity.getOverview",
                "settings.get",
                "settings.update",
                "bridge.shutdown"
            ],
            root.GetProperty("x-wuji-methods")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray());
        Assert.Contains(
            "request_timeout",
            root.GetProperty("x-wuji-error-codes")
                .EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "initialization_required",
            root.GetProperty("x-wuji-error-codes")
                .EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "validation_failed",
            root.GetProperty("x-wuji-error-codes")
                .EnumerateArray()
                .Select(value => value.GetString()));
    }

    [Fact]
    public void AgentContracts_ExposeOnlyApprovedSafeFields()
    {
        Assert.Equal(
            ["ActualState", "IsHealthy", "IsRunning", "IsStale", "LastHeartbeatUtc", "LastSampleUtc"],
            typeof(AgentStatus).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            ["Accepted", "ActualState", "Completed", "ErrorCode", "Message", "UsedFallback"],
            typeof(CommandResult).GetProperties().Select(property => property.Name).Order().ToArray());

        var generatedSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "QuantifiedSelf.Windows.Client.Bridge",
            "Generated",
            "BridgeContracts.g.cs"));
        string[] forbiddenFields =
        [
            "MachineName",
            "UserName",
            "ProcessId",
            "FullPipeName",
            "ExecutablePath",
            "DatabasePath",
            "DataRoot"
        ];

        foreach (var forbiddenField in forbiddenFields)
        {
            Assert.DoesNotContain(forbiddenField, generatedSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ActivityOverviewContracts_ExposeOnlyApprovedDashboardFields()
    {
        Assert.Equal(
            ["ActiveDurationSeconds", "DateUtc", "IdleDurationSeconds", "SessionCount", "TotalDurationSeconds", "UnknownDurationSeconds"],
            typeof(ActivityOverviewSummary).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            ["ActiveDurationSeconds", "DisplayName", "IdleDurationSeconds", "LastUsedAtUtc", "SessionCount", "TotalDurationSeconds", "UnknownDurationSeconds"],
            typeof(ActivityOverviewApp).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            ["ActiveDurationSeconds", "DisplayName", "EndedAtUtc", "IdleDurationSeconds", "StartedAtUtc", "TotalDurationSeconds", "UnknownDurationSeconds"],
            typeof(ActivityOverviewSession).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            ["RecentSessions", "Summary", "TopApps"],
            typeof(ActivityOverviewResult).GetProperties().Select(property => property.Name).Order().ToArray());

        var activityContractSource = string.Join(
            Environment.NewLine,
            typeof(ActivityOverviewSummary).GetProperties().Select(property => property.Name)
                .Concat(typeof(ActivityOverviewApp).GetProperties().Select(property => property.Name))
                .Concat(typeof(ActivityOverviewSession).GetProperties().Select(property => property.Name)));
        string[] forbiddenFields =
        [
            "WindowTitle",
            "ProcessName",
            "DatabasePath",
            "DataRoot",
            "ExecutablePath",
            "Exception",
            "StackTrace"
        ];

        foreach (var forbiddenField in forbiddenFields)
        {
            Assert.DoesNotContain(forbiddenField, activityContractSource, StringComparison.OrdinalIgnoreCase);
        }

        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "contracts",
            "wuji-bridge",
            "v1",
            "bridge.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var definitions = schema.RootElement.GetProperty("$defs");
        Assert.Equal(
            128,
            definitions.GetProperty("ActivityOverviewApp")
                .GetProperty("properties")
                .GetProperty("displayName")
                .GetProperty("maxLength")
                .GetInt32());
        Assert.Equal(
            0,
            definitions.GetProperty("ActivityOverviewSummary")
                .GetProperty("properties")
                .GetProperty("totalDurationSeconds")
                .GetProperty("minimum")
                .GetInt32());
        Assert.Equal(
            "date-time",
            definitions.GetProperty("ActivityOverviewSession")
                .GetProperty("properties")
                .GetProperty("startedAtUtc")
                .GetProperty("format")
                .GetString());
    }

    [Fact]
    public void SettingsContracts_ExposeOnlyTheApprovedSafeAllowlist()
    {
        Assert.Equal(
            ["AutoStartAgentWhenAppStarts", "RefreshIntervalSeconds", "Theme"],
            typeof(SettingsAppSettings).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            [
                "EnableAgentEventJournal", "EnableJsonlJournal", "EnableSessionMerge",
                "HeartbeatIntervalSeconds", "IdleThresholdSeconds", "MaskWindowTitles",
                "RetentionDays", "SamplingIntervalSeconds", "StaleThresholdSeconds"
            ],
            typeof(SettingsAgentOptions).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            ["AgentOptions", "AppSettings"],
            typeof(SettingsSnapshot).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            ["Defaults", "Settings"],
            typeof(SettingsGetResult).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            ["Field", "Message"],
            typeof(SettingsFieldError).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Contains(
            "FieldErrors",
            typeof(BridgeErrorData).GetProperties().Select(property => property.Name));

        var settingsContractSource = string.Join(
            Environment.NewLine,
            typeof(SettingsAppSettings).GetProperties().Select(property => property.Name)
                .Concat(typeof(SettingsAgentOptions).GetProperties().Select(property => property.Name)));
        string[] forbiddenFields =
        [
            "StartAppOnWindowsLogin", "LastSelectedPage", "MinimizeToTray", "CloseToTray",
            "UseMockCapture", "IdleSummaryIntervalMinutes", "ExcludedProcesses", "ExcludedTitlePatterns",
            "DataRoot", "DatabasePath", "AgentExecutablePath", "PipeName", "Mutex", "Registry"
        ];
        foreach (var forbiddenField in forbiddenFields)
        {
            Assert.DoesNotContain(forbiddenField, settingsContractSource, StringComparison.OrdinalIgnoreCase);
        }

        var schemaPath = Path.Combine(
            FindRepositoryRoot(), "contracts", "wuji-bridge", "v1", "bridge.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));
        var definitions = schema.RootElement.GetProperty("$defs");
        Assert.Equal(5, definitions.GetProperty("SettingsAppSettings").GetProperty("properties")
            .GetProperty("refreshIntervalSeconds").GetProperty("minimum").GetInt32());
        Assert.Equal(300, definitions.GetProperty("SettingsAppSettings").GetProperty("properties")
            .GetProperty("refreshIntervalSeconds").GetProperty("maximum").GetInt32());
        Assert.Equal(3650, definitions.GetProperty("SettingsAgentOptions").GetProperty("properties")
            .GetProperty("retentionDays").GetProperty("maximum").GetInt32());
    }

    [Fact]
    public void SettingsHandlers_UseOnlyTypedWujiClientSettingsUseCases()
    {
        var repositoryRoot = FindRepositoryRoot();
        var host = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "QuantifiedSelf.Windows.Client.Bridge", "BridgeHost.cs"));
        var mapper = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "QuantifiedSelf.Windows.Client.Bridge", "BridgeSettingsMapper.cs"));

        Assert.Contains("var settingsClient = _client.Settings", host, StringComparison.Ordinal);
        Assert.Contains("settingsClient.GetClientSettingsAsync", host, StringComparison.Ordinal);
        Assert.Contains("settingsClient.GetDefaultClientSettings", host, StringComparison.Ordinal);
        Assert.Contains("_client.Settings.UpdateClientSettingsAsync", host, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAppSettingsAsync", host, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAgentOptionsAsync", host, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveAppSettingsAsync", host, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveAgentOptionsAsync", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Infrastructure", host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sqlite", host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Registry", host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StartAppOnWindowsLogin", mapper, StringComparison.Ordinal);
        Assert.DoesNotContain("ExcludedTitlePatterns", mapper, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivityOverviewHandler_UsesOnlyWujiClientOverviewAndExplicitSafeMapper()
    {
        var repositoryRoot = FindRepositoryRoot();
        var host = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "QuantifiedSelf.Windows.Client.Bridge",
            "BridgeHost.cs"));
        var mapper = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "QuantifiedSelf.Windows.Client.Bridge",
            "BridgeActivityMapper.cs"));

        Assert.Contains("_client.Activity.Overview", host, StringComparison.Ordinal);
        Assert.DoesNotContain("_client.Activity.Apps", host, StringComparison.Ordinal);
        Assert.DoesNotContain("_client.Activity.Sessions", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Sqlite", host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Infrastructure", host, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WindowTitle", mapper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProcessName", mapper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Database", mapper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedArtifacts_ContainNoMachineSpecificAbsolutePaths()
    {
        var repositoryRoot = FindRepositoryRoot();
        foreach (var artifact in ContractGenerator.Generate(repositoryRoot))
        {
            Assert.DoesNotContain(repositoryRoot, artifact.Content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C:\\Users\\", artifact.Content, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QuantifiedSelf.Windows.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate repository root.");
    }
}
