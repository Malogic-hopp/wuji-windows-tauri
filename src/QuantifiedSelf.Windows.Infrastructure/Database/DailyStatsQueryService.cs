using System.Globalization;
using Microsoft.Data.Sqlite;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

/// <summary>
/// Read-only query service that aggregates today's activity from app_sessions and foreground_samples.
/// All duration calculations use overlap-scaling to the local calendar day, consistent with
/// OverviewQueryService and AppUsageQueryService.
/// </summary>
public sealed class DailyStatsQueryService
{
    private readonly string _databasePath;

    public DailyStatsQueryService(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>
    /// Returns the total number of foreground samples recorded for the given local date.
    /// </summary>
    public async Task<long> GetSampleCountForLocalDayAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            return 0;
        }

        var range = DataViewQueryHelpers.GetLocalDayRangeUtc(localDate);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(_databasePath, cancellationToken);
        if (!await DataViewQueryHelpers.TableExistsAsync(connection, "foreground_samples", cancellationToken))
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM foreground_samples
            WHERE sample_time_utc >= $start_utc
              AND sample_time_utc < $end_utc;
            """;

        command.Parameters.AddWithValue("$start_utc", DataViewQueryHelpers.ToDbDateTime(range.StartUtc));
        command.Parameters.AddWithValue("$end_utc", DataViewQueryHelpers.ToDbDateTime(range.EndUtc));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the earliest and latest sample times for the given local date.
    /// </summary>
    public async Task<(DateTime? FirstSeenUtc, DateTime? LastSeenUtc)> GetSampleTimeRangeForLocalDayAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            return (null, null);
        }

        var range = DataViewQueryHelpers.GetLocalDayRangeUtc(localDate);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(_databasePath, cancellationToken);
        if (!await DataViewQueryHelpers.TableExistsAsync(connection, "foreground_samples", cancellationToken))
        {
            return (null, null);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                MIN(sample_time_utc),
                MAX(sample_time_utc)
            FROM foreground_samples
            WHERE sample_time_utc >= $start_utc
              AND sample_time_utc < $end_utc;
            """;

        command.Parameters.AddWithValue("$start_utc", DataViewQueryHelpers.ToDbDateTime(range.StartUtc));
        command.Parameters.AddWithValue("$end_utc", DataViewQueryHelpers.ToDbDateTime(range.EndUtc));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            var first = reader.IsDBNull(0) ? null : (DateTime?)DataViewQueryHelpers.ParseDbDateTime(reader.GetString(0));
            var last = reader.IsDBNull(1) ? null : (DateTime?)DataViewQueryHelpers.ParseDbDateTime(reader.GetString(1));
            return (first, last);
        }

        return (null, null);
    }

    /// <summary>
    /// Returns the top window titles (grouped by process_name + window_title) from today's foreground_samples.
    /// Ordered by sample count descending, then window title ascending (case-insensitive).
    /// </summary>
    public async Task<IReadOnlyList<DailyWindowUsageSummary>> GetTopWindowsForLocalDayAsync(
        DateOnly localDate,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            return Array.Empty<DailyWindowUsageSummary>();
        }

        limit = DataViewQueryHelpers.NormalizeLimit(limit);

        var range = DataViewQueryHelpers.GetLocalDayRangeUtc(localDate);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(_databasePath, cancellationToken);
        if (!await DataViewQueryHelpers.TableExistsAsync(connection, "foreground_samples", cancellationToken))
        {
            return Array.Empty<DailyWindowUsageSummary>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                COALESCE(window_title, '') AS window_title,
                process_name,
                COUNT(*) AS sample_count
            FROM foreground_samples
            WHERE sample_time_utc >= $start_utc
              AND sample_time_utc < $end_utc
            GROUP BY COALESCE(window_title, ''), process_name
            ORDER BY
                sample_count DESC,
                window_title COLLATE NOCASE ASC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$start_utc", DataViewQueryHelpers.ToDbDateTime(range.StartUtc));
        command.Parameters.AddWithValue("$end_utc", DataViewQueryHelpers.ToDbDateTime(range.EndUtc));
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<DailyWindowUsageSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DailyWindowUsageSummary
            {
                WindowTitle = reader.GetString(0),
                ProcessName = reader.GetString(1),
                SampleCount = reader.GetInt32(2)
            });
        }

        return results;
    }

    /// <summary>
    /// Reads sessions overlapping the given local date, ordered by start time.
    /// </summary>
    public async Task<IReadOnlyList<AppSession>> GetSessionsOverlappingLocalDayAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            return Array.Empty<AppSession>();
        }

        var range = DataViewQueryHelpers.GetLocalDayRangeUtc(localDate);
        var nowUtc = DateTime.UtcNow;

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(_databasePath, cancellationToken);
        if (!await DataViewQueryHelpers.TableExistsAsync(connection, "app_sessions", cancellationToken))
        {
            return Array.Empty<AppSession>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                started_at_utc,
                ended_at_utc,
                process_name,
                window_title,
                total_duration_seconds,
                active_duration_seconds,
                idle_duration_seconds,
                unknown_duration_seconds,
                close_reason
            FROM app_sessions
            WHERE started_at_utc < $end_utc
              AND COALESCE(ended_at_utc, $now_utc) > $start_utc
            ORDER BY started_at_utc ASC;
            """;

        command.Parameters.AddWithValue("$start_utc", DataViewQueryHelpers.ToDbDateTime(range.StartUtc));
        command.Parameters.AddWithValue("$end_utc", DataViewQueryHelpers.ToDbDateTime(range.EndUtc));
        command.Parameters.AddWithValue("$now_utc", DataViewQueryHelpers.ToDbDateTime(nowUtc));

        var results = new List<AppSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var processName = reader.GetString(3);
            results.Add(new AppSession
            {
                Id = reader.GetInt64(0),
                StartedAtUtc = DataViewQueryHelpers.ParseDbDateTime(reader.GetString(1)),
                EndedAtUtc = reader.IsDBNull(2) ? null : DataViewQueryHelpers.ParseDbDateTime(reader.GetString(2)),
                ProcessName = processName,
                DisplayName = DataViewQueryHelpers.ResolveDisplayName(processName),
                WindowTitle = reader.IsDBNull(4) ? null : reader.GetString(4),
                TotalDurationSeconds = reader.GetInt32(5),
                ActiveDurationSeconds = reader.GetInt32(6),
                IdleDurationSeconds = reader.GetInt32(7),
                UnknownDurationSeconds = reader.GetInt32(8),
                CloseReason = reader.GetString(9)
            });
        }

        return results;
    }

    /// <summary>
    /// Computes overlap-scaled duration contribution of a session within a date range.
    /// Returns the scaled active, idle, total, and unknown durations in seconds.
    /// </summary>
    public static (long Total, long Active, long Idle) ScaleSessionDurations(
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
            Idle: (long)Math.Round(session.IdleDurationSeconds * scale, MidpointRounding.AwayFromZero)
        );
    }

    /// <summary>
    /// Returns all foreground samples for the given local date, ordered by sample time ascending.
    /// Used by focus-metrics calculations that need the raw sample sequence.
    /// </summary>
    public async Task<IReadOnlyList<ForegroundSample>> GetSamplesForLocalDayAsync(
        DateOnly localDate,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            return Array.Empty<ForegroundSample>();
        }

        var range = DataViewQueryHelpers.GetLocalDayRangeUtc(localDate);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(_databasePath, cancellationToken);
        if (!await DataViewQueryHelpers.TableExistsAsync(connection, "foreground_samples", cancellationToken))
        {
            return Array.Empty<ForegroundSample>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                sample_time_utc,
                process_name,
                window_title,
                executable_path,
                idle_seconds,
                activity_state
            FROM foreground_samples
            WHERE sample_time_utc >= $start_utc
              AND sample_time_utc < $end_utc
            ORDER BY sample_time_utc ASC;
            """;

        command.Parameters.AddWithValue("$start_utc", DataViewQueryHelpers.ToDbDateTime(range.StartUtc));
        command.Parameters.AddWithValue("$end_utc", DataViewQueryHelpers.ToDbDateTime(range.EndUtc));

        var results = new List<ForegroundSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var processName = reader.GetString(2);
            results.Add(new ForegroundSample
            {
                Id = reader.GetInt64(0),
                SampleTimeUtc = DataViewQueryHelpers.ParseDbDateTime(reader.GetString(1)),
                ProcessName = processName,
                DisplayName = DataViewQueryHelpers.ResolveDisplayName(processName),
                WindowTitle = reader.IsDBNull(3) ? null : reader.GetString(3),
                ExecutablePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                IdleSeconds = reader.GetInt32(5),
                ActivityState = reader.GetString(6)
            });
        }

        return results;
    }

    /// <summary>
    /// Returns all foreground samples for the given date range (inclusive), ordered by sample time ascending.
    /// Uses a single SQL query spanning the full UTC range.
    /// </summary>
    public async Task<IReadOnlyList<ForegroundSample>> GetSamplesForDateRangeAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            return Array.Empty<ForegroundSample>();
        }

        var startRange = DataViewQueryHelpers.GetLocalDayRangeUtc(startDate);
        var endRange = DataViewQueryHelpers.GetLocalDayRangeUtc(endDate);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(_databasePath, cancellationToken);
        if (!await DataViewQueryHelpers.TableExistsAsync(connection, "foreground_samples", cancellationToken))
        {
            return Array.Empty<ForegroundSample>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                sample_time_utc,
                process_name,
                window_title,
                executable_path,
                idle_seconds,
                activity_state
            FROM foreground_samples
            WHERE sample_time_utc >= $start_utc
              AND sample_time_utc < $end_utc
            ORDER BY sample_time_utc ASC;
            """;

        command.Parameters.AddWithValue("$start_utc", DataViewQueryHelpers.ToDbDateTime(startRange.StartUtc));
        command.Parameters.AddWithValue("$end_utc", DataViewQueryHelpers.ToDbDateTime(endRange.EndUtc));

        var results = new List<ForegroundSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var processName = reader.GetString(2);
            results.Add(new ForegroundSample
            {
                Id = reader.GetInt64(0),
                SampleTimeUtc = DataViewQueryHelpers.ParseDbDateTime(reader.GetString(1)),
                ProcessName = processName,
                DisplayName = DataViewQueryHelpers.ResolveDisplayName(processName),
                WindowTitle = reader.IsDBNull(3) ? null : reader.GetString(3),
                ExecutablePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                IdleSeconds = reader.GetInt32(5),
                ActivityState = reader.GetString(6)
            });
        }

        return results;
    }

    /// <summary>
    /// Returns the UTC date range for the given local date, computed from local midnight boundaries.
    /// </summary>
    public static (DateTime StartUtc, DateTime EndUtc) GetLocalDayRangeUtc(DateOnly localDate)
    {
        var range = DataViewQueryHelpers.GetLocalDayRangeUtc(localDate);
        return (range.StartUtc, range.EndUtc);
    }
}
