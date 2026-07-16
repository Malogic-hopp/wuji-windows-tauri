namespace QuantifiedSelf.Windows.Client.Bridge;

internal sealed class BridgeHostOptions
{
    public const int DefaultMaxPayloadBytes = 1024 * 1024;

    public int MaxPayloadBytes { get; init; } = DefaultMaxPayloadBytes;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public void Validate()
    {
        if (MaxPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadBytes));
        }

        if (RequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        }
    }
}
