using System.IO;

namespace QuantifiedSelf.Windows.App.Services;

/// <summary>
/// Builds and parses HKCU Run Key commands for WUJI startup registration.
/// Supports injectable process path for testability.
/// </summary>
public sealed class StartupCommandBuilder
{
    private readonly Func<string?> _getProcessPath;

    public static readonly string[] RequiredArgs = ["--from-autostart", "--start-hidden"];

    /// <summary>
    /// Production constructor using Environment.ProcessPath.
    /// </summary>
    public StartupCommandBuilder()
        : this(() => Environment.ProcessPath) { }

    /// <summary>
    /// Test constructor with injectable process path provider.
    /// </summary>
    public StartupCommandBuilder(Func<string?> getProcessPath)
    {
        _getProcessPath = getProcessPath ?? throw new ArgumentNullException(nameof(getProcessPath));
    }

    /// <summary>
    /// Returns true if the current process path is a valid WUJI App exe.
    /// Rejects dotnet.exe, .dll, empty, or non-exe paths.
    /// </summary>
    public bool IsValidProcessPath()
    {
        var path = _getProcessPath();
        return IsValidExecutablePath(path);
    }

    /// <summary>
    /// Builds the Run Key command string, or returns null if the path is invalid.
    /// </summary>
    public string? BuildCommand()
    {
        var path = _getProcessPath();
        if (!IsValidExecutablePath(path))
            return null;

        return $"\"{path}\" --from-autostart --start-hidden";
    }

    /// <summary>
    /// Normalizes an exe path by trimming quotes and normalizing directory separators.
    /// Returns the original string if null/empty.
    /// </summary>
    public static string? NormalizeExePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var trimmed = path.Trim().Trim('"').Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        return trimmed.Replace('/', '\\');
    }

    /// <summary>
    /// Extracts the executable path from a registered Run Key command.
    /// Handles quoted and unquoted first tokens.
    /// Returns null if the command is unparseable.
    /// </summary>
    public static string? ExtractExePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var trimmed = command.Trim();

        // Quoted exe path: "C:\path\to\app.exe" args...
        if (trimmed.StartsWith('"'))
        {
            var closeQuote = trimmed.IndexOf('"', 1);
            if (closeQuote < 0)
                return null;
            return trimmed[1..closeQuote];
        }

        // Unquoted: first space-delimited token
        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }

    /// <summary>
    /// Checks whether the registered command matches the current app exe and
    /// includes all required startup arguments.
    /// Supports case-insensitive argument matching and whitespace/quote tolerance.
    /// </summary>
    public bool CommandsMatch(string? registeredCommand)
    {
        if (string.IsNullOrWhiteSpace(registeredCommand))
            return false;

        var currentPath = NormalizeExePath(_getProcessPath());
        if (string.IsNullOrWhiteSpace(currentPath))
            return false;

        var registeredExe = NormalizeExePath(ExtractExePath(registeredCommand));
        if (string.IsNullOrWhiteSpace(registeredExe))
            return false;

        // Compare normalized exe paths case-insensitively
        if (!string.Equals(currentPath, registeredExe, StringComparison.OrdinalIgnoreCase))
            return false;

        // Extract argument tokens from the args portion (after the exe path)
        // and check that each required arg is present as an exact token (case-insensitive).
        var argsTokens = ExtractArgTokens(registeredCommand);
        foreach (var required in RequiredArgs)
        {
            if (!argsTokens.Contains(required, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Extracts the argument tokens from a Run Key command string.
    /// Splits on whitespace after skipping the exe path (quoted or unquoted).
    /// </summary>
    private static string[] ExtractArgTokens(string command)
    {
        var trimmed = command.Trim();

        // Skip past the exe path
        var argsStart = 0;
        if (trimmed.StartsWith('"'))
        {
            var closeQuote = trimmed.IndexOf('"', 1);
            argsStart = closeQuote >= 0 ? closeQuote + 1 : 0;
        }
        else
        {
            var space = trimmed.IndexOf(' ');
            argsStart = space >= 0 ? space : trimmed.Length;
        }

        var argsPart = trimmed[argsStart..].Trim();
        if (string.IsNullOrWhiteSpace(argsPart))
            return [];

        // Split on whitespace, keeping only non-empty tokens
        return argsPart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsValidExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        // Reject dotnet.exe host
        if (fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        // Must end with .exe (reject .dll, empty extension, etc.)
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }
}
