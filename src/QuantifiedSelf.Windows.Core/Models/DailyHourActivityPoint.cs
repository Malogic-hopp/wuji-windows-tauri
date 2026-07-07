namespace QuantifiedSelf.Windows.Core.Models;

/// <summary>
/// A single (date, hour) bucket in the 24h×7d activity heatmap.
/// Aggregated from foreground_samples grouped by local date and hour.
/// </summary>
public sealed class DailyHourActivityPoint
{
    /// <summary>
    /// Local date string, e.g. "2026-07-06".
    /// </summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>
    /// Local hour of day, 0–23.
    /// </summary>
    public int Hour { get; set; }

    /// <summary>
    /// Number of active-state samples in this bucket.
    /// </summary>
    public int ActiveSamples { get; set; }

    /// <summary>
    /// Number of idle-state samples in this bucket.
    /// </summary>
    public int IdleSamples { get; set; }

    /// <summary>
    /// Number of unknown-state samples in this bucket.
    /// </summary>
    public int UnknownSamples { get; set; }

    /// <summary>
    /// Total samples in this bucket.
    /// </summary>
    public int TotalSamples => ActiveSamples + IdleSamples + UnknownSamples;

    /// <summary>
    /// Fraction of samples that are active, 0.0–1.0.
    /// Returns 0 when there are no samples in this bucket.
    /// </summary>
    public double ActiveRatio => TotalSamples > 0
        ? (double)ActiveSamples / TotalSamples
        : 0.0;

    /// <summary>
    /// Normalized intensity for heatmap coloring: ActiveSamples / maxActiveInPeriod.
    /// This makes color reflect "how active this hour is" relative to the busiest
    /// hour in the 7-day window, instead of the proportion of active samples within
    /// this single hour. Set by the calculator after all buckets are built.
    /// </summary>
    public double ActiveIntensity { get; set; }
}
