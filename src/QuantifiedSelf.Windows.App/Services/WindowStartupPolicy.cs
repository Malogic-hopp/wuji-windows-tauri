using QuantifiedSelf.Windows.Client.Startup;

namespace QuantifiedSelf.Windows.App.Services;

/// <summary>
/// Pure logic: determines whether the main window should be shown on launch
/// based on parsed command-line arguments. No WPF, Dispatcher, or NotifyIcon dependencies.
/// </summary>
public sealed class WindowStartupPolicy
{
    public bool ShouldShowMainWindowOnLaunch { get; init; }
    public bool ShouldStartHidden { get; init; }

    /// <summary>
    /// Only AutoStart-hide when BOTH --from-autostart AND --start-hidden are present.
    /// --start-hidden alone (without --from-autostart) is treated as manual launch.
    /// </summary>
    public static WindowStartupPolicy Decide(StartupLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Only enter AutoStartHidden when both flags are present
        var isAutostartHidden = options.FromAutostart && options.StartHidden;

        return new WindowStartupPolicy
        {
            ShouldShowMainWindowOnLaunch = !isAutostartHidden,
            ShouldStartHidden = isAutostartHidden
        };
    }
}
