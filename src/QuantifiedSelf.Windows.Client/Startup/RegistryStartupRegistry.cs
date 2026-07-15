using Microsoft.Win32;

namespace QuantifiedSelf.Windows.Client.Startup;

/// <summary>
/// Production IStartupRegistry implementation that reads/writes the current user's Run key.
/// </summary>
public sealed class RegistryStartupRegistry : IStartupRegistry
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? ReadValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        if (key is null)
            return null;
        return key.GetValue(name) as string;
    }

    public void SetValue(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(name, command);
    }

    public void DeleteValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
            return;

        // Only delete if the value actually exists; do nothing otherwise (idempotent).
        if (key.GetValue(name) is not null)
            key.DeleteValue(name, throwOnMissingValue: false);
    }
}
