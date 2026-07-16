using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Database;
using QuantifiedSelf.Windows.Tests.TestHelpers;

namespace QuantifiedSelf.Windows.Tests;

[Trait("Category", "Integration")]
public sealed class DashboardParityTests
{
    [Fact]
    public async Task WpfAndTauriQueryPaths_KeepDashboardSemanticsForNonEmptyLocalDay()
    {
        using var workspace = new TempWorkspace("dashboard-parity");
        var paths = new WindowsAgentPaths(workspace.Root, "dev");
        await new SqliteDatabaseInitializer(paths.DatabasePath).InitializeAsync();

        var dayStart = DateTime.Now.Date;
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(-1), dayStart.AddHours(1),
            "CrossMidnight", 120, 40, 60, 20);
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(9), dayStart.AddHours(10),
            "Atlas", 6000, 5000, 500, 500);
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(10), dayStart.AddHours(10.5),
            "Browser", 5000, 4000, 500, 500);
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(11), dayStart.AddHours(11.333333),
            "Terminal", 4000, 3000, 500, 500);
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(12), dayStart.AddHours(12.166667),
            "Notes", 3000, 2000, 500, 500);
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(13), dayStart.AddHours(13.083333),
            "Mail", 2000, 1000, 500, 500);

        var wpfSummary = await ActivityTestServices.CreateDailyStats(paths).GetTodaySummaryAsync();
        var wpfRecentSessions = await ActivityTestServices.CreateSessions(paths).GetRecentSessionsAsync(limit: 5);
        var tauriOverview = ActivityTestServices.CreateOverview(paths);
        var tauriSummary = await tauriOverview.GetDashboardSummaryAsync();
        var tauriTopApps = await tauriOverview.GetTopAppsTodayAsync(limit: 5);
        var tauriRecentSessions = await tauriOverview.GetRecentSessionsAsync(limit: 5);

        Assert.True(wpfSummary.TotalDurationSeconds > 20000);
        Assert.True(wpfSummary.TotalActiveDurationSeconds > 15000);
        Assert.True(wpfSummary.TotalIdleDurationSeconds > 2500);
        Assert.Equal(6, wpfSummary.SessionCount);
        Assert.Equal(wpfSummary.TotalDurationSeconds, tauriSummary.TotalDurationSeconds);
        Assert.Equal(wpfSummary.TotalActiveDurationSeconds, tauriSummary.ActiveDurationSeconds);
        Assert.Equal(wpfSummary.TotalIdleDurationSeconds, tauriSummary.IdleDurationSeconds);
        Assert.Equal(
            wpfSummary.TotalDurationSeconds - wpfSummary.TotalActiveDurationSeconds - wpfSummary.TotalIdleDurationSeconds,
            tauriSummary.UnknownDurationSeconds);
        Assert.Equal(wpfSummary.SessionCount, tauriSummary.SessionCount);

        Assert.Equal(dayStart, wpfSummary.Date);
        Assert.Equal(dayStart, tauriSummary.DateUtc.ToLocalTime());
        Assert.Equal(DateTimeKind.Local, tauriSummary.DateUtc.ToLocalTime().Kind);

        Assert.Equal(["Atlas", "Browser", "Terminal", "Notes", "Mail"],
            tauriTopApps.Select(app => app.DisplayName).ToArray());
        AssertAppUsageMatches(wpfSummary.TopApps, tauriTopApps);

        Assert.Equal(5, tauriRecentSessions.Count);
        AssertSessionMatches(wpfRecentSessions, tauriRecentSessions);
        Assert.Equal(
            ["Mail", "Notes", "Terminal", "Browser", "Atlas"],
            tauriRecentSessions.Select(session => session.DisplayName).ToArray());
        Assert.All(tauriRecentSessions, session =>
        {
            Assert.Equal(DateTimeKind.Utc, session.StartedAtUtc.Kind);
            Assert.Equal(DateTimeKind.Local, session.StartedAtUtc.ToLocalTime().Kind);
        });
    }

    private static void AssertAppUsageMatches(
        IReadOnlyList<AppUsageSummary> expected,
        IReadOnlyList<AppUsageSummary> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].DisplayName, actual[index].DisplayName);
            Assert.Equal(expected[index].TotalDurationSeconds, actual[index].TotalDurationSeconds);
            Assert.Equal(expected[index].ActiveDurationSeconds, actual[index].ActiveDurationSeconds);
            Assert.Equal(expected[index].IdleDurationSeconds, actual[index].IdleDurationSeconds);
            Assert.Equal(expected[index].UnknownDurationSeconds, actual[index].UnknownDurationSeconds);
            Assert.Equal(expected[index].SessionCount, actual[index].SessionCount);
            Assert.Equal(expected[index].LastUsedAtUtc, actual[index].LastUsedAtUtc);
        }
    }

    private static void AssertSessionMatches(
        IReadOnlyList<AppSession> expected,
        IReadOnlyList<AppSession> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].DisplayName, actual[index].DisplayName);
            Assert.Equal(expected[index].StartedAtUtc, actual[index].StartedAtUtc);
            Assert.Equal(expected[index].EndedAtUtc, actual[index].EndedAtUtc);
            Assert.Equal(expected[index].TotalDurationSeconds, actual[index].TotalDurationSeconds);
            Assert.Equal(expected[index].ActiveDurationSeconds, actual[index].ActiveDurationSeconds);
            Assert.Equal(expected[index].IdleDurationSeconds, actual[index].IdleDurationSeconds);
            Assert.Equal(expected[index].UnknownDurationSeconds, actual[index].UnknownDurationSeconds);
        }
    }

    private static async Task InsertSessionAsync(
        string databasePath,
        DateTime startedAtLocal,
        DateTime endedAtLocal,
        string processName,
        int totalDurationSeconds,
        int activeDurationSeconds,
        int idleDurationSeconds,
        int unknownDurationSeconds)
    {
        await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(databasePath);
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
                $ended_at_utc,
                $process_name,
                NULL,
                $total_duration_seconds,
                $active_duration_seconds,
                $idle_duration_seconds,
                $unknown_duration_seconds,
                'Stopped'
            );
            """;

        command.Parameters.AddWithValue("$started_at_utc", startedAtLocal.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$ended_at_utc", endedAtLocal.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$process_name", processName);
        command.Parameters.AddWithValue("$total_duration_seconds", totalDurationSeconds);
        command.Parameters.AddWithValue("$active_duration_seconds", activeDurationSeconds);
        command.Parameters.AddWithValue("$idle_duration_seconds", idleDurationSeconds);
        command.Parameters.AddWithValue("$unknown_duration_seconds", unknownDurationSeconds);
        await command.ExecuteNonQueryAsync();
    }
}
