using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Data;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.ApplicationLayer.Activity;

public sealed class SamplesDataService : ISamplesDataService
{
    private readonly ISampleQueryPort _queryPort;

    public SamplesDataService(ISampleQueryPort queryPort)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
    }

    public Task<IReadOnlyList<ForegroundSample>> GetRecentSamplesAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return _queryPort.GetRecentSamplesAsync(limit, cancellationToken);
    }
}
