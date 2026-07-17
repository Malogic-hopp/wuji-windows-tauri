using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Settings;
using QuantifiedSelf.Windows.ApplicationLayer.Settings;
using QuantifiedSelf.Windows.Core.Options;

namespace QuantifiedSelf.Windows.Application.Tests;

[Trait("Category", "Fast")]
public sealed class SettingsServiceTests
{
    [Fact]
    public async Task GetClientSettings_ProjectsOnlyTheApprovedSafeAllowlist()
    {
        var appStore = new FakeAppSettingsStore(new AppSettings
        {
            Theme = "Dark",
            RefreshIntervalSeconds = 30,
            AutoStartAgentWhenAppStarts = true,
            StartAppOnWindowsLogin = true,
            LastSelectedPage = "Privacy"
        });
        var agentStore = new FakeAgentOptionsStore(new WindowsAgentOptions
        {
            SamplingIntervalSeconds = 5,
            UseMockCapture = true,
            ExcludedProcesses = ["PrivateProcess"],
            ExcludedTitlePatterns = ["Private title"]
        });
        var service = new SettingsService(appStore, agentStore);

        var result = await service.GetClientSettingsAsync();

        Assert.Equal("Dark", result.AppSettings.Theme);
        Assert.Equal(30, result.AppSettings.RefreshIntervalSeconds);
        Assert.True(result.AppSettings.AutoStartAgentWhenAppStarts);
        Assert.Equal(5, result.AgentOptions.SamplingIntervalSeconds);
        Assert.DoesNotContain("StartAppOnWindowsLogin", result.AppSettings.GetType().GetProperties().Select(x => x.Name));
        Assert.DoesNotContain("UseMockCapture", result.AgentOptions.GetType().GetProperties().Select(x => x.Name));
        Assert.DoesNotContain("ExcludedProcesses", result.AgentOptions.GetType().GetProperties().Select(x => x.Name));
        Assert.DoesNotContain("ExcludedTitlePatterns", result.AgentOptions.GetType().GetProperties().Select(x => x.Name));
    }

    [Fact]
    public void GetDefaultClientSettings_ProjectsCoreDefaultsWithoutReadingStores()
    {
        var appStore = new FakeAppSettingsStore(new AppSettings { Theme = "Dark" });
        var agentStore = new FakeAgentOptionsStore(new WindowsAgentOptions { SamplingIntervalSeconds = 17 });
        var service = new SettingsService(appStore, agentStore);

        var result = service.GetDefaultClientSettings();
        var defaultApp = new AppSettings();
        var defaultAgent = new WindowsAgentOptions();

        Assert.Equal(defaultApp.Theme, result.AppSettings.Theme);
        Assert.Equal(defaultApp.RefreshIntervalSeconds, result.AppSettings.RefreshIntervalSeconds);
        Assert.Equal(defaultAgent.SamplingIntervalSeconds, result.AgentOptions.SamplingIntervalSeconds);
        Assert.Equal(defaultAgent.RetentionDays, result.AgentOptions.RetentionDays);
        Assert.Equal(0, appStore.ReadCount);
        Assert.Equal(0, agentStore.ReadCount);
    }

    [Fact]
    public async Task UpdateClientSettings_ValidatesAndPreservesEveryNonAllowlistedField()
    {
        var appStore = new FakeAppSettingsStore(new AppSettings
        {
            StartAppOnWindowsLogin = true,
            MinimizeToTray = false,
            CloseToTray = false,
            LastSelectedPage = "Privacy"
        });
        var agentStore = new FakeAgentOptionsStore(new WindowsAgentOptions
        {
            IdleSummaryIntervalMinutes = 17,
            UseMockCapture = true,
            ExcludedProcesses = ["KeePass", "PrivateProcess"],
            ExcludedTitlePatterns = ["Private title"]
        });
        var service = new SettingsService(appStore, agentStore);

        var result = await service.UpdateClientSettingsAsync(ValidUpdate() with
        {
            AppSettings = new ClientAppSettings("dark", 25, true)
        });

        Assert.True(result.IsValid);
        Assert.NotNull(result.Settings);
        Assert.Equal("Dark", result.Settings.AppSettings.Theme);
        Assert.Equal(1, appStore.WriteCount);
        Assert.Equal(1, agentStore.WriteWithBackupCount);
        Assert.Equal(0, agentStore.RestoreCount);
        Assert.True(appStore.Value!.StartAppOnWindowsLogin);
        Assert.False(appStore.Value.MinimizeToTray);
        Assert.False(appStore.Value.CloseToTray);
        Assert.Equal("Privacy", appStore.Value.LastSelectedPage);
        Assert.Equal(17, agentStore.Value!.IdleSummaryIntervalMinutes);
        Assert.True(agentStore.Value.UseMockCapture);
        Assert.Equal(["KeePass", "PrivateProcess"], agentStore.Value.ExcludedProcesses);
        Assert.Equal(["Private title"], agentStore.Value.ExcludedTitlePatterns);
    }

    [Fact]
    public async Task UpdateClientSettings_InvalidValuesReturnFieldIssuesWithoutWriting()
    {
        var appStore = new FakeAppSettingsStore(new AppSettings());
        var agentStore = new FakeAgentOptionsStore(new WindowsAgentOptions());
        var service = new SettingsService(appStore, agentStore);
        var update = ValidUpdate() with
        {
            AppSettings = new ClientAppSettings("RemoteTheme", 999, false),
            AgentOptions = ValidUpdate().AgentOptions with
            {
                SamplingIntervalSeconds = 0,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 30
            }
        };

        var result = await service.UpdateClientSettingsAsync(update);

        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        Assert.Contains(result.Issues, issue => issue.FieldName == "appSettings.theme");
        Assert.Contains(result.Issues, issue => issue.FieldName == "appSettings.refreshIntervalSeconds");
        Assert.Contains(result.Issues, issue => issue.FieldName == "agentOptions.samplingIntervalSeconds");
        Assert.Contains(result.Issues, issue => issue.FieldName == "agentOptions.staleThresholdSeconds");
        Assert.Equal(0, appStore.WriteCount);
        Assert.Equal(0, agentStore.WriteWithBackupCount);
    }

    [Fact]
    public async Task UpdateClientSettings_DoesNotValidateOrRewriteNonAllowlistedPrivacyLists()
    {
        var excludedProcesses = new List<string> { @"C:\Private\secret.exe" };
        var excludedTitles = new List<string> { @"C:\Private\document.txt" };
        var appStore = new FakeAppSettingsStore(new AppSettings());
        var agentStore = new FakeAgentOptionsStore(new WindowsAgentOptions
        {
            ExcludedProcesses = excludedProcesses,
            ExcludedTitlePatterns = excludedTitles
        });
        var service = new SettingsService(appStore, agentStore);

        var result = await service.UpdateClientSettingsAsync(ValidUpdate());

        Assert.True(result.IsValid);
        Assert.Same(excludedProcesses, agentStore.Value!.ExcludedProcesses);
        Assert.Same(excludedTitles, agentStore.Value.ExcludedTitlePatterns);
    }

    [Fact]
    public async Task GetClientSettings_CorruptAllowedValuesFallBackToCoreDefaults()
    {
        var appStore = new FakeAppSettingsStore(new AppSettings
        {
            Theme = "RemoteTheme",
            RefreshIntervalSeconds = -1,
            AutoStartAgentWhenAppStarts = true
        });
        var agentStore = new FakeAgentOptionsStore(new WindowsAgentOptions
        {
            SamplingIntervalSeconds = 0,
            IdleThresholdSeconds = 1,
            HeartbeatIntervalSeconds = 30,
            StaleThresholdSeconds = 30,
            RetentionDays = -1,
            EnableJsonlJournal = false
        });
        var service = new SettingsService(appStore, agentStore);

        var result = await service.GetClientSettingsAsync();

        var defaultApp = new AppSettings();
        var defaultAgent = new WindowsAgentOptions();
        Assert.Equal(defaultApp.Theme, result.AppSettings.Theme);
        Assert.Equal(defaultApp.RefreshIntervalSeconds, result.AppSettings.RefreshIntervalSeconds);
        Assert.True(result.AppSettings.AutoStartAgentWhenAppStarts);
        Assert.Equal(defaultAgent.SamplingIntervalSeconds, result.AgentOptions.SamplingIntervalSeconds);
        Assert.Equal(defaultAgent.IdleThresholdSeconds, result.AgentOptions.IdleThresholdSeconds);
        Assert.Equal(defaultAgent.HeartbeatIntervalSeconds, result.AgentOptions.HeartbeatIntervalSeconds);
        Assert.Equal(defaultAgent.StaleThresholdSeconds, result.AgentOptions.StaleThresholdSeconds);
        Assert.Equal(defaultAgent.RetentionDays, result.AgentOptions.RetentionDays);
        Assert.False(result.AgentOptions.EnableJsonlJournal);
    }

    [Fact]
    public async Task UpdateClientSettings_AppWriteFailureRestoresAgentBackup()
    {
        var appStore = new FakeAppSettingsStore(new AppSettings()) { WriteException = new IOException("private path") };
        var originalAgentOptions = new WindowsAgentOptions { SamplingIntervalSeconds = 3 };
        var agentStore = new FakeAgentOptionsStore(originalAgentOptions);
        var service = new SettingsService(appStore, agentStore);

        await Assert.ThrowsAsync<IOException>(() => service.UpdateClientSettingsAsync(ValidUpdate()));

        Assert.Equal(1, agentStore.WriteWithBackupCount);
        Assert.Equal(1, agentStore.RestoreCount);
        Assert.Same(originalAgentOptions, agentStore.Value);
    }

    private static ClientSettingsSnapshot ValidUpdate() => new(
        new ClientAppSettings("Dark", 15, true),
        new ClientAgentOptions(
            SamplingIntervalSeconds: 3,
            IdleThresholdSeconds: 60,
            HeartbeatIntervalSeconds: 3,
            StaleThresholdSeconds: 15,
            RetentionDays: 30,
            EnableJsonlJournal: true,
            EnableAgentEventJournal: true,
            EnableSessionMerge: true,
            MaskWindowTitles: true));

    private sealed class FakeAppSettingsStore(AppSettings? value) : IAppSettingsStore
    {
        public AppSettings? Value { get; private set; } = value;

        public int WriteCount { get; private set; }

        public int ReadCount { get; private set; }

        public Exception? WriteException { get; init; }

        public Task<AppSettings?> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(Value);
        }

        public Task WriteAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            if (WriteException is not null)
            {
                return Task.FromException(WriteException);
            }

            Value = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAgentOptionsStore(WindowsAgentOptions? value) : IAgentOptionsStore
    {
        private WindowsAgentOptions? _backup;

        public WindowsAgentOptions? Value { get; private set; } = value;

        public int WriteWithBackupCount { get; private set; }

        public int RestoreCount { get; private set; }

        public int ReadCount { get; private set; }

        public Task<WindowsAgentOptions?> ReadAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(Value);
        }

        public Task WriteAsync(WindowsAgentOptions options, CancellationToken cancellationToken = default)
        {
            Value = options;
            return Task.CompletedTask;
        }

        public Task WriteWithBackupAsync(
            WindowsAgentOptions options,
            CancellationToken cancellationToken = default)
        {
            WriteWithBackupCount++;
            _backup = Value;
            Value = options;
            return Task.CompletedTask;
        }

        public Task RestoreBackupAsync(CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            Value = _backup;
            return Task.CompletedTask;
        }
    }
}
