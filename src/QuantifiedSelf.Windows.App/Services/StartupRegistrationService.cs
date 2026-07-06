namespace QuantifiedSelf.Windows.App.Services;

/// <summary>
/// Manages WUJI startup registration through the HKCU Run Key.
/// Uses IStartupRegistry for registry access and StartupCommandBuilder for path/command logic.
/// </summary>
public sealed class StartupRegistrationService : IStartupRegistrationService
{
    private const string ValueName = "WUJI";

    private readonly IStartupRegistry _registry;
    private readonly StartupCommandBuilder _commandBuilder;

    public StartupRegistrationService(
        IStartupRegistry registry,
        StartupCommandBuilder commandBuilder)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _commandBuilder = commandBuilder ?? throw new ArgumentNullException(nameof(commandBuilder));
    }

    /// <summary>
    /// Registers WUJI in the HKCU Run Key.
    /// Idempotent: repeated calls overwrite with the correct command.
    /// Returns UnsupportedInCurrentLaunchMode if the process path is invalid.
    /// Returns Error if an exception occurs during registry access.
    /// </summary>
    public Task<StartupRegistrationStatus> RegisterAsync()
    {
        try
        {
            var command = _commandBuilder.BuildCommand();
            if (command is null)
                return Task.FromResult(StartupRegistrationStatus.Unsupported());

            _registry.SetValue(ValueName, command);
            return Task.FromResult(StartupRegistrationStatus.Enabled());
        }
        catch (Exception)
        {
            return Task.FromResult(StartupRegistrationStatus.RegistrationUnavailable());
        }
    }

    /// <summary>
    /// Removes the WUJI value from the HKCU Run Key.
    /// Idempotent: succeeds even if the value doesn't exist.
    /// Only deletes the WUJI value, never touches other Run Key entries.
    /// Returns Error if the registry call itself throws.
    /// </summary>
    public Task<StartupRegistrationStatus> UnregisterAsync()
    {
        try
        {
            _registry.DeleteValue(ValueName);
            return Task.FromResult(StartupRegistrationStatus.Disabled());
        }
        catch (Exception)
        {
            return Task.FromResult(StartupRegistrationStatus.RegistrationUnavailable());
        }
    }

    /// <summary>
    /// Reads the current HKCU Run Key and analyzes the WUJI value.
    /// Returns Disabled, Enabled, Mismatch, Error, or UnsupportedInCurrentLaunchMode.
    /// Never throws — all failures are captured in the status object.
    /// </summary>
    public Task<StartupRegistrationStatus> GetStatusAsync()
    {
        try
        {
            // If the current process path is invalid, we can't meaningfully compare,
            // but we still check if there's an existing registration.
            if (!_commandBuilder.IsValidProcessPath())
            {
                // If a WUJI value exists, it's a mismatch — we can't verify it.
                var existing = ReadValueSafe();
                if (existing is not null)
                    return Task.FromResult(StartupRegistrationStatus.Mismatch(
                        "Startup entry exists but cannot be verified in the current launch mode."));

                return Task.FromResult(StartupRegistrationStatus.Unsupported());
            }

            var registeredCommand = ReadValueSafe();

            if (registeredCommand is null)
                return Task.FromResult(StartupRegistrationStatus.Disabled());

            // Check if the command matches
            if (_commandBuilder.CommandsMatch(registeredCommand))
                return Task.FromResult(StartupRegistrationStatus.Enabled());

            // Command exists but doesn't match
            return Task.FromResult(StartupRegistrationStatus.RegisteredCommandNeedsRepair());
        }
        catch (Exception)
        {
            return Task.FromResult(StartupRegistrationStatus.RegistrationUnavailable());
        }
    }

    private string? ReadValueSafe()
    {
        return _registry.ReadValue(ValueName);
    }
}
