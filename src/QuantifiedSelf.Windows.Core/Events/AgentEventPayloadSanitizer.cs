using System.Text.Json;
using QuantifiedSelf.Windows.Core.Serialization;

namespace QuantifiedSelf.Windows.Core.Events;

public static class AgentEventPayloadSanitizer
{
    public static string? CreatePayloadJson(IReadOnlyDictionary<string, object?>? payload, params string[] allowedKeys)
    {
        if (payload is null || payload.Count == 0 || allowedKeys.Length == 0)
        {
            return null;
        }

        var allowedSet = new HashSet<string>(allowedKeys, StringComparer.Ordinal);
        var sanitized = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in payload)
        {
            if (!allowedSet.Contains(key))
            {
                continue;
            }

            sanitized[key] = value switch
            {
                string text => DiagnosticMessageSanitizer.CreateSafeText(text, 240),
                _ => value
            };
        }

        return sanitized.Count == 0
            ? null
            : JsonSerializer.Serialize(sanitized, JsonSerializationOptions.Default);
    }
}
