using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Client.Startup;

namespace QuantifiedSelf.Windows.App.Services;

/// <summary>
/// Pure model that converts StartupRegistrationStatus and LaunchMode
/// into safe, user-visible display text for Diagnostics and Settings.
///
/// Never contains full paths, SIDs, raw registry exception text, or stack traces.
/// All text values are pre-sanitized and safe for UI display.
/// </summary>
public sealed class StartupRegistrationDisplayModel
{
    /// <summary>
    /// Short status label: "Enabled", "Disabled", "Mismatch", "Error", "Unavailable", or "Unknown".
    /// </summary>
    public string LoginStartupStatusText { get; init; } = "Unknown";

    /// <summary>
    /// Launch mode label: "Manual" or "AutoStart".
    /// </summary>
    public string LaunchModeText { get; init; } = "Manual";

    /// <summary>
    /// Safe human-readable summary of the current registration state.
    /// Examples: "Registered to current app", "Not registered",
    /// "Registered command needs repair", "Registration unavailable",
    /// "Registration unavailable in current launch mode".
    /// </summary>
    public string StartupRegistrationSummary { get; init; } = "Unknown";

    /// <summary>
    /// Safe error text. "None" when no error has occurred.
    /// Never contains full paths, SIDs, raw registry exception text, or stack traces.
    /// </summary>
    public string LastStartupRegistrationErrorText { get; init; } = "None";

    /// <summary>
    /// Builds a display model from the given status and launch mode.
    /// All output text is pre-sanitized and safe for UI display.
    /// </summary>
    public static StartupRegistrationDisplayModel FromStatus(
        StartupRegistrationStatus status,
        LaunchMode launchMode)
    {
        ArgumentNullException.ThrowIfNull(status);

        var loginStatus = status.State switch
        {
            StartupRegistrationState.Enabled => "Enabled",
            StartupRegistrationState.Disabled => "Disabled",
            StartupRegistrationState.Mismatch => "Mismatch",
            StartupRegistrationState.Error => "Error",
            StartupRegistrationState.UnsupportedInCurrentLaunchMode => "Unavailable",
            _ => "Unknown"
        };

        var launchModeText = launchMode switch
        {
            LaunchMode.AutoStart => "AutoStart",
            _ => "Manual"
        };

        var summary = status.State switch
        {
            StartupRegistrationState.Enabled => "Registered to current app",
            StartupRegistrationState.Disabled => "Not registered",
            StartupRegistrationState.Mismatch => "Registered command needs repair",
            StartupRegistrationState.Error => "Registration unavailable",
            StartupRegistrationState.UnsupportedInCurrentLaunchMode =>
                "Registration unavailable in current launch mode",
            _ => "Unknown"
        };

        var errorText = status.State switch
        {
            StartupRegistrationState.Error => SanitizeErrorText(status.DetailText),
            _ => "None"
        };

        return new StartupRegistrationDisplayModel
        {
            LoginStartupStatusText = loginStatus,
            LaunchModeText = launchModeText,
            StartupRegistrationSummary = summary,
            LastStartupRegistrationErrorText = errorText
        };
    }

    /// <summary>
    /// Sanitizes error detail text for safe UI display.
    /// Removes full paths, SIDs, computer names, and other sensitive information.
    /// Returns "Registration unavailable" if the sanitized text is empty.
    /// </summary>
    private static string SanitizeErrorText(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "Registration unavailable";

        var sanitized = DiagnosticMessageSanitizer.CreateSafeText(detail, 160);
        return string.IsNullOrWhiteSpace(sanitized)
            ? "Registration unavailable"
            : sanitized;
    }
}
