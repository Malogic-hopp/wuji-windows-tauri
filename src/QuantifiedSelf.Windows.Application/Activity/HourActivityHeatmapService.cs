using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Data;
using QuantifiedSelf.Windows.ApplicationLayer.Analytics;
using QuantifiedSelf.Windows.ApplicationLayer.Contracts.Activity;

namespace QuantifiedSelf.Windows.ApplicationLayer.Activity;

/// <summary>
/// Read-only service that queries 7 days of foreground samples and builds
/// a framework-independent 24h×7d activity heatmap result.
/// </summary>
public sealed class HourActivityHeatmapService : IHourActivityHeatmapService
{
    private readonly IDailyStatsQueryPort _queryPort;
    private readonly TimeProvider _timeProvider;

    public HourActivityHeatmapService(
        IDailyStatsQueryPort queryPort,
        TimeProvider? timeProvider = null)
    {
        _queryPort = queryPort ?? throw new ArgumentNullException(nameof(queryPort));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns pre-computed heatmap data for the past 7 days.
    /// Throws on query failure — the caller decides whether to preserve old data.
    /// </summary>
    public async Task<HourActivityHeatmapResult> GetHeatmapAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
        var startDate = today.AddDays(-6);

        var weekSamples = await _queryPort.GetSamplesForDateRangeAsync(
            startDate, today, cancellationToken);

        var points = HourActivityHeatmapCalculator.Compute(weekSamples, today);

        return new HourActivityHeatmapResult
        {
            Today = today,
            Points = points
        };
    }
}
