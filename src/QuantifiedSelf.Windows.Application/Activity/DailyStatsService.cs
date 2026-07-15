using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Data;
using QuantifiedSelf.Windows.ApplicationLayer.Analytics;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.ApplicationLayer.Activity;

/// <summary>
/// Read-only service that aggregates today's activity from app_sessions and foreground_samples
/// into a DailyActivitySummary. All queries are read-only — this service never writes to SQLite.
/// </summary>
public sealed class DailyStatsService : IDailyStatsService
{
    private readonly IDailyStatsQueryPort _statsQueryPort;
    private readonly IAppUsageQueryPort _appUsageQueryPort;
    private readonly TimeProvider _timeProvider;

    public DailyStatsService(
        IDailyStatsQueryPort statsQueryPort,
        IAppUsageQueryPort appUsageQueryPort,
        TimeProvider? timeProvider = null)
    {
        _statsQueryPort = statsQueryPort ?? throw new ArgumentNullException(nameof(statsQueryPort));
        _appUsageQueryPort = appUsageQueryPort ?? throw new ArgumentNullException(nameof(appUsageQueryPort));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns a full daily activity summary for today, including durations, top apps, and top windows.
    /// When there is no data for today, returns an empty summary (all durations zero, empty lists).
    /// Query errors are caught and returned as an empty summary with no exception thrown.
    /// </summary>
    public Task<DailyActivitySummary> GetTodaySummaryAsync(
        int topAppsLimit = 5,
        int topWindowsLimit = 10,
        CancellationToken cancellationToken = default)
    {
        return GetSummaryForDateAsync(
            DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime),
            topAppsLimit,
            topWindowsLimit,
            cancellationToken);
    }

    /// <summary>
    /// Returns a full daily activity summary for the given local date.
    /// For past dates, uses the end-of-day UTC as the effective "now" for open-session scaling.
    /// For today, uses DateTime.UtcNow so partial-day sessions are scaled correctly.
    /// </summary>
    public async Task<DailyActivitySummary> GetSummaryForDateAsync(
        DateOnly localDate,
        int topAppsLimit = 5,
        int topWindowsLimit = 10,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var (rangeStartUtc, rangeEndUtc) = GetLocalDayRangeUtc(localDate);

            // For past dates, treat end-of-day as "now" so open-ended sessions
            // are clipped to the day boundary rather than stretched to present.
            var effectiveNow = localDate < DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime)
                ? rangeEndUtc
                : nowUtc;

            // Fetch overlapping sessions
            var sessions = await _statsQueryPort.GetSessionsOverlappingLocalDayAsync(localDate, cancellationToken);

            // Compute overlap-scaled aggregate durations
            long totalDuration = 0;
            long totalActive = 0;
            long totalIdle = 0;
            DateTime? firstSeenUtc = null;
            DateTime? lastSeenUtc = null;

            foreach (var session in sessions)
            {
                var (total, active, idle) = ScaleSessionDurations(
                    session, rangeStartUtc, rangeEndUtc, effectiveNow);

                totalDuration += total;
                totalActive += active;
                totalIdle += idle;

                var sessionStart = session.StartedAtUtc.ToUniversalTime();
                var sessionEnd = session.EndedAtUtc?.ToUniversalTime() ?? effectiveNow;
                var overlapStart = sessionStart > rangeStartUtc ? sessionStart : rangeStartUtc;
                var overlapEnd = sessionEnd < rangeEndUtc ? sessionEnd : rangeEndUtc;
                if (overlapEnd > effectiveNow)
                {
                    overlapEnd = effectiveNow;
                }

                if (overlapEnd <= overlapStart)
                {
                    continue;
                }

                if (!firstSeenUtc.HasValue || overlapStart < firstSeenUtc.Value)
                {
                    firstSeenUtc = overlapStart;
                }

                if (!lastSeenUtc.HasValue || overlapEnd > lastSeenUtc.Value)
                {
                    lastSeenUtc = overlapEnd;
                }
            }

            // Fetch sample count and sample time range
            var sampleCount = await _statsQueryPort.GetSampleCountForLocalDayAsync(localDate, cancellationToken);
            var (sampleFirstUtc, sampleLastUtc) = await _statsQueryPort.GetSampleTimeRangeForLocalDayAsync(localDate, cancellationToken);

            // Merge sample time range into first/last seen
            if (sampleFirstUtc.HasValue && (!firstSeenUtc.HasValue || sampleFirstUtc.Value < firstSeenUtc.Value))
            {
                firstSeenUtc = sampleFirstUtc.Value;
            }

            if (sampleLastUtc.HasValue && (!lastSeenUtc.HasValue || sampleLastUtc.Value > lastSeenUtc.Value))
            {
                lastSeenUtc = sampleLastUtc.Value;
            }

            // Fetch top apps, top windows, and samples (run in parallel for efficiency)
            var topAppsTask = _appUsageQueryPort.GetAppUsageForLocalDayAsync(localDate, topAppsLimit, cancellationToken);
            var topWindowsTask = _statsQueryPort.GetTopWindowsForLocalDayAsync(localDate, topWindowsLimit, cancellationToken);
            var samplesTask = _statsQueryPort.GetSamplesForLocalDayAsync(localDate, cancellationToken);

            await Task.WhenAll(topAppsTask, topWindowsTask, samplesTask);

            var topApps = (await topAppsTask) ?? Array.Empty<AppUsageSummary>();
            var rawTopWindows = (await topWindowsTask) ?? Array.Empty<DailyWindowUsageSummary>();
            var daySamples = (await samplesTask) ?? Array.Empty<ForegroundSample>();

            // Compute focus metrics from active samples
            var focusMetrics = FocusMetricsCalculator.Compute(daySamples);

            // Compute hourly activity breakdown (sample-gap durations capped at 60s)
            var hourlyActivity = ComputeHourlyActivity(daySamples);

            // Apply privacy filtering to window titles
            var topWindows = rawTopWindows.Select(w => new DailyWindowUsageSummary
            {
                WindowTitle = w.WindowTitle,
                ProcessName = w.ProcessName,
                SampleCount = w.SampleCount,
                SafeWindowTitle = string.IsNullOrWhiteSpace(w.WindowTitle)
                    ? string.Empty
                    : DiagnosticMessageSanitizer.CreateSafeText(w.WindowTitle, 120)
            }).ToList();

            return new DailyActivitySummary
            {
                Date = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local),
                TotalDurationSeconds = totalDuration,
                TotalActiveDurationSeconds = totalActive,
                TotalIdleDurationSeconds = totalIdle,
                SampleCount = sampleCount,
                SessionCount = sessions.Count,
                FirstSeenAtUtc = firstSeenUtc,
                LastSeenAtUtc = lastSeenUtc,
                TopApps = [.. topApps],
                TopWindows = topWindows,
                ContextSwitchCount = focusMetrics.ContextSwitchCount,
                RawContextSwitchCount = focusMetrics.RawContextSwitchCount,
                LongestFocusSession = focusMetrics.LongestFocusSession,
                FocusSessionCount = focusMetrics.FocusSessionCount,
                FragmentedTimeSeconds = focusMetrics.FragmentedTimeSeconds,
                HourlyActivity = hourlyActivity
            };
        }
        catch
        {
            // Return empty summary on any query error — no raw paths, SQL, or stack traces leaked.
            return new DailyActivitySummary
            {
                Date = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)
            };
        }
    }

    internal static (DateTime StartUtc, DateTime EndUtc) GetLocalDayRangeUtc(DateOnly localDate)
    {
        var localStart = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        return (localStart.ToUniversalTime(), localStart.AddDays(1).ToUniversalTime());
    }

    internal static (long Total, long Active, long Idle) ScaleSessionDurations(
        AppSession session,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        DateTime nowUtc)
    {
        var sessionStart = session.StartedAtUtc.ToUniversalTime();
        var sessionEnd = (session.EndedAtUtc ?? nowUtc).ToUniversalTime();
        var overlapStart = sessionStart > rangeStartUtc ? sessionStart : rangeStartUtc;
        var overlapEnd = sessionEnd < rangeEndUtc ? sessionEnd : rangeEndUtc;
        var overlapSeconds = Math.Max(0, (overlapEnd - overlapStart).TotalSeconds);
        var sessionSpanSeconds = Math.Max(1.0, (sessionEnd - sessionStart).TotalSeconds);
        var scale = overlapSeconds / sessionSpanSeconds;

        return (
            Total: (long)Math.Round(session.TotalDurationSeconds * scale, MidpointRounding.AwayFromZero),
            Active: (long)Math.Round(session.ActiveDurationSeconds * scale, MidpointRounding.AwayFromZero),
            Idle: (long)Math.Round(session.IdleDurationSeconds * scale, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Computes hour-by-hour activity durations from foreground samples.
    /// Each sample-to-next-sample gap is attributed to the earlier sample's state,
    /// capped at 60 seconds (the typical agent sample interval).
    /// Gaps that cross hour boundaries are split proportionally.
    /// </summary>
    private static IReadOnlyList<HourlyActivity> ComputeHourlyActivity(IReadOnlyList<ForegroundSample> daySamples)
    {
        const double maxGapSeconds = 60.0;

        // Initialize 24 empty hours
        var buckets = new (double Active, double Idle, double Unknown)[24];

        if (daySamples.Count == 0)
        {
            return Enumerable.Range(0, 24)
                .Select(h => new HourlyActivity(h, 0, 0, 0))
                .ToList();
        }

        // Sort by sample time (should already be sorted from SQL)
        var sorted = daySamples.OrderBy(s => s.SampleTimeUtc).ToList();

        for (var i = 0; i < sorted.Count; i++)
        {
            var sample = sorted[i];
            var localTime = sample.SampleTimeUtc.ToLocalTime();
            var sampleDate = DateOnly.FromDateTime(localTime);

            // Compute gap to next sample, or use 1s for the last sample
            double gapSeconds;
            if (i < sorted.Count - 1)
            {
                var nextSampleTime = sorted[i + 1].SampleTimeUtc;
                gapSeconds = Math.Min((nextSampleTime - sample.SampleTimeUtc).TotalSeconds, maxGapSeconds);
                if (gapSeconds < 0) gapSeconds = 0;
            }
            else
            {
                gapSeconds = 1.0; // last sample: count as 1 second
            }

            var state = sample.ActivityState?.Trim() ?? string.Empty;

            // Distribute gap across hour boundaries (and stop at day boundary)
            var remaining = gapSeconds;
            var cursor = localTime;
            while (remaining > 0.001)
            {
                var hour = cursor.Hour;
                if (DateOnly.FromDateTime(cursor) != sampleDate) break; // stop at day boundary

                var secondsToNextHour = 3600.0
                    - (cursor.Minute * 60 + cursor.Second + cursor.Millisecond / 1000.0);
                var portion = Math.Min(remaining, secondsToNextHour);

                if (string.Equals(state, "Active", StringComparison.OrdinalIgnoreCase))
                    buckets[hour].Active += portion;
                else if (string.Equals(state, "Idle", StringComparison.OrdinalIgnoreCase))
                    buckets[hour].Idle += portion;
                else
                    buckets[hour].Unknown += portion;

                remaining -= portion;
                cursor = cursor.AddSeconds(portion);
            }
        }

        return Enumerable.Range(0, 24)
            .Select(h => new HourlyActivity(
                h,
                Math.Round(buckets[h].Active, 1),
                Math.Round(buckets[h].Idle, 1),
                Math.Round(buckets[h].Unknown, 1)))
            .ToList();
    }
}
