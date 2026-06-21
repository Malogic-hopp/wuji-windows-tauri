using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Infrastructure.Database;

namespace QuantifiedSelf.Windows.Agent.Services;

public sealed class SessionAggregator
{
    private readonly AppSessionRepository _sessionRepository;

    public SessionAggregator(AppSessionRepository sessionRepository)
    {
        _sessionRepository = sessionRepository;
    }

    public async Task<SessionAggregationResult> HandleSampleAsync(
        ForegroundSample sample,
        int deltaSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var openSession = await _sessionRepository.GetOpenSessionAsync(cancellationToken);
        if (openSession is null)
        {
            var startedSession = await _sessionRepository.StartSessionAsync(sample, deltaSeconds, cancellationToken);
            return new SessionAggregationResult
            {
                StartedSession = startedSession
            };
        }

        if (string.Equals(openSession.ProcessName, sample.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            await _sessionRepository.ExtendOpenSessionAsync(sample, deltaSeconds, cancellationToken);
            return new SessionAggregationResult();
        }

        var closedSession = await _sessionRepository.CloseOpenSessionAsync("ProcessChanged", cancellationToken);
        var started = await _sessionRepository.StartSessionAsync(sample, deltaSeconds, cancellationToken);
        return new SessionAggregationResult
        {
            ClosedSession = closedSession,
            StartedSession = started,
            CloseReason = "ProcessChanged"
        };
    }

    public async Task<SessionAggregationResult> CloseOpenSessionAsync(string reason, CancellationToken cancellationToken = default)
    {
        var closedSession = await _sessionRepository.CloseOpenSessionAsync(reason, cancellationToken);
        return new SessionAggregationResult
        {
            ClosedSession = closedSession,
            CloseReason = reason
        };
    }
}
