using Microsoft.Data.Sqlite;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

public sealed class SqliteDatabaseInitializer
{
    private readonly string _databasePath;

    public SqliteDatabaseInitializer(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_databasePath);

        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = await SqliteConnectionFactory.OpenAsync(
            _databasePath,
            SqliteOpenMode.ReadWriteCreate,
            cancellationToken);

        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS foreground_samples (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sample_time_utc TEXT NOT NULL,
                process_name TEXT NOT NULL,
                window_title TEXT,
                executable_path TEXT,
                idle_seconds INTEGER NOT NULL,
                activity_state TEXT NOT NULL
            );
            """,
            cancellationToken);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS app_sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at_utc TEXT NOT NULL,
                ended_at_utc TEXT,
                process_name TEXT NOT NULL,
                window_title TEXT,
                total_duration_seconds INTEGER NOT NULL DEFAULT 0,
                active_duration_seconds INTEGER NOT NULL DEFAULT 0,
                idle_duration_seconds INTEGER NOT NULL DEFAULT 0,
                unknown_duration_seconds INTEGER NOT NULL DEFAULT 0,
                close_reason TEXT NOT NULL DEFAULT 'Open'
            );
            """,
            cancellationToken);

        await ExecuteAsync(
            connection,
            """
            CREATE INDEX IF NOT EXISTS idx_foreground_samples_time
            ON foreground_samples(sample_time_utc);
            """,
            cancellationToken);

        await ExecuteAsync(
            connection,
            """
            CREATE INDEX IF NOT EXISTS idx_app_sessions_started
            ON app_sessions(started_at_utc);
            """,
            cancellationToken);

        await ExecuteAsync(
            connection,
            """
            CREATE INDEX IF NOT EXISTS idx_app_sessions_process
            ON app_sessions(process_name);
            """,
            cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
