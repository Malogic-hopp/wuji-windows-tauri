using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Database;
using QuantifiedSelf.Windows.App.ViewModels;

namespace QuantifiedSelf.Windows.App.Services;

/// <summary>
/// Read-only service that queries 7 days of foreground samples and builds
/// a 24h×7d activity heatmap ViewModel.
/// </summary>
public sealed class HourActivityHeatmapService
{
    private readonly DailyStatsQueryService _queryService;

    public HourActivityHeatmapService(WindowsAgentPaths paths)
    {
        _queryService = new DailyStatsQueryService(paths.DatabasePath);
    }

    /// <summary>
    /// Returns a pre-computed heatmap ViewModel for the past 7 days.
    /// Throws on query failure — the caller decides whether to preserve old data.
    /// </summary>
    public async Task<HourActivityHeatmapViewModel> GetHeatmapAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var startDate = today.AddDays(-6);

        var weekSamples = await _queryService.GetSamplesForDateRangeAsync(
            startDate, today, cancellationToken);

        var points = HourActivityHeatmapCalculator.Compute(weekSamples, today);

        return new HourActivityHeatmapViewModel(points, today);
    }
}
