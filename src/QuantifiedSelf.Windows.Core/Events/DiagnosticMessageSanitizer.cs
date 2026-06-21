using System.Text.RegularExpressions;

namespace QuantifiedSelf.Windows.Core.Events;

public static class DiagnosticMessageSanitizer
{
    private static readonly Regex WindowsPathPattern = new(
        @"(?<!\w)(?:[A-Za-z]:[\\/]|\\\\)[^\r\n""<>|]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ControlCharactersPattern = new(
        @"[\u0000-\u001F\u007F]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string CreateSafeText(string? value, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = RedactPaths(value.Trim());
        sanitized = ControlCharactersPattern.Replace(sanitized, " ");
        sanitized = WhitespacePattern.Replace(sanitized, " ").Trim();

        if (sanitized.Length > maxLength)
        {
            sanitized = sanitized[..maxLength].TrimEnd();
        }

        return sanitized;
    }

    public static string CreateSafeExceptionMessage(Exception exception, int maxLength = 160)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = CreateSafeText(exception.Message, maxLength);
        return string.IsNullOrWhiteSpace(message)
            ? exception.GetType().Name
            : message;
    }

    private static string RedactPaths(string value)
    {
        return WindowsPathPattern.Replace(value, "<path>");
    }
}
