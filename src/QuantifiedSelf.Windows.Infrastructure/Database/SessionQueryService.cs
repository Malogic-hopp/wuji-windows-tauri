using Microsoft.Data.Sqlite;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

public sealed class SessionQueryService
{
    private readonly string _databasePath;

    public SessionQueryService(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task<IReadOnlyList<AppSession>> GetRecentSessionsAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            return Array.Empty<AppSession>();
        }

        limit = DataViewQueryHelpers.NormalizeLimit(limit);

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
            ORDER BY started_at_utc DESC, id DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", limit);
        return await ReadSessionsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<AppSession>> GetSessionsForLocalDayAsync(
        DateOnly localDate,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        var range = DataViewQueryHelpers.GetLocalDayRangeUtc(localDate);
        return await GetSessionsOverlappingRangeAsync(range.StartUtc, range.EndUtc, limit, cancellationToken);
    }

    public async Task<IReadOnlyList<AppSession>> GetSessionsOverlappingRangeAsync(
        DateTime startUtc,
        DateTime endUtc,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            return Array.Empty<AppSession>();
        }

        limit = DataViewQueryHelpers.NormalizeLimit(limit);
        var normalizedStartUtc = startUtc.ToUniversalTime();
        var normalizedEndUtc = endUtc.ToUniversalTime();
        if (normalizedEndUtc <= normalizedStartUtc)
        {
            return Array.Empty<AppSession>();
        }

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
            ORDER BY started_at_utc DESC, id DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$start_utc", DataViewQueryHelpers.ToDbDateTime(normalizedStartUtc));
        command.Parameters.AddWithValue("$end_utc", DataViewQueryHelpers.ToDbDateTime(normalizedEndUtc));
        command.Parameters.AddWithValue("$now_utc", DataViewQueryHelpers.ToDbDateTime(nowUtc));
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadSessionsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<AppSession>> ReadSessionsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var results = new List<AppSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadSession(reader));
        }

        return results;
    }

    private static AppSession ReadSession(SqliteDataReader reader)
    {
        var processName = reader.GetString(3);
        return new AppSession
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
        };
    }
}
