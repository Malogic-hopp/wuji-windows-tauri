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

    public async Task HandleSampleAsync(
        ForegroundSample sample,
        int deltaSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var openSession = await _sessionRepository.GetOpenSessionAsync(cancellationToken);
        if (openSession is null)
        {
            await _sessionRepository.StartSessionAsync(sample, deltaSeconds, cancellationToken);
            return;
        }

        if (string.Equals(openSession.ProcessName, sample.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            await _sessionRepository.ExtendOpenSessionAsync(sample, deltaSeconds, cancellationToken);
            return;
        }

        await _sessionRepository.CloseOpenSessionAsync("ProcessChanged", cancellationToken);
        await _sessionRepository.StartSessionAsync(sample, deltaSeconds, cancellationToken);
    }

    public Task CloseOpenSessionAsync(string reason, CancellationToken cancellationToken = default)
    {
        return _sessionRepository.CloseOpenSessionAsync(reason, cancellationToken);
    }
}
