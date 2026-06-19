using System.Globalization;
using Microsoft.Data.Sqlite;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

public sealed class ForegroundSampleRepository
{
    private readonly string _databasePath;

    public ForegroundSampleRepository(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InsertAsync(ForegroundSample sample, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);

        await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(_databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO foreground_samples (
                sample_time_utc,
                process_name,
                window_title,
                executable_path,
                idle_seconds,
                activity_state
            )
            VALUES (
                $sample_time_utc,
                $process_name,
                $window_title,
                $executable_path,
                $idle_seconds,
                $activity_state
            );
            """;

        command.Parameters.AddWithValue("$sample_time_utc", ToDbDateTime(sample.SampleTimeUtc));
        command.Parameters.AddWithValue("$process_name", sample.ProcessName);
        command.Parameters.AddWithValue("$window_title", (object?)sample.WindowTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$executable_path", (object?)sample.ExecutablePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$idle_seconds", sample.IdleSeconds);
        command.Parameters.AddWithValue("$activity_state", sample.ActivityState);

        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid();";
        var sampleId = await idCommand.ExecuteScalarAsync(cancellationToken);
        if (sampleId is long id)
        {
            sample.Id = id;
        }
    }

    private static string ToDbDateTime(DateTime value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}
