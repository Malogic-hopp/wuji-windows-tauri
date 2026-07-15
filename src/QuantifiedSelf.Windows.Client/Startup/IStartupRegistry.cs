namespace QuantifiedSelf.Windows.Client.Startup;

/// <summary>
/// Abstraction over the Windows Run key for testability.
/// Production implementation uses Microsoft.Win32.Registry.CurrentUser.
/// </summary>
public interface IStartupRegistry
{
    string? ReadValue(string name);

    void SetValue(string name, string command);

    void DeleteValue(string name);
}
