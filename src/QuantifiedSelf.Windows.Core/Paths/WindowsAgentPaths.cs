using QuantifiedSelf.Windows.Core.Runtime;

namespace QuantifiedSelf.Windows.Core.Paths;

public sealed class WindowsAgentPaths
{
    private readonly RuntimeChannel _channel;

    public string ChannelName => _channel.Name;

    public string Root { get; }

    public string ConfigDir => Path.Combine(Root, "config");

    public string DataDir => Path.Combine(Root, "data");

    public string LogsDir => Path.Combine(Root, "logs");

    public string RuntimeDir => Path.Combine(Root, "runtime");

    public string RuntimeStatePath => Path.Combine(RuntimeDir, "runtime_state.json");

    public string HealthStatePath => Path.Combine(RuntimeDir, "health_state.json");

    public string AgentControlPath => Path.Combine(RuntimeDir, "agent_control.json");

    public string AgentOptionsPath => Path.Combine(ConfigDir, "windows-agent.json");

    public string DatabasePath => Path.Combine(DataDir, "quantified_self_windows.db");

    public WindowsAgentPaths(string? root = null, string? channelName = null)
    {
        _channel = RuntimeChannel.Parse(channelName);

        Root = root
            ?? Environment.GetEnvironmentVariable("QUANTIFIEDSELF_WINDOWS_AGENT_ROOT")
#if DEBUG
            ?? (_channel.IsDefault && Directory.Exists(@"D:\WUJI\WindowsAgent")
                ? @"D:\WUJI\WindowsAgent"
                : null)
#endif
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                _channel.DataRootProductFolder,
                "WindowsAgent");
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(RuntimeDir);
    }
}
