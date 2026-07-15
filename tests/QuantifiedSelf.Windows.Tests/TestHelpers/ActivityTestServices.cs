using QuantifiedSelf.Windows.ApplicationLayer.Activity;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Database;

namespace QuantifiedSelf.Windows.Tests.TestHelpers;

internal static class ActivityTestServices
{
    public static OverviewDataService CreateOverview(WindowsAgentPaths paths)
    {
        var queries = CreateQueries(paths);
        return new OverviewDataService(queries, queries);
    }

    public static DiagnosticsDataService CreateDiagnostics(WindowsAgentPaths paths) =>
        new(CreateQueries(paths));

    public static SamplesDataService CreateSamples(WindowsAgentPaths paths) =>
        new(CreateQueries(paths));

    public static SessionsDataService CreateSessions(WindowsAgentPaths paths) =>
        new(CreateQueries(paths));

    public static AppsDataService CreateApps(WindowsAgentPaths paths) =>
        new(CreateQueries(paths));

    public static DailyStatsService CreateDailyStats(WindowsAgentPaths paths)
    {
        var queries = CreateQueries(paths);
        return new DailyStatsService(queries, queries);
    }

    public static WeeklyTrendService CreateWeeklyTrend(WindowsAgentPaths paths) =>
        new(CreateDailyStats(paths));

    public static FocusInterruptionInsightService CreateInsights(WindowsAgentPaths paths) =>
        new(CreateQueries(paths));

    public static HourActivityHeatmapService CreateHeatmap(WindowsAgentPaths paths) =>
        new(CreateQueries(paths));

    private static SqliteActivityQueryAdapter CreateQueries(WindowsAgentPaths paths) => new(paths);
}
