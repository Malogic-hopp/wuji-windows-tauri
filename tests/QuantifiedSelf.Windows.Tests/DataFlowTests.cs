using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.Agent.Services;
using QuantifiedSelf.Windows.Agent.State;
using QuantifiedSelf.Windows.Core.Capture;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.Database;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using QuantifiedSelf.Windows.Infrastructure.Settings;
using QuantifiedSelf.Windows.Infrastructure.Win32;

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
                UseMockCapture = true,
                ExcludedProcesses = [],
                ExcludedTitlePatterns = ["*Secret*"]
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "Code",
                        WindowTitle = "My Secret Notes",
                        ActivityState = "Active"
                    }
                ]),
                new QueueWin32ForegroundSampleProvider([
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "Win32App",
                        WindowTitle = "Win32 Window",
                        IdleSeconds = 0,
                        ActivityState = "Active"
                    }
                ])));

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
    public async Task ForegroundSampleRepository_InsertAsync_SetsInsertedSampleId()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var repository = new ForegroundSampleRepository(paths.DatabasePath);
        var sample = new ForegroundSample
        {
            SampleTimeUtc = DateTime.UtcNow,
            ProcessName = "Code",
            WindowTitle = "Window title should not matter here",
            IdleSeconds = 5,
            ActivityState = "Active"
        };

        await repository.InsertAsync(sample);

        Assert.True(sample.Id > 0);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM foreground_samples ORDER BY id DESC LIMIT 1;";
        var databaseId = Convert.ToInt64(await command.ExecuteScalarAsync());

        Assert.Equal(databaseId, sample.Id);
    }

    [Fact]
    public async Task AgentStateMachine_EmitsChineseTerminalSampleLogsWithoutWindowTitle()
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
                IdleThresholdSeconds = 60,
                UseMockCapture = true,
                MaskWindowTitles = false
            });

        var logger = new TestLogger<AgentStateMachine>();
        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "QuantifiedSelf.Windows.App",
                        WindowTitle = "Secret Project Window",
                        IdleSeconds = 7,
                        ActivityState = "Active"
                    }
                ]),
                new QueueWin32ForegroundSampleProvider([])),
            logger);

        await stateMachine.InitializeAsync(CancellationToken.None);
        await Task.Delay(1100);

        var keepRunning = await stateMachine.TickAsync(CancellationToken.None);

        Assert.True(keepRunning);
        var combinedLogs = string.Join(Environment.NewLine, logger.Messages);
        Assert.Contains("采样成功", combinedLogs);
        Assert.Contains("状态=Running", combinedLogs);
        Assert.Contains("前台=WUJI", combinedLogs);
        Assert.DoesNotContain("processName=", combinedLogs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idle=7秒", combinedLogs);
        Assert.Contains("sampleId=1", combinedLogs);
        Assert.Contains("已写入数据库", combinedLogs);
        Assert.DoesNotContain("Secret Project Window", combinedLogs, StringComparison.OrdinalIgnoreCase);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT process_name FROM foreground_samples ORDER BY id DESC LIMIT 1;";
        var storedProcessName = (string?)await command.ExecuteScalarAsync();

        Assert.Equal("QuantifiedSelf.Windows.App", storedProcessName);
    }

    [Fact]
    public async Task AgentStateMachine_UsesMockCaptureWhenConfigured()
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
                UseMockCapture = true,
                MaskWindowTitles = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "MockApp",
                        WindowTitle = "Mock Window",
                        IdleSeconds = 0,
                        ActivityState = "Active"
                    }
                ]),
                new QueueWin32ForegroundSampleProvider([
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "RealApp",
                        WindowTitle = "Real Window",
                        IdleSeconds = 120,
                        ActivityState = "Active"
                    }
                ])));

        await stateMachine.InitializeAsync(CancellationToken.None);
        await Task.Delay(1100);

        var keepRunning = await stateMachine.TickAsync(CancellationToken.None);
        Assert.True(keepRunning);

        await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(paths.DatabasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT process_name FROM foreground_samples ORDER BY id DESC LIMIT 1;";
        var processName = (string?)await command.ExecuteScalarAsync();

        Assert.Equal("MockApp", processName);
    }

    [Fact]
    public async Task AgentStateMachine_UsesWin32CaptureAndClassifiesIdleSamples()
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
                IdleThresholdSeconds = 60,
                UseMockCapture = false,
                MaskWindowTitles = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "MockApp",
                        WindowTitle = "Mock Window",
                        IdleSeconds = 0,
                        ActivityState = "Active"
                    }
                ]),
                new QueueWin32ForegroundSampleProvider([
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "RealApp",
                        WindowTitle = "Real Window",
                        IdleSeconds = 120,
                        ActivityState = "Active"
                    }
                ])));

        await stateMachine.InitializeAsync(CancellationToken.None);
        await Task.Delay(1100);

        var keepRunning = await stateMachine.TickAsync(CancellationToken.None);
        Assert.True(keepRunning);

        await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(paths.DatabasePath);
        await using var sampleCommand = connection.CreateCommand();
        sampleCommand.CommandText = "SELECT activity_state FROM foreground_samples ORDER BY id DESC LIMIT 1;";
        var activityState = (string?)await sampleCommand.ExecuteScalarAsync();

        await using var sessionCommand = connection.CreateCommand();
        sessionCommand.CommandText = "SELECT idle_duration_seconds FROM app_sessions ORDER BY id DESC LIMIT 1;";
        var idleDurationSeconds = Convert.ToInt32(await sessionCommand.ExecuteScalarAsync());

        Assert.Equal("Idle", activityState);
        Assert.Equal(1, idleDurationSeconds);
    }

    [Fact]
    public async Task OverviewDataService_MapsSelfAppNamesToProductDisplayNames()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var now = DateTime.Now;
        await InsertSessionAsync(paths.DatabasePath, now.AddMinutes(-50), now.AddMinutes(-40), "Code", 600, 600, 0, 0, "Closed");
        await InsertSessionAsync(paths.DatabasePath, now.AddMinutes(-35), now.AddMinutes(-25), "QuantifiedSelf.Windows.Agent", 1200, 1200, 0, 0, "Closed");
        await InsertSessionAsync(paths.DatabasePath, now.AddMinutes(-20), now.AddMinutes(-10), "QuantifiedSelf.Windows.App", 1800, 1800, 0, 0, "Closed");

        var overviewDataService = new OverviewDataService(paths);

        var topApps = await overviewDataService.GetTopAppsTodayAsync(5);
        Assert.Equal("WUJI", topApps[0].DisplayName);
        Assert.Equal("WUJI Agent", topApps[1].DisplayName);
        Assert.Equal("Code", topApps[2].DisplayName);

        var recentSessions = await overviewDataService.GetRecentSessionsAsync(3);
        Assert.Equal("WUJI", recentSessions[0].DisplayName);
        Assert.Equal("WUJI Agent", recentSessions[1].DisplayName);
        Assert.Equal("Code", recentSessions[2].DisplayName);
    }

    [Fact]
    public void AgentDi_CanResolveIdleDetectorAndCapturePipeline()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);

        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton<RuntimeStateStore>();
        services.AddSingleton<AgentHealthStateStore>();
        services.AddSingleton<AgentControlFileStore>();
        services.AddSingleton<WindowsAgentOptionsStore>();
        services.AddSingleton<SqliteDatabaseInitializer>(sp => new SqliteDatabaseInitializer(sp.GetRequiredService<WindowsAgentPaths>().DatabasePath));
        services.AddSingleton<ForegroundSampleRepository>(sp => new ForegroundSampleRepository(sp.GetRequiredService<WindowsAgentPaths>().DatabasePath));
        services.AddSingleton<AppSessionRepository>(sp => new AppSessionRepository(sp.GetRequiredService<WindowsAgentPaths>().DatabasePath));
        services.AddSingleton<ForegroundSamplePrivacyFilter>();
        services.AddSingleton<WindowsIdleDetector>();
        services.AddSingleton<IIdleDetector, WindowsIdleDetector>();
        services.AddSingleton<MockForegroundSampleProvider>();
        services.AddSingleton<Win32ForegroundSampleProvider>();
        services.AddSingleton<ConfiguredForegroundSampleProvider>();
        services.AddSingleton<SessionAggregator>();
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<AgentStateMachine>>(NullLogger<AgentStateMachine>.Instance);
        services.AddSingleton<AgentStateMachine>();

        using var provider = services.BuildServiceProvider();

        var idleDetector = provider.GetRequiredService<IIdleDetector>();
        var capturePipeline = provider.GetRequiredService<ConfiguredForegroundSampleProvider>();
        var stateMachine = provider.GetRequiredService<AgentStateMachine>();

        Assert.NotNull(idleDetector);
        Assert.NotNull(capturePipeline);
        Assert.NotNull(stateMachine);
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

    [Fact]
    public async Task SqliteDatabaseInitializer_ResetsLegacySchemaToCurrentShape()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);

        await using (var connection = await SqliteConnectionFactory.OpenAsync(
            paths.DatabasePath,
            Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate))
        {
            await using var createLegacyForeground = connection.CreateCommand();
            createLegacyForeground.CommandText =
                """
                CREATE TABLE foreground_samples (
                    sample_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    captured_at TEXT NOT NULL,
                    window_handle INTEGER NOT NULL,
                    process_id INTEGER NOT NULL,
                    process_name TEXT NOT NULL,
                    executable_path TEXT,
                    window_title TEXT,
                    desktop_state TEXT,
                    is_idle INTEGER NOT NULL,
                    idle_state TEXT NOT NULL,
                    idle_duration_ms INTEGER,
                    capture_status TEXT NOT NULL,
                    error_code TEXT,
                    error_message TEXT
                );

                CREATE TABLE app_sessions (
                    session_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    process_name TEXT NOT NULL,
                    app_name TEXT,
                    start_time TEXT NOT NULL,
                    end_time TEXT NOT NULL,
                    duration_ms INTEGER NOT NULL,
                    active_duration_ms INTEGER NOT NULL DEFAULT 0,
                    idle_duration_ms INTEGER NOT NULL DEFAULT 0,
                    unknown_duration_ms INTEGER NOT NULL DEFAULT 0,
                    is_idle_session INTEGER NOT NULL,
                    merge_reason TEXT NOT NULL,
                    session_end_reason TEXT
                );
                """;

            await createLegacyForeground.ExecuteNonQueryAsync();
        }

        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        await using var verifyConnection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        var foregroundColumns = await GetColumnsAsync(verifyConnection, "foreground_samples");
        var sessionColumns = await GetColumnsAsync(verifyConnection, "app_sessions");

        Assert.Contains("id", foregroundColumns);
        Assert.Contains("id", sessionColumns);
        Assert.DoesNotContain("sample_id", foregroundColumns);
        Assert.DoesNotContain("session_id", sessionColumns);
    }

    private static AgentStateMachine CreateStateMachine(
        WindowsAgentPaths paths,
        ConfiguredForegroundSampleProvider sampleProvider,
        ILogger<AgentStateMachine>? logger = null)
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
            logger ?? NullLogger<AgentStateMachine>.Instance);
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

    private static async Task<List<string>> GetColumnsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private sealed class QueueMockForegroundSampleProvider : MockForegroundSampleProvider
    {
        private readonly Queue<ForegroundSample> _samples;

        public QueueMockForegroundSampleProvider(IEnumerable<ForegroundSample> samples)
        {
            _samples = new Queue<ForegroundSample>(samples);
        }

        public override ForegroundSample Capture()
        {
            if (_samples.Count == 0)
            {
                throw new InvalidOperationException("No samples left.");
            }

            return _samples.Dequeue();
        }
    }

    private sealed class QueueWin32ForegroundSampleProvider : Win32ForegroundSampleProvider
    {
        private readonly Queue<ForegroundSample> _samples;

        public QueueWin32ForegroundSampleProvider(IEnumerable<ForegroundSample> samples)
            : base(new FixedIdleDetector(0))
        {
            _samples = new Queue<ForegroundSample>(samples);
        }

        public override ForegroundSample Capture()
        {
            if (_samples.Count == 0)
            {
                throw new InvalidOperationException("No samples left.");
            }

            return _samples.Dequeue();
        }
    }

    private sealed class FixedIdleDetector : IIdleDetector
    {
        private readonly int _idleSeconds;

        public FixedIdleDetector(int idleSeconds)
        {
            _idleSeconds = idleSeconds;
        }

        public int GetIdleSeconds()
        {
            return _idleSeconds;
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NoopScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NoopScope : IDisposable
        {
            public static NoopScope Instance { get; } = new();

            public void Dispose()
            {
            }
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
