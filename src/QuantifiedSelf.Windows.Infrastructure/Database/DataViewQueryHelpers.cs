using System.Globalization;
using Microsoft.Data.Sqlite;
using QuantifiedSelf.Windows.Core.Display;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

internal static class DataViewQueryHelpers
{
    public static int NormalizeLimit(int limit)
    {
        return Math.Max(1, limit);
    }

    public static string ToDbDateTime(DateTime value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    public static DateTime ParseDbDateTime(string value)
    {
        return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public static DateRange GetLocalDayRangeUtc(DateOnly localDate)
    {
        var localStart = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        var localEnd = localStart.AddDays(1);
        return new DateRange(localStart.ToUniversalTime(), localEnd.ToUniversalTime());
    }

    public static string ResolveDisplayName(string processName)
    {
        var displayName = ProductDisplayNameResolver.Resolve(processName);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        return string.IsNullOrWhiteSpace(processName)
            ? "Unknown"
            : processName.Trim();
    }

    public static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $table_name;
            """;

        command.Parameters.AddWithValue("$table_name", tableName);
        var count = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(count, CultureInfo.InvariantCulture) > 0;
    }

    public sealed record DateRange(DateTime StartUtc, DateTime EndUtc);
}
