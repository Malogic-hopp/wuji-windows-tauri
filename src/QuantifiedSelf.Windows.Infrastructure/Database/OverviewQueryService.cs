using System.Globalization;
using Microsoft.Data.Sqlite;
using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Infrastructure.Database;

public sealed class OverviewQueryService
{
    private readonly string _databasePath;

    public OverviewQueryService(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task<DashboardSummary> GetTodaySummaryAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            return new DashboardSummary();
        }

        var range = GetLocalDayRangeUtc(DateTime.Now);
        var nowUtc = DateTime.UtcNow;
        var sessions = await ReadOverlappingSessionsAsync(range, nowUtc, cancellationToken);
        var summary = new DashboardSummary { DateUtc = range.StartUtc };

        foreach (var session in sessions)
        {
            AddContribution(summary, session, range, nowUtc);
        }

        return summary;
    }

    public async Task<IReadOnlyList<AppUsageSummary>> GetTopAppsTodayAsync(
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            return Array.Empty<AppUsageSummary>();
        }

        limit = Math.Max(1, limit);

        var range = GetLocalDayRangeUtc(DateTime.Now);
        var nowUtc = DateTime.UtcNow;
        var sessions = await ReadOverlappingSessionsAsync(range, nowUtc, cancellationToken);
        var aggregates = new Dictionary<string, AppUsageSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var session in sessions)
        {
            var overlap = GetOverlapSeconds(session, range, nowUtc);
            if (overlap <= 0)
            {
                continue;
            }

            var contribution = CreateContribution(session, range, nowUtc);
            if (!aggregates.TryGetValue(session.ProcessName, out var summary))
            {
                summary = new AppUsageSummary
                {
                    ProcessName = session.ProcessName,
                    DisplayName = session.ProcessName
                };
                aggregates.Add(session.ProcessName, summary);
            }

            summary.TotalDurationSeconds += contribution.TotalDurationSeconds;
            summary.ActiveDurationSeconds += contribution.ActiveDurationSeconds;
            summary.IdleDurationSeconds += contribution.IdleDurationSeconds;
            summary.UnknownDurationSeconds += contribution.UnknownDurationSeconds;
            summary.SessionCount++;

            var lastUsedAtUtc = GetSessionLastUsedAtUtc(session, nowUtc);
            if (!summary.LastUsedAtUtc.HasValue || lastUsedAtUtc > summary.LastUsedAtUtc.Value)
            {
                summary.LastUsedAtUtc = lastUsedAtUtc;
            }
        }

        return aggregates.Values
            .OrderByDescending(x => x.ActiveDurationSeconds)
            .ThenByDescending(x => x.TotalDurationSeconds)
            .ThenBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    public async Task<IReadOnlyList<AppSession>> GetRecentSessionsAsync(
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath))
        {
            return Array.Empty<AppSession>();
        }

        limit = Math.Max(1, limit);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(_databasePath, cancellationToken);
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
            ORDER BY started_at_utc DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<AppSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadSession(reader));
        }

        return results;
    }

    private async Task<IReadOnlyList<AppSession>> ReadOverlappingSessionsAsync(
        DateRange range,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(_databasePath, cancellationToken);
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
            ORDER BY started_at_utc ASC;
            """;

        command.Parameters.AddWithValue("$start_utc", ToDbDateTime(range.StartUtc));
        command.Parameters.AddWithValue("$end_utc", ToDbDateTime(range.EndUtc));
        command.Parameters.AddWithValue("$now_utc", ToDbDateTime(nowUtc));

        var results = new List<AppSession>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadSession(reader));
        }

        return results;
    }

    private static void AddContribution(DashboardSummary summary, AppSession session, DateRange range, DateTime nowUtc)
    {
        var contribution = CreateContribution(session, range, nowUtc);
        summary.SessionCount++;
        summary.TotalDurationSeconds += contribution.TotalDurationSeconds;
        summary.ActiveDurationSeconds += contribution.ActiveDurationSeconds;
        summary.IdleDurationSeconds += contribution.IdleDurationSeconds;
        summary.UnknownDurationSeconds += contribution.UnknownDurationSeconds;
    }

    private static SessionContribution CreateContribution(AppSession session, DateRange range, DateTime nowUtc)
    {
        var overlapSeconds = GetOverlapSeconds(session, range, nowUtc);
        if (overlapSeconds <= 0)
        {
            return new SessionContribution();
        }

        var sessionSpanSeconds = GetSessionSpanSeconds(session, nowUtc);
        var scale = overlapSeconds / (double)sessionSpanSeconds;

        return new SessionContribution
        {
            TotalDurationSeconds = ScaleDuration(session.TotalDurationSeconds, scale),
            ActiveDurationSeconds = ScaleDuration(session.ActiveDurationSeconds, scale),
            IdleDurationSeconds = ScaleDuration(session.IdleDurationSeconds, scale),
            UnknownDurationSeconds = ScaleDuration(session.UnknownDurationSeconds, scale)
        };
    }

    private static int GetOverlapSeconds(AppSession session, DateRange range, DateTime nowUtc)
    {
        var sessionStart = session.StartedAtUtc.ToUniversalTime();
        var sessionEnd = (session.EndedAtUtc ?? nowUtc).ToUniversalTime();
        var overlapStart = sessionStart > range.StartUtc ? sessionStart : range.StartUtc;
        var overlapEnd = sessionEnd < range.EndUtc ? sessionEnd : range.EndUtc;
        var overlapSeconds = (int)Math.Round((overlapEnd - overlapStart).TotalSeconds, MidpointRounding.AwayFromZero);
        return Math.Max(0, overlapSeconds);
    }

    private static int GetSessionSpanSeconds(AppSession session, DateTime nowUtc)
    {
        var sessionStart = session.StartedAtUtc.ToUniversalTime();
        var sessionEnd = (session.EndedAtUtc ?? nowUtc).ToUniversalTime();
        var spanSeconds = (int)Math.Round((sessionEnd - sessionStart).TotalSeconds, MidpointRounding.AwayFromZero);
        return Math.Max(1, spanSeconds);
    }

    private static int ScaleDuration(int durationSeconds, double scale)
    {
        return (int)Math.Round(durationSeconds * scale, MidpointRounding.AwayFromZero);
    }

    private static DateTime GetSessionLastUsedAtUtc(AppSession session, DateTime nowUtc)
    {
        return session.EndedAtUtc?.ToUniversalTime() ?? nowUtc;
    }

    private static DateRange GetLocalDayRangeUtc(DateTime localNow)
    {
        var localStart = localNow.Date;
        var localEnd = localStart.AddDays(1);
        return new DateRange(localStart.ToUniversalTime(), localEnd.ToUniversalTime());
    }

    private static string ToDbDateTime(DateTime value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTime ParseDbDateTime(string value)
    {
        return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
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

    private sealed record DateRange(DateTime StartUtc, DateTime EndUtc);

    private sealed record SessionContribution
    {
        public int TotalDurationSeconds { get; init; }

        public int ActiveDurationSeconds { get; init; }

        public int IdleDurationSeconds { get; init; }

        public int UnknownDurationSeconds { get; init; }
    }
}
