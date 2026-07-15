namespace QuantifiedSelf.Windows.Client.Startup;

public enum StartupRegistrationState
{
    Disabled,
    Enabled,
    Mismatch,
    Error,
    UnsupportedInCurrentLaunchMode
}

/// <summary>
/// Pure model describing the current startup registration state.
/// All text fields are safe for UI display — no paths, SIDs, or raw exception text.
/// </summary>
public sealed class StartupRegistrationStatus
{
    public StartupRegistrationState State { get; init; } = StartupRegistrationState.Disabled;

    /// <summary>
    /// Safe display text, e.g. "Enabled", "Disabled", "Mismatch", "Error".
    /// </summary>
    public string StatusText { get; init; } = "Login startup: Disabled";

    /// <summary>
    /// Safe diagnostic detail. Never contains full paths, SIDs, or raw exception text.
    /// </summary>
    public string? DetailText { get; init; }

    // ── Factory helpers ──

    public static StartupRegistrationStatus Disabled() => new()
    {
        State = StartupRegistrationState.Disabled,
        StatusText = "Login startup: Disabled"
    };

    public static StartupRegistrationStatus Enabled() => new()
    {
        State = StartupRegistrationState.Enabled,
        StatusText = "Login startup: Enabled"
    };

    public static StartupRegistrationStatus Mismatch(string detail) => new()
    {
        State = StartupRegistrationState.Mismatch,
        StatusText = "Login startup: Mismatch",
        DetailText = detail
    };

    public static StartupRegistrationStatus Error(string detail) => new()
    {
        State = StartupRegistrationState.Error,
        StatusText = "Login startup: Error",
        DetailText = detail
    };

    public static StartupRegistrationStatus Unsupported() => new()
    {
        State = StartupRegistrationState.UnsupportedInCurrentLaunchMode,
        StatusText = "Login startup: Unavailable",
        DetailText = "Startup registration is not available in the current launch mode."
    };

    public static StartupRegistrationStatus RegisteredCommandNeedsRepair() => Mismatch(
        "Registered command needs repair.");

    public static StartupRegistrationStatus RegistrationUnavailable() => Error(
        "Registration unavailable.");
}
