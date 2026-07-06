namespace QuantifiedSelf.Windows.App.Services;

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
    public string[] RawArgs { get; init; } = [];

    public static StartupLaunchOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var fromAutostart = false;
        var startHidden = false;
        var showAgentConsole = false;

        foreach (var arg in args)
        {
            var normalized = arg.TrimStart('-').ToLowerInvariant();

            switch (normalized)
            {
                case "from-autostart":
                    fromAutostart = true;
                    break;
                case "start-hidden":
                    startHidden = true;
                    break;
                case "show-agent-console":
                    showAgentConsole = true;
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
            RawArgs = args.ToArray()
        };
    }
}
