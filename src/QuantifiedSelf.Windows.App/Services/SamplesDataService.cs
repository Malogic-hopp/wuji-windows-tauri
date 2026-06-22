using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Database;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class SamplesDataService
{
    private readonly SampleQueryService _queryService;

    public SamplesDataService(WindowsAgentPaths paths)
    {
        _queryService = new SampleQueryService(paths.DatabasePath);
    }

    public Task<IReadOnlyList<ForegroundSample>> GetRecentSamplesAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        return _queryService.GetRecentSamplesAsync(limit, cancellationToken);
    }
}
