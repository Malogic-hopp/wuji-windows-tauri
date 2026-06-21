using System.Globalization;
using Microsoft.Data.Sqlite;
using QuantifiedSelf.Windows.Core.Events;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

public sealed class AgentEventRepository
{
    private readonly string _databasePath;

    public AgentEventRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InsertAsync(AgentEvent agentEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);

        await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO agent_events (
                event_time_utc,
                event_type,
                event_level,
                message,
                source,
                request_id,
                error_code,
                process_name,
                session_id,
                payload_json
            )
            VALUES (
                $event_time_utc,
                $event_type,
                $event_level,
                $message,
                $source,
                $request_id,
                $error_code,
                $process_name,
                $session_id,
                $payload_json
            );
            """;

        command.Parameters.AddWithValue("$event_time_utc", ToDbDateTime(agentEvent.EventTimeUtc));
        command.Parameters.AddWithValue("$event_type", agentEvent.EventType.ToString());
        command.Parameters.AddWithValue("$event_level", agentEvent.EventLevel.ToString());
        command.Parameters.AddWithValue("$message", agentEvent.Message);
        command.Parameters.AddWithValue("$source", (object?)agentEvent.Source ?? DBNull.Value);
        command.Parameters.AddWithValue("$request_id", (object?)agentEvent.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$error_code", (object?)agentEvent.ErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$process_name", (object?)agentEvent.ProcessName ?? DBNull.Value);
        command.Parameters.AddWithValue("$session_id", (object?)agentEvent.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload_json", (object?)agentEvent.PayloadJson ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid();";
        var value = await idCommand.ExecuteScalarAsync(cancellationToken);
        if (value is long id)
        {
            agentEvent.Id = id;
        }
    }

    public async Task<IReadOnlyList<AgentEvent>> GetRecentAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        if (!await HasAgentEventsTableAsync(cancellationToken))
        {
            return Array.Empty<AgentEvent>();
        }

        limit = Math.Max(1, limit);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                event_time_utc,
                event_type,
                event_level,
                message,
                source,
                request_id,
                error_code,
                process_name,
                session_id,
                payload_json
            FROM agent_events
            ORDER BY event_time_utc DESC, id DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", limit);

        return await ReadAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<AgentEvent>> GetRecentErrorsAsync(int limit = 20, CancellationToken cancellationToken = default)
    {
        if (!await HasAgentEventsTableAsync(cancellationToken))
        {
            return Array.Empty<AgentEvent>();
        }

        limit = Math.Max(1, limit);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                event_time_utc,
                event_type,
                event_level,
                message,
                source,
                request_id,
                error_code,
                process_name,
                session_id,
                payload_json
            FROM agent_events
            WHERE event_level IN ('Warning', 'Error', 'Critical')
            ORDER BY event_time_utc DESC, id DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", limit);

        return await ReadAsync(command, cancellationToken);
    }

    private async Task<bool> HasAgentEventsTableAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return false;
        }

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name = 'agent_events'
            LIMIT 1;
            """;

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null;
    }

    private static async Task<IReadOnlyList<AgentEvent>> ReadAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var results = new List<AgentEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadEvent(reader));
        }

        return results;
    }

    private static AgentEvent ReadEvent(SqliteDataReader reader)
    {
        return new AgentEvent
        {
            Id = reader.GetInt64(0),
            EventTimeUtc = ParseDbDateTime(reader.GetString(1)),
            EventType = Enum.Parse<AgentEventType>(reader.GetString(2), ignoreCase: true),
            EventLevel = Enum.Parse<AgentEventLevel>(reader.GetString(3), ignoreCase: true),
            Message = reader.GetString(4),
            Source = reader.IsDBNull(5) ? null : reader.GetString(5),
            RequestId = reader.IsDBNull(6) ? null : reader.GetString(6),
            ErrorCode = reader.IsDBNull(7) ? null : reader.GetString(7),
            ProcessName = reader.IsDBNull(8) ? null : reader.GetString(8),
            SessionId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
            PayloadJson = reader.IsDBNull(10) ? null : reader.GetString(10)
        };
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
