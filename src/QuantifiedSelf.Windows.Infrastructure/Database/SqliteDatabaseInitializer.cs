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

        if (await NeedsSchemaResetAsync(connection, cancellationToken))
        {
            await ExecuteAsync(connection, "DROP TABLE IF EXISTS foreground_samples;", cancellationToken);
            await ExecuteAsync(connection, "DROP TABLE IF EXISTS app_sessions;", cancellationToken);
        }

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

    private static async Task<bool> NeedsSchemaResetAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var foregroundColumns = await GetColumnNamesAsync(connection, "foreground_samples", cancellationToken);
        var sessionColumns = await GetColumnNamesAsync(connection, "app_sessions", cancellationToken);

        var hasExistingSchema = foregroundColumns.Count > 0 || sessionColumns.Count > 0;
        if (!hasExistingSchema)
        {
            return false;
        }

        return !IsCompatibleSchema(
            foregroundColumns,
            ["id", "sample_time_utc", "process_name", "window_title", "executable_path", "idle_seconds", "activity_state"])
            || !IsCompatibleSchema(
                sessionColumns,
                ["id", "started_at_utc", "ended_at_utc", "process_name", "window_title", "total_duration_seconds", "active_duration_seconds", "idle_duration_seconds", "unknown_duration_seconds", "close_reason"]);
    }

    private static bool IsCompatibleSchema(
        IReadOnlyCollection<string> actualColumns,
        IReadOnlyCollection<string> expectedColumns)
    {
        if (actualColumns.Count == 0)
        {
            return false;
        }

        var actualSet = new HashSet<string>(actualColumns, StringComparer.OrdinalIgnoreCase);
        foreach (var column in expectedColumns)
        {
            if (!actualSet.Contains(column))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<IReadOnlyCollection<string>> GetColumnNamesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
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
