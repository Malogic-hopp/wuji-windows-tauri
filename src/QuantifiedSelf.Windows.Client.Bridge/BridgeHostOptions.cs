namespace QuantifiedSelf.Windows.Client.Bridge;

internal sealed class BridgeHostOptions
{
    public const int DefaultMaxPayloadBytes = 1024 * 1024;

    public int MaxPayloadBytes { get; init; } = DefaultMaxPayloadBytes;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan AgentStatusTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan AgentStartTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan AgentPauseTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan AgentResumeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan AgentStopTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan ActivityOverviewTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan CompletedRequestTtl { get; init; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (MaxPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadBytes));
        }

        if (RequestTimeout <= TimeSpan.Zero
            || AgentStatusTimeout <= TimeSpan.Zero
            || AgentStartTimeout <= TimeSpan.Zero
            || AgentPauseTimeout <= TimeSpan.Zero
            || AgentResumeTimeout <= TimeSpan.Zero
            || AgentStopTimeout <= TimeSpan.Zero
            || ActivityOverviewTimeout <= TimeSpan.Zero
            || CompletedRequestTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "Bridge timeouts must be positive.");
        }
    }

    public TimeSpan GetRequestTimeout(string method) => method switch
    {
        BridgeProtocol.AgentGetStatusMethod => AgentStatusTimeout,
        BridgeProtocol.AgentStartMethod => AgentStartTimeout,
        BridgeProtocol.AgentPauseMethod => AgentPauseTimeout,
        BridgeProtocol.AgentResumeMethod => AgentResumeTimeout,
        BridgeProtocol.AgentStopMethod => AgentStopTimeout,
        BridgeProtocol.ActivityGetOverviewMethod => ActivityOverviewTimeout,
        _ => RequestTimeout
    };
}
