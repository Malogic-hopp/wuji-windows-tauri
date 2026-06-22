using Microsoft.Data.Sqlite;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

public sealed class SampleQueryService
{
    private readonly string _databasePath;

    public SampleQueryService(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task<IReadOnlyList<ForegroundSample>> GetRecentSamplesAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            return Array.Empty<ForegroundSample>();
        }

        limit = DataViewQueryHelpers.NormalizeLimit(limit);

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
            ORDER BY sample_time_utc DESC, id DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<ForegroundSample>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadSample(reader));
        }

        return results;
    }

    private static ForegroundSample ReadSample(SqliteDataReader reader)
    {
        var processName = reader.GetString(2);
        return new ForegroundSample
        {
            Id = reader.GetInt64(0),
            SampleTimeUtc = DataViewQueryHelpers.ParseDbDateTime(reader.GetString(1)),
            ProcessName = processName,
            DisplayName = DataViewQueryHelpers.ResolveDisplayName(processName),
            WindowTitle = reader.IsDBNull(3) ? null : reader.GetString(3),
            ExecutablePath = reader.IsDBNull(4) ? null : reader.GetString(4),
            IdleSeconds = reader.GetInt32(5),
            ActivityState = reader.GetString(6)
        };
    }
}
