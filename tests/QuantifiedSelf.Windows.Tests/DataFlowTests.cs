using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using QuantifiedSelf.Windows.Agent.Services;
using QuantifiedSelf.Windows.Agent.State;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.Database;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using QuantifiedSelf.Windows.Infrastructure.Settings;

namespace QuantifiedSelf.Windows.Tests;

public sealed class DataFlowTests
{
    [Fact]
    public async Task AgentControlFileStore_RoundsTripStringEnums_AndFlagsMalformedFiles()
    {
        using var workspace = new TempWorkspace();
        var store = new AgentControlFileStore();
        var controlPath = Path.Combine(workspace.Root, "runtime", "agent_control.json");

        var command = new AgentControlCommand
        {
            Command = AgentCommandType.Pause,
            DesiredState = AgentDesiredState.Paused
        };

        await store.WriteAsync(controlPath, command);

        var raw = await File.ReadAllTextAsync(controlPath);
        Assert.Contains("\"command\": \"Pause\"", raw);
        Assert.Contains("\"desiredState\": \"Paused\"", raw);

        var readResult = await store.PeekAsync(controlPath);
        Assert.False(readResult.WasMalformed);
        Assert.NotNull(readResult.Command);
        Assert.Equal(AgentCommandType.Pause, readResult.Command!.Command);
        Assert.Equal(AgentDesiredState.Paused, readResult.Command.DesiredState);

        await File.WriteAllTextAsync(controlPath, "{ not json");
        var peekResult = await store.PeekAsync(controlPath);

        Assert.True(peekResult.WasMalformed);
        Assert.Null(peekResult.Command);
        Assert.True(File.Exists(controlPath));
        Assert.False(File.Exists(controlPath + ".bad"));

        var agentReadResult = await store.ReadForAgentAsync(controlPath);
        Assert.True(agentReadResult.WasMalformed);
        Assert.Null(agentReadResult.Command);
        Assert.False(File.Exists(controlPath));
        Assert.True(File.Exists(controlPath + ".bad"));
    }

    [Fact]
    public void PrivacyFilter_ExcludesProcesses_AndMasksWindowTitles()
    {
        var filter = new ForegroundSamplePrivacyFilter();
        var options = new WindowsAgentOptions
        {
            MaskWindowTitles = true,
            ExcludedProcesses = ["KeePass"],
            ExcludedTitlePatterns = ["*Secret*"]
        };

        var excludedProcessDecision = filter.Apply(
            new ForegroundSample
            {
                ProcessName = "KeePass",
                WindowTitle = "Vault",
                ActivityState = "Active"
            },
            options);

        Assert.False(excludedProcessDecision.ShouldWriteSample);
        Assert.True(excludedProcessDecision.ShouldCloseOpenSession);
        Assert.Contains("process privacy rule", excludedProcessDecision.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var maskedDecision = filter.Apply(
            new ForegroundSample
            {
                ProcessName = "Code",
                WindowTitle = "Regular Project",
                ActivityState = "Active"
            },
            options);

        Assert.True(maskedDecision.ShouldWriteSample);
        Assert.NotNull(maskedDecision.Sample);
        Assert.Null(maskedDecision.Sample!.WindowTitle);

        var excludedTitleDecision = filter.Apply(
            new ForegroundSample
            {
                ProcessName = "Code",
                WindowTitle = "My Secret Notes",
                ActivityState = "Active"
            },
            options);

        Assert.False(excludedTitleDecision.ShouldWriteSample);
        Assert.True(excludedTitleDecision.ShouldCloseOpenSession);
        Assert.Contains("title privacy rule", excludedTitleDecision.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", excludedTitleDecision.Reason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentStateMachine_SkipsWritingExcludedSamples()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 1,
                HeartbeatIntervalSeconds = 1,
                MaskWindowTitles = true,
                ExcludedProcesses = [],
                ExcludedTitlePatterns = ["*Secret*"]
            });

        var stateMachine = CreateStateMachine(
            paths,
            new QueueForegroundSampleProvider([
                new ForegroundSample
                {
                    SampleTimeUtc = DateTime.UtcNow,
                    ProcessName = "Code",
                    WindowTitle = "My Secret Notes",
                    ActivityState = "Active"
                }
            ]));

        await stateMachine.InitializeAsync(CancellationToken.None);
        await Task.Delay(1100);

        var keepRunning = await stateMachine.TickAsync(CancellationToken.None);
        Assert.True(keepRunning);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM foreground_samples;"));
        Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM app_sessions;"));

        var healthJson = await File.ReadAllTextAsync(paths.HealthStatePath);
        var runtimeJson = await File.ReadAllTextAsync(paths.RuntimeStatePath);
        Assert.DoesNotContain("My Secret Notes", healthJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("My Secret Notes", runtimeJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("title privacy rule", healthJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OverviewQueryService_UsesLocalDayOverlap_AndOrdersByActiveDuration()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var dayStart = DateTime.Now.Date;
        var sessionAStart = dayStart.AddHours(-1);
        var sessionAEnd = dayStart.AddHours(2);
        var sessionBStart = dayStart.AddHours(8);
        var sessionBEnd = dayStart.AddHours(9);
        var yesterdayStart = dayStart.AddDays(-1).AddHours(10);
        var yesterdayEnd = dayStart.AddDays(-1).AddHours(11);

        await InsertSessionAsync(paths.DatabasePath, sessionAStart, sessionAEnd, "Alpha", 1800, 1500, 200, 100, "Closed");
        await InsertSessionAsync(paths.DatabasePath, sessionBStart, sessionBEnd, "Beta", 3600, 900, 1800, 900, "Closed");
        await InsertSessionAsync(paths.DatabasePath, yesterdayStart, yesterdayEnd, "Ignored", 900, 900, 0, 0, "Closed");

        var queryService = new OverviewQueryService(paths.DatabasePath);

        var summary = await queryService.GetTodaySummaryAsync();
        Assert.Equal(2, summary.SessionCount);
        Assert.Equal(4800, summary.TotalDurationSeconds);
        Assert.Equal(1900, summary.ActiveDurationSeconds);
        Assert.Equal(1933, summary.IdleDurationSeconds);
        Assert.Equal(967, summary.UnknownDurationSeconds);

        var topApps = await queryService.GetTopAppsTodayAsync(5);
        Assert.Equal(2, topApps.Count);
        Assert.Equal("Alpha", topApps[0].ProcessName);
        Assert.Equal(1000, topApps[0].ActiveDurationSeconds);
        Assert.Equal("Beta", topApps[1].ProcessName);
        Assert.Equal(900, topApps[1].ActiveDurationSeconds);
    }

    private static AgentStateMachine CreateStateMachine(
        WindowsAgentPaths paths,
        IForegroundSampleProvider sampleProvider)
    {
        var runtimeStateStore = new RuntimeStateStore();
        var healthStateStore = new AgentHealthStateStore();
        var controlFileStore = new AgentControlFileStore();
        var optionsStore = new WindowsAgentOptionsStore();
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        var sampleRepository = new ForegroundSampleRepository(paths.DatabasePath);
        var sessionAggregator = new SessionAggregator(new AppSessionRepository(paths.DatabasePath));
        var privacyFilter = new ForegroundSamplePrivacyFilter();

        return new AgentStateMachine(
            paths,
            runtimeStateStore,
            healthStateStore,
            controlFileStore,
            optionsStore,
            initializer,
            sampleRepository,
            sessionAggregator,
            privacyFilter,
            sampleProvider,
            NullLogger<AgentStateMachine>.Instance);
    }

    private static async Task InsertSessionAsync(
        string databasePath,
        DateTime startedAtLocal,
        DateTime endedAtLocal,
        string processName,
        int totalDurationSeconds,
        int activeDurationSeconds,
        int idleDurationSeconds,
        int unknownDurationSeconds,
        string closeReason)
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
                $window_title,
                $total_duration_seconds,
                $active_duration_seconds,
                $idle_duration_seconds,
                $unknown_duration_seconds,
                $close_reason
            );
            """;

        command.Parameters.AddWithValue("$started_at_utc", startedAtLocal.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$ended_at_utc", endedAtLocal.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$process_name", processName);
        command.Parameters.AddWithValue("$window_title", DBNull.Value);
        command.Parameters.AddWithValue("$total_duration_seconds", totalDurationSeconds);
        command.Parameters.AddWithValue("$active_duration_seconds", activeDurationSeconds);
        command.Parameters.AddWithValue("$idle_duration_seconds", idleDurationSeconds);
        command.Parameters.AddWithValue("$unknown_duration_seconds", unknownDurationSeconds);
        command.Parameters.AddWithValue("$close_reason", closeReason);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value);
    }

    private sealed class QueueForegroundSampleProvider : IForegroundSampleProvider
    {
        private readonly Queue<ForegroundSample> _samples;

        public QueueForegroundSampleProvider(IEnumerable<ForegroundSample> samples)
        {
            _samples = new Queue<ForegroundSample>(samples);
        }

        public ForegroundSample Capture()
        {
            if (_samples.Count == 0)
            {
                throw new InvalidOperationException("No samples left.");
            }

            return _samples.Dequeue();
        }
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "qsw-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }
}
