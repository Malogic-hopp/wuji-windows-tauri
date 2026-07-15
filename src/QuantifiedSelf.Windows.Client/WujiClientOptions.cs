using QuantifiedSelf.Windows.Client.Startup;
using QuantifiedSelf.Windows.Core.Runtime;

namespace QuantifiedSelf.Windows.Client;

public sealed class WujiClientOptions
{
    public string ChannelName { get; init; } = RuntimeChannel.DefaultName;

    public string? DataRootPath { get; init; }

    public bool ShowAgentConsole { get; init; }

    public string? UserIdentity { get; init; }

    public Func<string?>? ProcessPathProvider { get; init; }

    public StartupLaunchOptions LaunchOptions { get; init; } = StartupLaunchOptions.Parse([]);

    public static WujiClientOptions FromLaunchOptions(
        StartupLaunchOptions launchOptions,
        string? dataRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(launchOptions);

        return new WujiClientOptions
        {
            ChannelName = launchOptions.ChannelName,
            DataRootPath = dataRootPath,
            ShowAgentConsole = launchOptions.ShowAgentConsole,
            LaunchOptions = launchOptions
        };
    }
}
