namespace QuantifiedSelf.Windows.Client.Startup;

/// <summary>
/// Abstraction over startup registration for testability.
/// </summary>
public interface IStartupRegistrationService
{
    Task<StartupRegistrationStatus> RegisterAsync();
    Task<StartupRegistrationStatus> UnregisterAsync();
    Task<StartupRegistrationStatus> GetStatusAsync();
}
