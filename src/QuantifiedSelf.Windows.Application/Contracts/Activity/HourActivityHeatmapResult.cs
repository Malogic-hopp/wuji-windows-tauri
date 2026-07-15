using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.ApplicationLayer.Contracts.Activity;

public sealed class HourActivityHeatmapResult
{
    public DateOnly Today { get; init; }

    public IReadOnlyList<DailyHourActivityPoint> Points { get; init; } = [];
}
