using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Database;

namespace QuantifiedSelf.Windows.App.Services;

/// <summary>
/// Read-only service that aggregates today's activity from app_sessions and foreground_samples
/// into a DailyActivitySummary. All queries are read-only — this service never writes to SQLite.
/// </summary>
public sealed class DailyStatsService
{
    private readonly DailyStatsQueryService _statsQueryService;
    private readonly AppUsageQueryService _appUsageQueryService;

    public DailyStatsService(WindowsAgentPaths paths)
    {
        _statsQueryService = new DailyStatsQueryService(paths.DatabasePath);
        _appUsageQueryService = new AppUsageQueryService(paths.DatabasePath);
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
            DateOnly.FromDateTime(DateTime.Now),
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
            var nowUtc = DateTime.UtcNow;
            var (rangeStartUtc, rangeEndUtc) = DailyStatsQueryService.GetLocalDayRangeUtc(localDate);

            // For past dates, treat end-of-day as "now" so open-ended sessions
            // are clipped to the day boundary rather than stretched to present.
            var effectiveNow = localDate < DateOnly.FromDateTime(DateTime.Now)
                ? rangeEndUtc
                : nowUtc;

            // Fetch overlapping sessions
            var sessions = await _statsQueryService.GetSessionsOverlappingLocalDayAsync(localDate, cancellationToken);

            // Compute overlap-scaled aggregate durations
            long totalDuration = 0;
            long totalActive = 0;
            long totalIdle = 0;
            DateTime? firstSeenUtc = null;
            DateTime? lastSeenUtc = null;

            foreach (var session in sessions)
            {
                var (total, active, idle) = DailyStatsQueryService.ScaleSessionDurations(
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
            var sampleCount = await _statsQueryService.GetSampleCountForLocalDayAsync(localDate, cancellationToken);
            var (sampleFirstUtc, sampleLastUtc) = await _statsQueryService.GetSampleTimeRangeForLocalDayAsync(localDate, cancellationToken);

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
            var topAppsTask = _appUsageQueryService.GetAppUsageForLocalDayAsync(localDate, topAppsLimit, cancellationToken);
            var topWindowsTask = _statsQueryService.GetTopWindowsForLocalDayAsync(localDate, topWindowsLimit, cancellationToken);
            var samplesTask = _statsQueryService.GetSamplesForLocalDayAsync(localDate, cancellationToken);

            await Task.WhenAll(topAppsTask, topWindowsTask, samplesTask);

            var topApps = (await topAppsTask) ?? Array.Empty<AppUsageSummary>();
            var rawTopWindows = (await topWindowsTask) ?? Array.Empty<DailyWindowUsageSummary>();
            var daySamples = (await samplesTask) ?? Array.Empty<ForegroundSample>();

            // Compute focus metrics from active samples
            var focusMetrics = FocusMetricsCalculator.Compute(daySamples);

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
                FragmentedTimeSeconds = focusMetrics.FragmentedTimeSeconds
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
}
