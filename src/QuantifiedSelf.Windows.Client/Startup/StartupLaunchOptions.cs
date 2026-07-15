using QuantifiedSelf.Windows.Core.Runtime;

namespace QuantifiedSelf.Windows.Client.Startup;

public enum LaunchMode
{
    Manual,
    AutoStart
}

public sealed class StartupLaunchOptions
{
    public LaunchMode Mode { get; init; }
    public bool StartHidden { get; init; }
    public bool FromAutostart { get; init; }
    public bool ShowAgentConsole { get; init; }
    public string ChannelName { get; init; } = RuntimeChannel.DefaultName;
    public bool UsePreviewUi { get; init; }
    public string[] RawArgs { get; init; } = [];

    public static StartupLaunchOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var fromAutostart = false;
        var startHidden = false;
        var showAgentConsole = false;
        var channelName = RuntimeChannel.DefaultName;
        var usePreviewUi = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var normalized = arg.TrimStart('-').ToLowerInvariant();
            var value = ReadValue(arg);

            switch (normalized)
            {
                case var item when item.StartsWith("channel=", StringComparison.OrdinalIgnoreCase):
                    channelName = RuntimeChannel.Normalize(value);
                    break;
                case "from-autostart":
                    fromAutostart = true;
                    break;
                case "start-hidden":
                    startHidden = true;
                    break;
                case "show-agent-console":
                    showAgentConsole = true;
                    break;
                case "ui-preview":
                    usePreviewUi = true;
                    break;
                case "channel":
                    if (i + 1 < args.Length)
                    {
                        channelName = RuntimeChannel.Normalize(args[++i]);
                    }
                    break;
                default:
                    // Unknown args are safely ignored
                    break;
            }
        }

        return new StartupLaunchOptions
        {
            Mode = fromAutostart ? LaunchMode.AutoStart : LaunchMode.Manual,
            StartHidden = startHidden,
            FromAutostart = fromAutostart,
            ShowAgentConsole = showAgentConsole,
            ChannelName = channelName,
            UsePreviewUi = usePreviewUi,
            RawArgs = args.ToArray()
        };
    }

    private static string? ReadValue(string arg)
    {
        var separator = arg.IndexOf('=');
        return separator < 0 || separator == arg.Length - 1
            ? null
            : arg[(separator + 1)..];
    }
}
