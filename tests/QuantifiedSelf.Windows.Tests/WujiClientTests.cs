using System.IO;
using QuantifiedSelf.Windows.Client;
using QuantifiedSelf.Windows.Client.Startup;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Tests.TestHelpers;

namespace QuantifiedSelf.Windows.Tests;

[Trait("Category", "Integration")]
public sealed class WujiClientTests
{
    [Fact]
    public async Task Factory_InitializeBuildsAllFeatureClientsAndChannelPaths()
    {
        using var workspace = new TempWorkspace("wuji-client-tests");
        var launchOptions = StartupLaunchOptions.Parse(["--channel", "dev", "--ui-preview"]);
        await using var client = WujiClientFactory.Create(new WujiClientOptions
        {
            ChannelName = launchOptions.ChannelName,
            DataRootPath = workspace.Root,
            UserIdentity = "test-user",
            ProcessPathProvider = () => @"C:\WUJI\QuantifiedSelf.Windows.App.exe",
            LaunchOptions = launchOptions
        });

        await client.InitializeAsync();
        await client.InitializeAsync();

        Assert.NotNull(client.Agent);
        Assert.NotNull(client.Activity);
        Assert.NotNull(client.Diagnostics);
        Assert.NotNull(client.Settings);
        Assert.NotNull(client.Startup);
        Assert.Equal("dev", client.Context.ChannelName);
        Assert.False(client.Context.IsDefaultChannel);
        Assert.Equal("WUJI Dev", client.Context.ProductDisplayName);
        Assert.True(client.Startup.LaunchOptions.UsePreviewUi);
        Assert.Equal(workspace.Root, client.Paths.Root);
        Assert.True(Directory.Exists(client.Paths.ConfigDirectory));
        Assert.True(Directory.Exists(client.Paths.DataDirectory));
        Assert.True(Directory.Exists(client.Paths.LogsDirectory));
        Assert.True(Directory.Exists(client.Paths.RuntimeDirectory));
    }

    [Fact]
    public async Task SettingsClient_RoundTripsExistingJsonStores()
    {
        using var workspace = new TempWorkspace("wuji-client-settings-tests");
        await using var client = WujiClientFactory.Create(new WujiClientOptions
        {
            DataRootPath = workspace.Root,
            UserIdentity = "test-user"
        });
        await client.InitializeAsync();

        await client.Settings.SaveAppSettingsAsync(new AppSettings
        {
            Theme = "Dark",
            RefreshIntervalSeconds = 27
        });

        var settings = await client.Settings.ReadAppSettingsAsync();

        Assert.Equal("Dark", settings.Theme);
        Assert.Equal(27, settings.RefreshIntervalSeconds);
        Assert.True(File.Exists(client.Paths.AppSettingsPath));
    }

    [Fact]
    public async Task SettingsClient_MalformedAppSettingsReturnsSafeDefaultForStartup()
    {
        using var workspace = new TempWorkspace("wuji-client-invalid-settings-tests");
        await using var client = WujiClientFactory.Create(new WujiClientOptions
        {
            DataRootPath = workspace.Root,
            UserIdentity = "test-user"
        });
        await client.InitializeAsync();
        await File.WriteAllTextAsync(client.Paths.AppSettingsPath, "{ invalid json");

        var settings = await client.Settings.ReadAppSettingsOrDefaultAsync();

        Assert.Equal(new AppSettings().Theme, settings.Theme);
        Assert.Equal(new AppSettings().RefreshIntervalSeconds, settings.RefreshIntervalSeconds);
    }

    [Fact]
    public async Task Dispose_DoesNotIssueAgentStopFallback()
    {
        using var workspace = new TempWorkspace("wuji-client-dispose-tests");
        var client = WujiClientFactory.Create(new WujiClientOptions
        {
            DataRootPath = workspace.Root,
            UserIdentity = "test-user"
        });
        await client.InitializeAsync();

        await client.DisposeAsync();

        Assert.False(File.Exists(Path.Combine(
            client.Paths.RuntimeDirectory,
            "agent_control.json")));
    }

    [Fact]
    public async Task Initialize_AfterDisposeRejectsReactivation()
    {
        using var workspace = new TempWorkspace("wuji-client-disposed-tests");
        var client = WujiClientFactory.Create(new WujiClientOptions
        {
            DataRootPath = workspace.Root,
            UserIdentity = "test-user"
        });

        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.InitializeAsync());
    }

    [Fact]
    public async Task Initialize_CancellationDoesNotCreateRuntimeDirectories()
    {
        using var workspace = new TempWorkspace("wuji-client-cancel-tests");
        await using var client = WujiClientFactory.Create(new WujiClientOptions
        {
            DataRootPath = workspace.Root,
            UserIdentity = "test-user"
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.InitializeAsync(cancellation.Token));

        Assert.False(Directory.Exists(client.Paths.RuntimeDirectory));
    }
}
