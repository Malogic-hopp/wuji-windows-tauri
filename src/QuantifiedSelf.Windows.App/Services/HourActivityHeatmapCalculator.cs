using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.App.Services;

/// <summary>
/// Pure-calculation service that aggregates foreground samples into
/// a 24-hour × 7-day activity matrix for the heatmap.
/// All time conversions happen in C# (UTC → local), not in SQL.
/// </summary>
public static class HourActivityHeatmapCalculator
{
    /// <summary>
    /// Computes (date, hour) activity buckets from a week's worth of foreground samples.
    /// Returns one point per (date, hour) combination — missing buckets are filled with zero.
    /// </summary>
    /// <param name="weekSamples">All foreground samples from the 7-day window.</param>
    /// <param name="today">Today's local date.</param>
    /// <returns>168 points (7 days × 24 hours), ordered by date ASC then hour ASC.</returns>
    public static IReadOnlyList<DailyHourActivityPoint> Compute(
        IReadOnlyList<ForegroundSample> weekSamples,
        DateOnly today)
    {
        // Build the 7-day date list: today-6 ... today
        var dates = Enumerable.Range(0, 7)
            .Select(i => today.AddDays(i - 6))
            .ToList();

        // Bucket samples by (localDate, localHour)
        var buckets = new Dictionary<(string Date, int Hour), (int Active, int Idle, int Unknown)>();

        foreach (var sample in weekSamples)
        {
            var localTime = sample.SampleTimeUtc.ToLocalTime();
            var dateKey = localTime.ToString("yyyy-MM-dd");
            var hour = localTime.Hour;

            var key = (dateKey, hour);
            if (!buckets.TryGetValue(key, out var counts))
            {
                counts = (0, 0, 0);
            }

            var state = sample.ActivityState?.Trim() ?? string.Empty;
            if (string.Equals(state, "Active", StringComparison.OrdinalIgnoreCase))
                counts.Active++;
            else if (string.Equals(state, "Idle", StringComparison.OrdinalIgnoreCase))
                counts.Idle++;
            else
                counts.Unknown++;

            buckets[key] = counts;
        }

        // Build the full 24×7 matrix (fill missing with zero)
        var result = new List<DailyHourActivityPoint>(168);
        foreach (var date in dates)
        {
            var dateStr = date.ToString("yyyy-MM-dd");
            for (var hour = 0; hour < 24; hour++)
            {
                var key = (dateStr, hour);
                buckets.TryGetValue(key, out var counts);

                result.Add(new DailyHourActivityPoint
                {
                    Date = dateStr,
                    Hour = hour,
                    ActiveSamples = counts.Active,
                    IdleSamples = counts.Idle,
                    UnknownSamples = counts.Unknown
                });
            }
        }

        // Compute ActiveIntensity for heatmap coloring: normalize against the
        // busiest hour so the color reflects activity volume, not within-hour ratio.
        var maxActive = result.Max(p => p.ActiveSamples);
        if (maxActive > 0)
        {
            foreach (var point in result)
            {
                point.ActiveIntensity = (double)point.ActiveSamples / maxActive;
            }
        }

        return result;
    }
}
