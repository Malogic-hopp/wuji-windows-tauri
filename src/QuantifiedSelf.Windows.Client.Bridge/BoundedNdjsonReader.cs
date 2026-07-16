using System.Text;

namespace QuantifiedSelf.Windows.Client.Bridge;

internal sealed class BoundedNdjsonReader
{
    private readonly Stream _stream;
    private readonly int _maxPayloadBytes;
    private readonly byte[] _buffer = new byte[4096];
    private int _bufferOffset;
    private int _bufferCount;

    public BoundedNdjsonReader(Stream stream, int maxPayloadBytes)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (maxPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
        }

        _maxPayloadBytes = maxPayloadBytes;
    }

    public async Task<NdjsonReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream(capacity: Math.Min(_maxPayloadBytes, 4096));
        var oversized = false;

        while (true)
        {
            if (_bufferOffset >= _bufferCount)
            {
                _bufferCount = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                _bufferOffset = 0;
                if (_bufferCount == 0)
                {
                    if (oversized)
                    {
                        return NdjsonReadResult.PayloadTooLarge();
                    }

                    return message.Length == 0
                        ? NdjsonReadResult.EndOfStream()
                        : Decode(message);
                }
            }

            var value = _buffer[_bufferOffset++];
            if (value == (byte)'\n')
            {
                if (oversized)
                {
                    return NdjsonReadResult.PayloadTooLarge();
                }

                if (message.Length > 0 && message.GetBuffer()[message.Length - 1] == (byte)'\r')
                {
                    message.SetLength(message.Length - 1);
                }

                return Decode(message);
            }

            if (oversized)
            {
                continue;
            }

            if (message.Length >= _maxPayloadBytes)
            {
                oversized = true;
                continue;
            }

            message.WriteByte(value);
        }
    }

    private static NdjsonReadResult Decode(MemoryStream message)
    {
        try
        {
            var encoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);
            return NdjsonReadResult.Message(encoding.GetString(message.GetBuffer(), 0, checked((int)message.Length)));
        }
        catch (DecoderFallbackException)
        {
            return NdjsonReadResult.InvalidEncoding();
        }
    }
}

internal readonly record struct NdjsonReadResult(
    string? Line,
    bool IsEndOfStream,
    bool IsPayloadTooLarge,
    bool HasInvalidEncoding)
{
    public static NdjsonReadResult Message(string line) => new(line, false, false, false);

    public static NdjsonReadResult EndOfStream() => new(null, true, false, false);

    public static NdjsonReadResult PayloadTooLarge() => new(null, false, true, false);

    public static NdjsonReadResult InvalidEncoding() => new(null, false, false, true);
}
