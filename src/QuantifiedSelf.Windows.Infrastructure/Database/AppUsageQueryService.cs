using Microsoft.Data.Sqlite;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

public sealed class AppUsageQueryService
{
    private readonly string _databasePath;

    public AppUsageQueryService(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task<IReadOnlyList<AppUsageSummary>> GetAppUsageForLocalDayAsync(
        DateOnly localDate,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            return Array.Empty<AppUsageSummary>();
        }

        limit = DataViewQueryHelpers.NormalizeLimit(limit);

        var range = DataViewQueryHelpers.GetLocalDayRangeUtc(localDate);
        var nowUtc = DateTime.UtcNow;

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(_databasePath, cancellationToken);
        if (!await DataViewQueryHelpers.TableExistsAsync(connection, "app_sessions", cancellationToken))
        {
            return Array.Empty<AppUsageSummary>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH overlapping AS (
                SELECT
                    process_name,
                    total_duration_seconds,
                    active_duration_seconds,
                    idle_duration_seconds,
                    unknown_duration_seconds,
                    CASE
                        WHEN started_at_utc > $start_utc THEN started_at_utc
                        ELSE $start_utc
                    END AS overlap_start_utc,
                    CASE
                        WHEN COALESCE(ended_at_utc, $now_utc) < $end_utc THEN COALESCE(ended_at_utc, $now_utc)
                        ELSE $end_utc
                    END AS overlap_end_utc,
                    MAX(
                        1.0,
                        ROUND((julianday(COALESCE(ended_at_utc, $now_utc)) - julianday(started_at_utc)) * 86400.0)
                    ) AS span_seconds
                FROM app_sessions
                WHERE started_at_utc < $end_utc
                  AND COALESCE(ended_at_utc, $now_utc) > $start_utc
            ),
            scaled AS (
                SELECT
                    process_name,
                    total_duration_seconds,
                    active_duration_seconds,
                    idle_duration_seconds,
                    unknown_duration_seconds,
                    overlap_end_utc,
                    MAX(
                        0.0,
                        ROUND((julianday(overlap_end_utc) - julianday(overlap_start_utc)) * 86400.0)
                    ) AS overlap_seconds,
                    span_seconds
                FROM overlapping
            ),
            contributions AS (
                SELECT
                    process_name,
                    CAST(ROUND(total_duration_seconds * overlap_seconds / span_seconds) AS INTEGER) AS total_duration_seconds,
                    CAST(ROUND(active_duration_seconds * overlap_seconds / span_seconds) AS INTEGER) AS active_duration_seconds,
                    CAST(ROUND(idle_duration_seconds * overlap_seconds / span_seconds) AS INTEGER) AS idle_duration_seconds,
                    CAST(ROUND(unknown_duration_seconds * overlap_seconds / span_seconds) AS INTEGER) AS unknown_duration_seconds,
                    overlap_end_utc
                FROM scaled
                WHERE overlap_seconds > 0
            )
            SELECT
                process_name,
                SUM(total_duration_seconds) AS total_duration_seconds,
                SUM(active_duration_seconds) AS active_duration_seconds,
                SUM(idle_duration_seconds) AS idle_duration_seconds,
                SUM(unknown_duration_seconds) AS unknown_duration_seconds,
                COUNT(*) AS session_count,
                MAX(overlap_end_utc) AS last_used_utc
            FROM contributions
            GROUP BY process_name
            ORDER BY
                active_duration_seconds DESC,
                total_duration_seconds DESC,
                process_name COLLATE NOCASE ASC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$start_utc", DataViewQueryHelpers.ToDbDateTime(range.StartUtc));
        command.Parameters.AddWithValue("$end_utc", DataViewQueryHelpers.ToDbDateTime(range.EndUtc));
        command.Parameters.AddWithValue("$now_utc", DataViewQueryHelpers.ToDbDateTime(nowUtc));
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<AppUsageSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadSummary(reader));
        }

        return results;
    }

    private static AppUsageSummary ReadSummary(SqliteDataReader reader)
    {
        var processName = reader.GetString(0);
        return new AppUsageSummary
        {
            ProcessName = processName,
            DisplayName = DataViewQueryHelpers.ResolveDisplayName(processName),
            TotalDurationSeconds = reader.GetInt32(1),
            ActiveDurationSeconds = reader.GetInt32(2),
            IdleDurationSeconds = reader.GetInt32(3),
            UnknownDurationSeconds = reader.GetInt32(4),
            SessionCount = reader.GetInt32(5),
            LastUsedAtUtc = reader.IsDBNull(6) ? null : DataViewQueryHelpers.ParseDbDateTime(reader.GetString(6))
        };
    }
}
