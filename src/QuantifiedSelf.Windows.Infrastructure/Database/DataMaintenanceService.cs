using System.Globalization;
using Microsoft.Data.Sqlite;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Maintenance;
using QuantifiedSelf.Windows.Core.Paths;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

public class DataMaintenanceService
{
    private readonly WindowsAgentPaths _paths;

    public DataMaintenanceService(WindowsAgentPaths paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// Prune expired data based on retentionDays.
    /// referenceTimeUtc is injectable for testing; defaults to DateTime.UtcNow.
    /// </summary>
    public virtual async Task<PruneDataResult> PruneDataAsync(
        int retentionDays,
        DateTime? referenceTimeUtc = null,
        CancellationToken cancellationToken = default)
    {
        var refTime = referenceTimeUtc ?? DateTime.UtcNow;
        var cutoffUtc = refTime.AddDays(-retentionDays);
        var cutoffLocalDate = DateOnly.FromDateTime(refTime.ToLocalTime().Date.AddDays(-retentionDays));

        // SQLite deletion
        int foregroundDeleted, sessionsDeleted, eventsDeleted;
        try
        {
            (foregroundDeleted, sessionsDeleted, eventsDeleted) = await DeleteExpiredSqliteRowsAsync(
                cutoffUtc, cancellationToken);
        }
        catch (Exception ex)
        {
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            return PruneDataResult.Failed(
                "PruneDataSqliteFailed",
                string.IsNullOrWhiteSpace(safeMessage) ? "SQLite prune failed." : $"SQLite prune failed: {safeMessage}",
                cutoffUtc, cutoffLocalDate);
        }

        // JSONL deletion
        int jsonlDeleted;
        int jsonlErrors;
        try
        {
            (jsonlDeleted, jsonlErrors) = DeleteOldJsonlFiles(cutoffLocalDate);
        }
        catch (Exception ex)
        {
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            return PruneDataResult.Failed(
                "PruneDataJsonlFailed",
                string.IsNullOrWhiteSpace(safeMessage) ? "JSONL prune failed." : $"JSONL prune failed: {safeMessage}",
                cutoffUtc, cutoffLocalDate);
        }

        return PruneDataResult.Ok(foregroundDeleted, sessionsDeleted, eventsDeleted, jsonlDeleted, cutoffUtc, cutoffLocalDate, jsonlErrors);
    }

    /// <summary>
    /// Clear all history: delete all rows from foreground_samples, app_sessions, agent_events in a transaction,
    /// and delete historical JSONL files (before today).
    /// </summary>
    public virtual async Task<ClearHistoryResult> ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        var todayLocal = DateOnly.FromDateTime(DateTime.Now);

        // SQLite: clear all three tables in a transaction
        int foregroundDeleted, sessionsDeleted, eventsDeleted;
        try
        {
            await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(
                _paths.DatabasePath, cancellationToken);

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                foregroundDeleted = await ExecuteNonQueryAsync(connection,
                    "DELETE FROM foreground_samples", cancellationToken);
                sessionsDeleted = await ExecuteNonQueryAsync(connection,
                    "DELETE FROM app_sessions", cancellationToken);
                eventsDeleted = await ExecuteNonQueryAsync(connection,
                    "DELETE FROM agent_events", cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch (Exception ex)
        {
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            return ClearHistoryResult.Failed(
                "ClearHistorySqliteFailed",
                string.IsNullOrWhiteSpace(safeMessage) ? "SQLite clear failed." : $"SQLite clear failed: {safeMessage}");
        }

        // JSONL: delete files before today
        int jsonlDeleted;
        int jsonlErrors;
        try
        {
            (jsonlDeleted, jsonlErrors) = DeleteOldJsonlFiles(todayLocal);
        }
        catch (Exception ex)
        {
            var safeMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(ex);
            return ClearHistoryResult.Failed(
                "ClearHistoryJsonlFailed",
                string.IsNullOrWhiteSpace(safeMessage) ? "JSONL clear failed." : $"JSONL clear failed: {safeMessage}");
        }

        return ClearHistoryResult.Ok(foregroundDeleted, sessionsDeleted, eventsDeleted, jsonlDeleted, jsonlErrors);
    }

    private async Task<(int foregroundDeleted, int sessionsDeleted, int eventsDeleted)> DeleteExpiredSqliteRowsAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken)
    {
        var cutoffText = cutoffUtc.ToString("O", CultureInfo.InvariantCulture);

        await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(
            _paths.DatabasePath, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var foregroundDeleted = await ExecuteDeleteAsync(
                connection,
                "DELETE FROM foreground_samples WHERE sample_time_utc < @cutoff",
                ("@cutoff", cutoffText),
                cancellationToken);

            var sessionsDeleted = await ExecuteDeleteAsync(
                connection,
                "DELETE FROM app_sessions WHERE ended_at_utc IS NOT NULL AND ended_at_utc < @cutoff",
                ("@cutoff", cutoffText),
                cancellationToken);

            var eventsDeleted = await ExecuteDeleteAsync(
                connection,
                "DELETE FROM agent_events WHERE event_time_utc < @cutoff",
                ("@cutoff", cutoffText),
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return (foregroundDeleted, sessionsDeleted, eventsDeleted);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<int> ExecuteDeleteAsync(
        SqliteConnection connection,
        string commandText,
        (string Name, string Value) parameter,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            // Table missing — safe to return 0
            return 0;
        }
    }

    private static async Task<int> ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = commandText;
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
    }

    /// <summary>
    /// Delete JSONL files in the logs directory whose local file date is before cutoffLocalDate.
    /// Files matching agent_events_YYYYMMDD.jsonl and foreground_samples_YYYYMMDD.jsonl are targeted.
    /// </summary>
    public (int deleted, int errorCount) DeleteOldJsonlFiles(DateOnly cutoffLocalDate)
    {
        var logsDir = _paths.LogsDir;
        if (!Directory.Exists(logsDir))
        {
            return (0, 0);
        }

        var deleted = 0;
        var errors = 0;

        foreach (var file in Directory.EnumerateFiles(logsDir, "*.jsonl"))
        {
            var fileName = Path.GetFileName(file);
            var fileDate = TryParseJournalDate(fileName);
            if (fileDate is null)
            {
                continue; // not a journal file we manage
            }

            if (fileDate.Value >= cutoffLocalDate)
            {
                continue; // keep current and recent files
            }

            try
            {
                File.Delete(file);
                deleted++;
            }
            catch
            {
                errors++;
            }
        }

        return (deleted, errors);
    }

    /// <summary>
    /// Try to parse a date from a journal file name like agent_events_20260101.jsonl
    /// or foreground_samples_20260101.jsonl. Returns null for non-matching files.
    /// </summary>
    public static DateOnly? TryParseJournalDate(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var prefix = fileName.StartsWith("agent_events_", StringComparison.OrdinalIgnoreCase) ? "agent_events_"
            : fileName.StartsWith("foreground_samples_", StringComparison.OrdinalIgnoreCase) ? "foreground_samples_"
            : null;

        if (prefix is null)
        {
            return null;
        }

        var remainder = fileName.AsSpan()[prefix.Length..];
        // Expect exactly 8 digits followed by ".jsonl"
        if (!remainder.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || remainder.Length != 14)
        {
            return null;
        }

        var datePart = remainder[..8];
        if (!DateOnly.TryParseExact(datePart, "yyyyMMdd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return null;
        }

        return date;
    }
}
