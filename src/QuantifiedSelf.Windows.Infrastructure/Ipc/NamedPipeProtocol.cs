using System.Text;
using System.Text.Json;
using QuantifiedSelf.Windows.Core.Ipc;

namespace QuantifiedSelf.Windows.Infrastructure.Ipc;

public static class NamedPipeProtocol
{
    public const int MaxPayloadBytes = 16 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task WriteMessageAsync<T>(
        Stream stream,
        T message,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message, SerializerOptions);
        var payloadBytes = Encoding.UTF8.GetBytes(json);

        if (payloadBytes.Length > MaxPayloadBytes)
        {
            throw new IpcProtocolException(
                "IpcPayloadTooLarge",
                "IPC payload too large.");
        }

        var lengthBytes = BitConverter.GetBytes(payloadBytes.Length);
        await stream.WriteAsync(lengthBytes, cancellationToken);
        await stream.WriteAsync(payloadBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadMessageAsync<T>(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var lengthBytes = new byte[4];
        var bytesRead = await ReadExactAsync(stream, lengthBytes, 4, cancellationToken);

        if (bytesRead < 4)
        {
            throw new IpcProtocolException(
                "IpcProtocolError",
                "IPC protocol error.");
        }

        var payloadLength = BitConverter.ToInt32(lengthBytes, 0);

        if (payloadLength < 0)
        {
            throw new IpcProtocolException(
                "IpcProtocolError",
                "IPC protocol error.");
        }

        if (payloadLength > MaxPayloadBytes)
        {
            throw new IpcProtocolException(
                "IpcPayloadTooLarge",
                "IPC payload too large.");
        }

        var payloadBytes = new byte[payloadLength];
        bytesRead = await ReadExactAsync(stream, payloadBytes, payloadLength, cancellationToken);

        if (bytesRead < payloadLength)
        {
            throw new IpcProtocolException(
                "IpcProtocolError",
                "IPC protocol error.");
        }

        try
        {
            var result = JsonSerializer.Deserialize<T>(payloadBytes, SerializerOptions);
            if (result is null)
            {
                throw new IpcProtocolException(
                    "IpcProtocolError",
                    "IPC protocol error.");
            }
            return result;
        }
        catch (JsonException)
        {
            throw new IpcProtocolException(
                "IpcProtocolError",
                "IPC protocol error.");
        }
    }

    private static async Task<int> ReadExactAsync(
        Stream stream,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, count - totalRead),
                cancellationToken);

            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }
}
