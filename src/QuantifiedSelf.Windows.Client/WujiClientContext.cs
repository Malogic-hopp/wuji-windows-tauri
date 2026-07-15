using System.IO;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Core.Runtime;

namespace QuantifiedSelf.Windows.Client;

public sealed record WujiClientContext(
    string ChannelName,
    string ProductDisplayName,
    bool IsDefaultChannel);

public sealed class WujiClientPaths
{
    internal WujiClientPaths(WindowsAgentPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        Root = paths.Root;
        ConfigDirectory = paths.ConfigDir;
        DataDirectory = paths.DataDir;
        LogsDirectory = paths.LogsDir;
        RuntimeDirectory = paths.RuntimeDir;
        AppSettingsPath = Path.Combine(paths.ConfigDir, "app-settings.json");
        AgentOptionsPath = paths.AgentOptionsPath;
        DatabasePath = paths.DatabasePath;
    }

    public string Root { get; }

    public string ConfigDirectory { get; }

    public string DataDirectory { get; }

    public string LogsDirectory { get; }

    public string RuntimeDirectory { get; }

    public string AppSettingsPath { get; }

    public string AgentOptionsPath { get; }

    public string DatabasePath { get; }

    public static WujiClientPaths FromRoot(string root, string? channelName = null) =>
        new(new WindowsAgentPaths(root, channelName));

    public static implicit operator WujiClientPaths(WindowsAgentPaths paths) => new(paths);
}
