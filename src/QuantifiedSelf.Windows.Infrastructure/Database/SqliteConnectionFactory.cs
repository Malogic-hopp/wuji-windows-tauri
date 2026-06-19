using Microsoft.Data.Sqlite;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

public static class SqliteConnectionFactory
{
    private const int BusyTimeoutMilliseconds = 5000;

    public static Task<SqliteConnection> OpenReadOnlyAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        return OpenAsync(databasePath, SqliteOpenMode.ReadOnly, cancellationToken);
    }

    public static Task<SqliteConnection> OpenReadWriteAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        return OpenAsync(databasePath, SqliteOpenMode.ReadWrite, cancellationToken);
    }

    public static async Task<SqliteConnection> OpenAsync(
        string databasePath,
        SqliteOpenMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        if (mode == SqliteOpenMode.ReadWriteCreate)
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await ApplyBusyTimeoutAsync(connection, cancellationToken);
        return connection;
    }

    private static async Task ApplyBusyTimeoutAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
