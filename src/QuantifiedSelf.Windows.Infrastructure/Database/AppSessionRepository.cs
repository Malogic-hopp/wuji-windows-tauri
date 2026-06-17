using System.Globalization;
using Microsoft.Data.Sqlite;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

public sealed class AppSessionRepository
{
    private readonly string _databasePath;

    public AppSessionRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task<AppSession?> GetOpenSessionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(_databasePath, cancellationToken);
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
            WHERE ended_at_utc IS NULL
            ORDER BY started_at_utc DESC
            LIMIT 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadSession(reader);
    }

    public async Task StartSessionAsync(
        ForegroundSample sample,
        int deltaSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);

        await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO app_sessions (
                started_at_utc,
                ended_at_utc,
                process_name,
                window_title,
                total_duration_seconds,
                active_duration_seconds,
                idle_duration_seconds,
                unknown_duration_seconds,
                close_reason
            )
            VALUES (
                $started_at_utc,
                NULL,
                $process_name,
                $window_title,
                $total_duration_seconds,
                $active_duration_seconds,
                $idle_duration_seconds,
                $unknown_duration_seconds,
                'Open'
            );
            """;

        command.Parameters.AddWithValue("$started_at_utc", ToDbDateTime(sample.SampleTimeUtc));
        command.Parameters.AddWithValue("$process_name", sample.ProcessName);
        command.Parameters.AddWithValue("$window_title", (object?)sample.WindowTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$total_duration_seconds", deltaSeconds);
        command.Parameters.AddWithValue("$active_duration_seconds", GetDurationDelta(sample, deltaSeconds, "Active"));
        command.Parameters.AddWithValue("$idle_duration_seconds", GetDurationDelta(sample, deltaSeconds, "Idle"));
        command.Parameters.AddWithValue("$unknown_duration_seconds", GetDurationDelta(sample, deltaSeconds, "Unknown"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ExtendOpenSessionAsync(
        ForegroundSample sample,
        int deltaSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);

        await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE app_sessions
            SET
                window_title = $window_title,
                total_duration_seconds = total_duration_seconds + $delta_seconds,
                active_duration_seconds = active_duration_seconds + $active_delta,
                idle_duration_seconds = idle_duration_seconds + $idle_delta,
                unknown_duration_seconds = unknown_duration_seconds + $unknown_delta
            WHERE id = (
                SELECT id
                FROM app_sessions
                WHERE ended_at_utc IS NULL
                ORDER BY started_at_utc DESC
                LIMIT 1
            );
            """;

        command.Parameters.AddWithValue("$window_title", (object?)sample.WindowTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$delta_seconds", deltaSeconds);
        command.Parameters.AddWithValue("$active_delta", GetDurationDelta(sample, deltaSeconds, "Active"));
        command.Parameters.AddWithValue("$idle_delta", GetDurationDelta(sample, deltaSeconds, "Idle"));
        command.Parameters.AddWithValue("$unknown_delta", GetDurationDelta(sample, deltaSeconds, "Unknown"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CloseOpenSessionAsync(string closeReason, CancellationToken cancellationToken = default)
    {
        await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE app_sessions
            SET
                ended_at_utc = $ended_at_utc,
                close_reason = $close_reason
            WHERE id = (
                SELECT id
                FROM app_sessions
                WHERE ended_at_utc IS NULL
                ORDER BY started_at_utc DESC
                LIMIT 1
            );
            """;

        command.Parameters.AddWithValue("$ended_at_utc", ToDbDateTime(DateTime.UtcNow));
        command.Parameters.AddWithValue("$close_reason", closeReason);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static AppSession ReadSession(SqliteDataReader reader)
    {
        return new AppSession
        {
            Id = reader.GetInt64(0),
            StartedAtUtc = ParseDbDateTime(reader.GetString(1)),
            EndedAtUtc = reader.IsDBNull(2) ? null : ParseDbDateTime(reader.GetString(2)),
            ProcessName = reader.GetString(3),
            WindowTitle = reader.IsDBNull(4) ? null : reader.GetString(4),
            TotalDurationSeconds = reader.GetInt32(5),
            ActiveDurationSeconds = reader.GetInt32(6),
            IdleDurationSeconds = reader.GetInt32(7),
            UnknownDurationSeconds = reader.GetInt32(8),
            CloseReason = reader.GetString(9)
        };
    }

    private static int GetDurationDelta(ForegroundSample sample, int deltaSeconds, string activeState)
    {
        return string.Equals(sample.ActivityState, activeState, StringComparison.OrdinalIgnoreCase)
            ? deltaSeconds
            : 0;
    }

    private static string ToDbDateTime(DateTime value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTime ParseDbDateTime(string value)
    {
        return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
