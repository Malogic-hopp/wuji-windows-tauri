using System.Text.Json;
using System.Text.Json.Serialization;
using QuantifiedSelf.Windows.Client.Bridge.Generated;

namespace QuantifiedSelf.Windows.Client.Bridge;

internal static class BridgeProtocol
{
    public const string JsonRpcVersion = "2.0";
    public const string ApiVersion = "1.0";

    public const string HelloMethod = "bridge.hello";
    public const string InitializeMethod = "client.initialize";
    public const string ShutdownMethod = "bridge.shutdown";

    public static readonly IReadOnlyList<string> Capabilities =
    [
        HelloMethod,
        InitializeMethod,
        ShutdownMethod
    ];

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)
        }
    };

    public static bool IsSupportedApiVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf('.');
        var major = separator < 0 ? value : value[..separator];
        return string.Equals(major, "1", StringComparison.Ordinal);
    }

    public static BridgeResponseEnvelope Success<T>(string id, T result)
    {
        return new BridgeResponseEnvelope
        {
            Jsonrpc = JsonRpcVersion,
            Id = id,
            Result = JsonSerializer.SerializeToElement(result, SerializerOptions)
        };
    }

    public static BridgeResponseEnvelope Failure(
        string id,
        string correlationId,
        string code,
        string message,
        BridgeErrorKind kind,
        bool retryable)
    {
        return new BridgeResponseEnvelope
        {
            Jsonrpc = JsonRpcVersion,
            Id = id,
            Error = new BridgeError
            {
                Code = code,
                Message = message,
                Data = new BridgeErrorData
                {
                    Kind = kind,
                    Retryable = retryable,
                    CorrelationId = correlationId
                }
            }
        };
    }
}
