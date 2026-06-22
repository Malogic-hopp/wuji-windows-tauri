using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.App.ViewModels;
using QuantifiedSelf.Windows.Agent.Events;
using QuantifiedSelf.Windows.Agent.Services;
using QuantifiedSelf.Windows.Agent.State;
using QuantifiedSelf.Windows.Core.Capture;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.Database;
using QuantifiedSelf.Windows.Infrastructure.Events;
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
    public void DiagnosticMessageSanitizer_RedactsWindowsAndUncPaths()
    {
        var exception = new InvalidOperationException(
            @"Failed to open C:\Users\Alice\secrets\db.sqlite and \\server\share\logs\agent.log");

        var message = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(exception);

        Assert.DoesNotContain(@"C:\Users\Alice\secrets\db.sqlite", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\server\share\logs\agent.log", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<path>", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentEventPayloadSanitizer_RemovesForbiddenKeys_AndRedactsStringValues()
    {
        var payloadJson = AgentEventPayloadSanitizer.CreatePayloadJson(
            new Dictionary<string, object?>
            {
                ["errorCode"] = "SampleWriteFailed",
                ["exceptionType"] = "InvalidOperationException",
                ["shortMessage"] = @"Failed at C:\Users\Alice\secrets\db.sqlite",
                ["windowTitle"] = "Secret Bank Account",
                ["rawJson"] = "{ secret: true }",
                ["executablePath"] = @"C:\Users\Alice\AppData\Local\app.exe"
            },
            "errorCode",
            "exceptionType",
            "shortMessage");

        Assert.NotNull(payloadJson);
        Assert.Contains("\"errorCode\": \"SampleWriteFailed\"", payloadJson);
        Assert.Contains("\"exceptionType\": \"InvalidOperationException\"", payloadJson);
        Assert.Contains("\\u003Cpath\\u003E", payloadJson);
        Assert.DoesNotContain("Secret Bank Account", payloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rawJson", payloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("executablePath", payloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Users\Alice", payloadJson, StringComparison.OrdinalIgnoreCase);
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

        var dayStart = DateTime.Now.Date;
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(9), dayStart.AddHours(9).AddMinutes(10), "Code", 600, 600, 0, 0, "Closed");
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(10), dayStart.AddHours(10).AddMinutes(20), "QuantifiedSelf.Windows.Agent", 1200, 1200, 0, 0, "Closed");
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(11), dayStart.AddHours(11).AddMinutes(30), "QuantifiedSelf.Windows.App", 1800, 1800, 0, 0, "Closed");

        var overviewDataService = new OverviewDataService(paths);

        var topApps = await overviewDataService.GetTopAppsTodayAsync(5);
        Assert.Equal(3, topApps.Count);
        Assert.Equal("WUJI", topApps[0].DisplayName);
        Assert.Equal("WUJI Agent", topApps[1].DisplayName);
        Assert.Equal("Code", topApps[2].DisplayName);

        var recentSessions = await overviewDataService.GetRecentSessionsAsync(3);
        Assert.Equal(3, recentSessions.Count);
        Assert.Equal("WUJI", recentSessions[0].DisplayName);
        Assert.Equal("WUJI Agent", recentSessions[1].DisplayName);
        Assert.Equal("Code", recentSessions[2].DisplayName);
    }

    [Fact]
    public async Task SqliteDatabaseInitializer_CreatesAgentEventsTableAndIndexes()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);

        await initializer.InitializeAsync();

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        var columns = await GetColumnsAsync(connection, "agent_events");
        Assert.Contains("id", columns);
        Assert.Contains("event_time_utc", columns);
        Assert.Contains("event_type", columns);
        Assert.Contains("event_level", columns);
        Assert.Contains("message", columns);
        Assert.Contains("payload_json", columns);

        var indexNames = await GetIndexNamesAsync(connection, "agent_events");
        Assert.Contains("idx_agent_events_time", indexNames);
        Assert.Contains("idx_agent_events_type", indexNames);
        Assert.Contains("idx_agent_events_level_time", indexNames);
    }

    [Fact]
    public async Task AgentEventRepository_InsertAndGetRecentAsync_UsesStableOrdering()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var repository = new AgentEventRepository(paths.DatabasePath);
        var eventTime = new DateTime(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc);

        var first = new AgentEvent
        {
            EventTimeUtc = eventTime,
            EventType = AgentEventType.AgentStarted,
            EventLevel = AgentEventLevel.Info,
            Message = "First event"
        };

        var second = new AgentEvent
        {
            EventTimeUtc = eventTime,
            EventType = AgentEventType.AgentStopped,
            EventLevel = AgentEventLevel.Warning,
            Message = "Second event"
        };

        await repository.InsertAsync(first);
        await repository.InsertAsync(second);

        var recent = await repository.GetRecentAsync(10);
        Assert.Equal(2, recent.Count);
        Assert.Equal(second.Id, recent[0].Id);
        Assert.Equal(first.Id, recent[1].Id);
        Assert.Equal("Second event", recent[0].Message);
        Assert.Equal("First event", recent[1].Message);
    }

    [Fact]
    public async Task AgentEventRepository_GetRecentErrorsAsync_FiltersToWarningAndAbove()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var repository = new AgentEventRepository(paths.DatabasePath);
        var eventTime = new DateTime(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc);

        await repository.InsertAsync(new AgentEvent
        {
            EventTimeUtc = eventTime.AddMinutes(-3),
            EventType = AgentEventType.AgentStarted,
            EventLevel = AgentEventLevel.Info,
            Message = "Info event"
        });
        await repository.InsertAsync(new AgentEvent
        {
            EventTimeUtc = eventTime.AddMinutes(-2),
            EventType = AgentEventType.CaptureFailed,
            EventLevel = AgentEventLevel.Warning,
            Message = "Warning event"
        });
        await repository.InsertAsync(new AgentEvent
        {
            EventTimeUtc = eventTime.AddMinutes(-1),
            EventType = AgentEventType.CommandFailed,
            EventLevel = AgentEventLevel.Error,
            Message = "Error event"
        });

        var recentErrors = await repository.GetRecentErrorsAsync(10);
        Assert.Equal(2, recentErrors.Count);
        Assert.DoesNotContain(recentErrors, x => x.EventLevel == AgentEventLevel.Info);
        Assert.Equal("Error event", recentErrors[0].Message);
        Assert.Equal("Warning event", recentErrors[1].Message);
    }

    [Fact]
    public async Task DiagnosticsQueryService_ReturnsEmptyListsWhenAgentEventsTableIsEmpty()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var queryService = new DiagnosticsQueryService(paths.DatabasePath);
        var recentEvents = await queryService.GetRecentEventsAsync();
        var recentErrors = await queryService.GetRecentErrorsAsync();

        Assert.Empty(recentEvents);
        Assert.Empty(recentErrors);
    }

    [Fact]
    public async Task AgentEventRepository_ReturnsEmptyListsWhenAgentEventsTableIsMissing()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        await using (var connection = await SqliteConnectionFactory.OpenAsync(paths.DatabasePath, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE legacy_state (id INTEGER PRIMARY KEY AUTOINCREMENT, value TEXT NOT NULL);";
            await command.ExecuteNonQueryAsync();
        }

        var repository = new AgentEventRepository(paths.DatabasePath);
        var recentEvents = await repository.GetRecentAsync();
        var recentErrors = await repository.GetRecentErrorsAsync();

        Assert.Empty(recentEvents);
        Assert.Empty(recentErrors);
    }

    [Fact]
    public async Task AgentEventJournal_WritesJsonLineWithStringEnums()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var journal = new AgentEventJournal(paths);
        var agentEvent = new AgentEvent
        {
            EventTimeUtc = new DateTime(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc),
            EventType = AgentEventType.CommandDetected,
            EventLevel = AgentEventLevel.Info,
            Message = "Command detected",
            Source = "AgentStateMachine",
            RequestId = "request-1",
            ErrorCode = null,
            ProcessName = "QuantifiedSelf.Windows.Agent",
            SessionId = 42,
            PayloadJson = "{\"commandSource\":\"FileFallback\"}"
        };

        await journal.AppendAsync(agentEvent);

        var journalPath = journal.GetJournalPath(agentEvent.EventTimeUtc);
        var lines = await File.ReadAllLinesAsync(journalPath);
        Assert.Single(lines);
        Assert.Contains("\"eventType\":\"CommandDetected\"", lines[0]);
        Assert.Contains("\"eventLevel\":\"Info\"", lines[0]);
        Assert.Contains("\"requestId\":\"request-1\"", lines[0]);
    }

    [Fact]
    public async Task AgentEventWriter_ContinuesWhenDatabaseWriteFails()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var badDatabasePath = Path.Combine(paths.Root, "event-db");
        Directory.CreateDirectory(badDatabasePath);

        var eventRepository = new AgentEventRepository(badDatabasePath);
        var journal = new AgentEventJournal(paths);
        var writer = new AgentEventWriter(eventRepository, journal);

        var agentEvent = new AgentEvent
        {
            EventTimeUtc = DateTime.UtcNow,
            EventType = AgentEventType.AgentStarted,
            EventLevel = AgentEventLevel.Info,
            Message = "Agent started"
        };

        await writer.WriteAsync(agentEvent);

        Assert.NotNull(writer.LastEventWriteError);
        Assert.True(writer.EventWriteErrorCount > 0);
        Assert.True(File.Exists(journal.GetJournalPath(agentEvent.EventTimeUtc)));
    }

    [Fact]
    public async Task AgentEventWriter_ContinuesWhenJournalWriteFails()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        Directory.Delete(paths.LogsDir);
        await File.WriteAllTextAsync(paths.LogsDir, "blocked");

        var eventRepository = new AgentEventRepository(paths.DatabasePath);
        var journal = new AgentEventJournal(paths);
        var writer = new AgentEventWriter(eventRepository, journal);

        var agentEvent = new AgentEvent
        {
            EventTimeUtc = DateTime.UtcNow,
            EventType = AgentEventType.AgentStarted,
            EventLevel = AgentEventLevel.Info,
            Message = "Agent started"
        };

        await writer.WriteAsync(agentEvent);

        Assert.NotNull(writer.LastJournalWriteError);
        Assert.True(writer.JournalWriteErrorCount > 0);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_events;";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public void AgentEventRateLimiter_SuppressesRepeatedEventsWithinWindow()
    {
        var limiter = new AgentEventRateLimiter();
        var utcNow = new DateTime(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(limiter.ShouldAllow("PrivacyFiltered:Code", utcNow.AddSeconds(i)));
        }

        Assert.False(limiter.ShouldAllow("PrivacyFiltered:Code", utcNow.AddSeconds(30)));
        Assert.True(limiter.ShouldAllow("PrivacyFiltered:Other", utcNow.AddSeconds(30)));
    }

    [Fact]
    public async Task AgentStateMachine_WritesPauseCommandEvents()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 3600,
                HeartbeatIntervalSeconds = 3600,
                UseMockCapture = true
            });

        var controlFileStore = new AgentControlFileStore();
        await controlFileStore.WriteAsync(
            paths.AgentControlPath,
            new AgentControlCommand
            {
                Command = AgentCommandType.Pause,
                DesiredState = AgentDesiredState.Paused,
                RequestId = "pause-1",
                RequestedBy = "QuantifiedSelf.Windows.App"
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);
        await stateMachine.TickAsync(CancellationToken.None);

        var events = await ReadEventsAsync(paths.DatabasePath);
        var eventTypes = events.Select(x => x.EventType).ToArray();

        Assert.Contains(AgentEventType.CommandDetected, eventTypes);
        Assert.Contains(AgentEventType.CommandAccepted, eventTypes);
        Assert.Contains(AgentEventType.CommandCompleted, eventTypes);
        Assert.Contains(AgentEventType.AgentPaused, eventTypes);

        var commandDetected = events.Single(x => x.EventType == AgentEventType.CommandDetected);
        Assert.Equal("pause-1", commandDetected.RequestId);
        Assert.Contains("\"commandSource\": \"FileFallback\"", commandDetected.PayloadJson ?? string.Empty);
    }

    [Fact]
    public async Task AgentStateMachine_WritesResumeAndStopLifecycleEvents()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 3600,
                HeartbeatIntervalSeconds = 3600,
                UseMockCapture = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);
        await stateMachine.ProcessCommandAsync(
            new AgentControlCommand
            {
                Command = AgentCommandType.Pause,
                DesiredState = AgentDesiredState.Paused,
                RequestId = "pause-lifecycle"
            },
            CancellationToken.None);
        await stateMachine.ProcessCommandAsync(
            new AgentControlCommand
            {
                Command = AgentCommandType.Resume,
                DesiredState = AgentDesiredState.Running,
                RequestId = "resume-lifecycle"
            },
            CancellationToken.None);
        await stateMachine.ProcessCommandAsync(
            new AgentControlCommand
            {
                Command = AgentCommandType.Stop,
                DesiredState = AgentDesiredState.Stopped,
                RequestId = "stop-lifecycle"
            },
            CancellationToken.None);

        var events = await ReadEventsAsync(paths.DatabasePath);
        var eventTypes = events.Select(x => x.EventType).ToArray();

        Assert.Contains(AgentEventType.AgentPaused, eventTypes);
        Assert.Contains(AgentEventType.AgentResumed, eventTypes);
        Assert.Contains(AgentEventType.AgentStopped, eventTypes);
        Assert.Contains(events, x => x.EventType == AgentEventType.CommandCompleted && x.RequestId == "resume-lifecycle");
        Assert.Contains(events, x => x.EventType == AgentEventType.CommandCompleted && x.RequestId == "stop-lifecycle");
    }

    [Fact]
    public async Task AgentStateMachine_WritesConfigReloadedEvent()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 3600,
                HeartbeatIntervalSeconds = 3600,
                UseMockCapture = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);
        await stateMachine.ProcessCommandAsync(
            new AgentControlCommand
            {
                Command = AgentCommandType.ReloadConfig,
                RequestId = "reload-config"
            },
            CancellationToken.None);

        var events = await ReadEventsAsync(paths.DatabasePath);
        var configReloaded = Assert.Single(events.Where(x => x.EventType == AgentEventType.ConfigReloaded));

        Assert.Equal(AgentEventLevel.Info, configReloaded.EventLevel);
        Assert.Contains("\"actualState\": \"Running\"", configReloaded.PayloadJson ?? string.Empty);
        Assert.Contains(events, x => x.EventType == AgentEventType.CommandCompleted && x.RequestId == "reload-config");
    }

    [Fact]
    public async Task AgentStateMachine_WritesInvalidJsonCommandEvents()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 3600,
                HeartbeatIntervalSeconds = 3600,
                UseMockCapture = true
            });

        await File.WriteAllTextAsync(paths.AgentControlPath, "{ not json");

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);
        await stateMachine.TickAsync(CancellationToken.None);

        var events = await ReadEventsAsync(paths.DatabasePath);
        var invalidJsonEvent = events.Single(x => x.EventType == AgentEventType.CommandInvalidJson);
        Assert.True(string.IsNullOrWhiteSpace(invalidJsonEvent.RequestId));
        Assert.Equal("CommandInvalidJson", invalidJsonEvent.ErrorCode);
        Assert.Contains("\"commandSource\": \"FileFallback\"", invalidJsonEvent.PayloadJson ?? string.Empty);
        Assert.Contains("\"quarantined\": true", invalidJsonEvent.PayloadJson ?? string.Empty);
    }

    [Fact]
    public async Task AgentStateMachine_WritesCommandFailedEventForUnsupportedCommand()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 3600,
                HeartbeatIntervalSeconds = 3600,
                UseMockCapture = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);
        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand
            {
                Command = (AgentCommandType)999,
                RequestId = "bad-command"
            },
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("UnsupportedCommand", result.ErrorCode);

        var events = await ReadEventsAsync(paths.DatabasePath);
        var failedEvent = events.Single(x => x.EventType == AgentEventType.CommandFailed);
        Assert.Equal("bad-command", failedEvent.RequestId);
        Assert.Equal("UnsupportedCommand", failedEvent.ErrorCode);
    }

    [Fact]
    public async Task AgentStateMachine_WritesSessionStartedAndClosedEvents()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 1,
                HeartbeatIntervalSeconds = 1,
                UseMockCapture = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "Code",
                        WindowTitle = "Workspace",
                        IdleSeconds = 0,
                        ActivityState = "Active"
                    },
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow.AddSeconds(2),
                        ProcessName = "Browser",
                        WindowTitle = "Browser Window",
                        IdleSeconds = 0,
                        ActivityState = "Active"
                    }
                ]),
                new QueueWin32ForegroundSampleProvider([])),
            eventWriter: eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);
        await Task.Delay(1100);
        await stateMachine.TickAsync(CancellationToken.None);
        await Task.Delay(1100);
        await stateMachine.TickAsync(CancellationToken.None);

        var events = await ReadEventsAsync(paths.DatabasePath);
        Assert.Equal(2, events.Count(x => x.EventType == AgentEventType.SessionStarted));
        Assert.Equal(1, events.Count(x => x.EventType == AgentEventType.SessionClosed));

        var closedEvent = events.Single(x => x.EventType == AgentEventType.SessionClosed);
        Assert.Contains("\"closeReason\": \"ProcessChanged\"", closedEvent.PayloadJson ?? string.Empty);
        Assert.Contains("\"startedAtUtc\":", events.First(x => x.EventType == AgentEventType.SessionStarted).PayloadJson ?? string.Empty);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        Assert.Equal(2, await CountAsync(connection, "SELECT COUNT(*) FROM app_sessions;"));
    }

    [Fact]
    public async Task AgentStateMachine_WritesPrivacyFilteredEvents_AndRateLimitsThem()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 1,
                HeartbeatIntervalSeconds = 1,
                UseMockCapture = true,
                ExcludedTitlePatterns = ["*Secret*"]
            });

        var samples = Enumerable.Range(0, 6)
            .Select(_ => new ForegroundSample
            {
                SampleTimeUtc = DateTime.UtcNow,
                ProcessName = "Code",
                WindowTitle = "My Secret Notes",
                IdleSeconds = 0,
                ActivityState = "Active"
            })
            .ToArray();

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider(samples),
                new QueueWin32ForegroundSampleProvider([])),
            eventWriter: eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);

        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(1100);
            await stateMachine.TickAsync(CancellationToken.None);
        }

        var events = await ReadEventsAsync(paths.DatabasePath);
        var privacyEvents = events.Where(x => x.EventType == AgentEventType.PrivacyFiltered).ToList();

        Assert.Equal(5, privacyEvents.Count);
        Assert.All(privacyEvents, x => Assert.Contains("Sample filtered by privacy", x.Message));
        Assert.All(privacyEvents, x => Assert.DoesNotContain("Secret", x.PayloadJson ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"ruleType\": \"Title\"", privacyEvents[0].PayloadJson ?? string.Empty);
        Assert.Contains("\"processName\": \"Code\"", privacyEvents[0].PayloadJson ?? string.Empty);
        Assert.Contains("\"privacyReason\": \"Excluded by title privacy rule\"", privacyEvents[0].PayloadJson ?? string.Empty);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM foreground_samples;"));
        Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM app_sessions;"));
    }

    [Fact]
    public async Task AgentStateMachine_RateLimitsPrivacyFilteredEventsByRuleTypeAndProcessName()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 1,
                HeartbeatIntervalSeconds = 1,
                UseMockCapture = true,
                ExcludedProcesses = ["KeePass"],
                ExcludedTitlePatterns = ["*Secret*"]
            });

        var samples = new[]
        {
            new ForegroundSample { SampleTimeUtc = DateTime.UtcNow, ProcessName = "Code", WindowTitle = "My Secret Notes", IdleSeconds = 0, ActivityState = "Active" },
            new ForegroundSample { SampleTimeUtc = DateTime.UtcNow, ProcessName = "Code", WindowTitle = "My Secret Notes", IdleSeconds = 0, ActivityState = "Active" },
            new ForegroundSample { SampleTimeUtc = DateTime.UtcNow, ProcessName = "Code", WindowTitle = "My Secret Notes", IdleSeconds = 0, ActivityState = "Active" },
            new ForegroundSample { SampleTimeUtc = DateTime.UtcNow, ProcessName = "Code", WindowTitle = "My Secret Notes", IdleSeconds = 0, ActivityState = "Active" },
            new ForegroundSample { SampleTimeUtc = DateTime.UtcNow, ProcessName = "Code", WindowTitle = "My Secret Notes", IdleSeconds = 0, ActivityState = "Active" },
            new ForegroundSample { SampleTimeUtc = DateTime.UtcNow, ProcessName = "KeePass", WindowTitle = "Vault", IdleSeconds = 0, ActivityState = "Active" }
        };

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider(samples),
                new QueueWin32ForegroundSampleProvider([])),
            eventWriter: eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);

        for (var i = 0; i < samples.Length; i++)
        {
            await Task.Delay(1100);
            await stateMachine.TickAsync(CancellationToken.None);
        }

        var events = await ReadEventsAsync(paths.DatabasePath);
        var privacyEvents = events.Where(x => x.EventType == AgentEventType.PrivacyFiltered).ToList();

        Assert.Equal(6, privacyEvents.Count);
        Assert.Equal(5, privacyEvents.Count(x => (x.PayloadJson ?? string.Empty).Contains("\"ruleType\": \"Title\"", StringComparison.Ordinal)));
        Assert.Equal(1, privacyEvents.Count(x => (x.PayloadJson ?? string.Empty).Contains("\"ruleType\": \"Process\"", StringComparison.Ordinal)));
        Assert.Contains(privacyEvents, x => (x.PayloadJson ?? string.Empty).Contains("\"processName\": \"KeePass\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentStateMachine_WritesCaptureFailedEvents_AndRateLimitsThem()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 1,
                HeartbeatIntervalSeconds = 1,
                UseMockCapture = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]),
                new QueueWin32ForegroundSampleProvider([])),
            eventWriter: eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);
        await Task.Delay(1100);

        for (var i = 0; i < 6; i++)
        {
            await stateMachine.TickAsync(CancellationToken.None);
        }

        var events = await ReadEventsAsync(paths.DatabasePath);
        var failedEvents = events.Where(x => x.EventType == AgentEventType.CaptureFailed).ToList();

        Assert.Equal(5, failedEvents.Count);
        Assert.All(failedEvents, x => Assert.Equal("ForegroundWindowUnavailable", x.ErrorCode));
        Assert.Contains("Foreground window capture failed", failedEvents[0].Message);
        Assert.Contains("\"errorCode\": \"ForegroundWindowUnavailable\"", failedEvents[0].PayloadJson ?? string.Empty);
        Assert.Contains("\"exceptionType\": \"InvalidOperationException\"", failedEvents[0].PayloadJson ?? string.Empty);
        Assert.Contains("\"shortMessage\": \"No samples left.\"", failedEvents[0].PayloadJson ?? string.Empty);
    }

    [Fact]
    public async Task AgentStateMachine_SanitizesPathLikeCaptureFailures()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 1,
                HeartbeatIntervalSeconds = 1,
                UseMockCapture = true
            });

        var throwingProvider = new ConfiguredForegroundSampleProvider(
            new PathThrowingMockForegroundSampleProvider(),
            new QueueWin32ForegroundSampleProvider([]));

        var stateMachine = CreateStateMachine(
            paths,
            throwingProvider,
            eventWriter: eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);
        await Task.Delay(1100);
        await stateMachine.TickAsync(CancellationToken.None);

        var healthJson = await File.ReadAllTextAsync(paths.HealthStatePath);
        var events = await ReadEventsAsync(paths.DatabasePath);
        var captureFailed = Assert.Single(events.Where(x => x.EventType == AgentEventType.CaptureFailed));

        Assert.DoesNotContain(@"C:\Users\Alice\secrets\db.sqlite", healthJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\server\share\logs\agent.log", healthJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Users\Alice\secrets\db.sqlite", captureFailed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\server\share\logs\agent.log", captureFailed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\Users\Alice\secrets\db.sqlite", captureFailed.PayloadJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\server\share\logs\agent.log", captureFailed.PayloadJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnosticsDataService_ReturnsCurrentJournalPathForToday()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var diagnosticsDataService = new DiagnosticsDataService(paths);

        var journalPath = diagnosticsDataService.GetCurrentJournalPath(new DateTime(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal("agent_events_20260619.jsonl", Path.GetFileName(journalPath));
        Assert.EndsWith(Path.Combine("logs", "agent_events_20260619.jsonl"), journalPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiagnosticsQueryService_DoesNotParseJsonl()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();
        await File.WriteAllTextAsync(
            Path.Combine(paths.LogsDir, "agent_events_20260619.jsonl"),
            "{\"eventType\":\"CommandFailed\",\"eventLevel\":\"Error\",\"message\":\"JSONL only\"}");

        var queryService = new DiagnosticsQueryService(paths.DatabasePath);
        var recentEvents = await queryService.GetRecentEventsAsync();
        var recentErrors = await queryService.GetRecentErrorsAsync();

        Assert.Empty(recentEvents);
        Assert.Empty(recentErrors);
    }

    [Fact]
    public async Task AgentStateMachine_DoesNotWriteHighFrequencySampleOrHeartbeatEvents()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 1,
                HeartbeatIntervalSeconds = 1,
                UseMockCapture = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([
                    new ForegroundSample { SampleTimeUtc = DateTime.UtcNow, ProcessName = "Code", WindowTitle = "One", IdleSeconds = 0, ActivityState = "Active" },
                    new ForegroundSample { SampleTimeUtc = DateTime.UtcNow.AddSeconds(2), ProcessName = "Code", WindowTitle = "Two", IdleSeconds = 0, ActivityState = "Active" },
                    new ForegroundSample { SampleTimeUtc = DateTime.UtcNow.AddSeconds(4), ProcessName = "Code", WindowTitle = "Three", IdleSeconds = 0, ActivityState = "Active" }
                ]),
                new QueueWin32ForegroundSampleProvider([])),
            eventWriter: eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(1100);
            await stateMachine.TickAsync(CancellationToken.None);
        }

        var events = await ReadEventsAsync(paths.DatabasePath);

        Assert.DoesNotContain(events, x => x.EventType.ToString().Equals("SampleCaptured", StringComparison.Ordinal));
        Assert.DoesNotContain(events, x => x.EventType.ToString().Equals("Heartbeat", StringComparison.Ordinal));
        Assert.Equal(1, events.Count(x => x.EventType == AgentEventType.AgentStarted));
        Assert.Equal(1, events.Count(x => x.EventType == AgentEventType.SessionStarted));

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        Assert.Equal(3, await CountAsync(connection, "SELECT COUNT(*) FROM foreground_samples;"));
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
        services.AddSingleton<AgentEventRepository>(sp => new AgentEventRepository(sp.GetRequiredService<WindowsAgentPaths>().DatabasePath));
        services.AddSingleton<AgentEventJournal>();
        services.AddSingleton<AgentEventWriter>();
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
        var eventWriter = provider.GetRequiredService<AgentEventWriter>();
        var stateMachine = provider.GetRequiredService<AgentStateMachine>();

        Assert.NotNull(idleDetector);
        Assert.NotNull(capturePipeline);
        Assert.NotNull(eventWriter);
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
    public async Task SampleQueryService_ReturnsRecentSamplesWithStableOrderingAndLimit()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var sampleTime = DateTime.UtcNow;
        await InsertSampleAsync(paths.DatabasePath, sampleTime.AddMinutes(-1), "Old", "Old title", "Active");
        await InsertSampleAsync(paths.DatabasePath, sampleTime, "Code", "Code title", "Active");
        await InsertSampleAsync(paths.DatabasePath, sampleTime, "QuantifiedSelf.Windows.App", null, "Idle");

        var queryService = new SampleQueryService(paths.DatabasePath);

        var samples = await queryService.GetRecentSamplesAsync(limit: 2);

        Assert.Equal(2, samples.Count);
        Assert.Equal("QuantifiedSelf.Windows.App", samples[0].ProcessName);
        Assert.Equal("WUJI", samples[0].DisplayName);
        Assert.Equal("Code", samples[1].ProcessName);
        Assert.True(samples[0].Id > samples[1].Id);
    }

    [Fact]
    public async Task SamplesViewModel_LoadsRecentSamplesAndFiltersByActivityState()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var sampleTime = DateTime.UtcNow;
        await InsertSampleAsync(paths.DatabasePath, sampleTime.AddMinutes(-2), "Code", "Code title", "Active");
        await InsertSampleAsync(paths.DatabasePath, sampleTime.AddMinutes(-1), "Explorer", null, "Unknown");
        await InsertSampleAsync(paths.DatabasePath, sampleTime, "QuantifiedSelf.Windows.App", null, "Idle");

        var viewModel = new SamplesViewModel(new SamplesDataService(paths));

        await viewModel.LoadAsync();

        Assert.Equal(3, viewModel.Samples.Count);
        Assert.Equal("QuantifiedSelf.Windows.App", viewModel.Samples[0].ProcessName);
        Assert.Equal("WUJI", viewModel.Samples[0].DisplayName);
        Assert.Equal("[Hidden]", viewModel.Samples[0].WindowTitleText);
        Assert.DoesNotContain(
            typeof(SampleListItemViewModel).GetProperties().Select(property => property.Name),
            propertyName => propertyName.Contains("ExecutablePath", StringComparison.OrdinalIgnoreCase));

        viewModel.SelectedActivityState = "Idle";

        Assert.Single(viewModel.Samples);
        Assert.Equal("Idle", viewModel.Samples[0].ActivityState);
        Assert.Contains("Showing 1 of 3", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SamplesViewModel_ReturnsEmptyStateForEmptyDatabase()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var viewModel = new SamplesViewModel(new SamplesDataService(paths));

        await viewModel.LoadAsync();

        Assert.Empty(viewModel.Samples);
        Assert.False(viewModel.HasLoadError);
        Assert.Equal("No samples found.", viewModel.StatusText);
        Assert.Contains("暂无采样记录", viewModel.EmptyStateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SamplesViewModel_RedactsLoadFailureAndShowsErrorState()
    {
        var viewModel = new SamplesViewModel((_, _) =>
            throw new InvalidOperationException(
                @"Failed to open C:\Users\Alice\secrets\db.sqlite and \\server\share\logs\agent.log"));

        await viewModel.LoadAsync();

        Assert.Empty(viewModel.Samples);
        Assert.True(viewModel.HasLoadError);
        Assert.Equal("Samples could not be loaded. Refresh to retry.", viewModel.EmptyStateText);
        Assert.Contains("Samples load failed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<path>", viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\Alice", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\server\share", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionQueryService_ReturnsRecentSessionsWithStableOrderingAndLimit()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var startedAt = DateTime.Now.Date.AddHours(10);
        await InsertSessionAsync(paths.DatabasePath, startedAt.AddHours(-1), startedAt, "Old", 600, 600, 0, 0, "ProcessChanged");
        await InsertSessionAsync(paths.DatabasePath, startedAt, startedAt.AddMinutes(10), "Code", 600, 600, 0, 0, "ProcessChanged");
        await InsertSessionAsync(paths.DatabasePath, startedAt, startedAt.AddMinutes(20), "QuantifiedSelf.Windows.Agent", 1200, 1200, 0, 0, "Stopped");

        var queryService = new SessionQueryService(paths.DatabasePath);

        var sessions = await queryService.GetRecentSessionsAsync(limit: 2);

        Assert.Equal(2, sessions.Count);
        Assert.Equal("QuantifiedSelf.Windows.Agent", sessions[0].ProcessName);
        Assert.Equal("WUJI Agent", sessions[0].DisplayName);
        Assert.Equal("Code", sessions[1].ProcessName);
        Assert.True(sessions[0].Id > sessions[1].Id);
    }

    [Fact]
    public async Task SessionQueryService_ReturnsSessionsOverlappingLocalDay()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var dayStart = DateTime.Now.Date;
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(-1), dayStart.AddHours(1), "CrossMidnight", 7200, 7200, 0, 0, "ProcessChanged");
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(8), dayStart.AddHours(9), "InsideDay", 3600, 1800, 1800, 0, "Paused");
        await InsertOpenSessionAsync(paths.DatabasePath, dayStart.AddHours(10), "OpenApp", 300, 300, 0, 0);
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddDays(-1).AddHours(8), dayStart.AddDays(-1).AddHours(9), "Yesterday", 3600, 3600, 0, 0, "Stopped");
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddDays(1).AddHours(1), dayStart.AddDays(1).AddHours(2), "Tomorrow", 3600, 3600, 0, 0, "Stopped");

        var queryService = new SessionQueryService(paths.DatabasePath);

        var sessions = await queryService.GetSessionsForLocalDayAsync(DateOnly.FromDateTime(dayStart), limit: 10);

        Assert.Equal(["OpenApp", "InsideDay", "CrossMidnight"], sessions.Select(x => x.ProcessName).ToArray());
        Assert.Equal("Open", sessions[0].CloseReason);
        Assert.Null(sessions[0].EndedAtUtc);
    }

    [Fact]
    public async Task AppUsageQueryService_RanksAppsByActiveDurationAndStableTieBreaking()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var dayStart = DateTime.Now.Date;
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(8), dayStart.AddHours(9), "Alpha", 3600, 1200, 2400, 0, "ProcessChanged");
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(9), dayStart.AddHours(10), "Beta", 3600, 1200, 2400, 0, "ProcessChanged");
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(10), dayStart.AddHours(12), "Gamma", 7200, 1200, 6000, 0, "ProcessChanged");
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(-1), dayStart.AddHours(1), "CrossMidnight", 7200, 2000, 5200, 0, "ProcessChanged");
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(12), dayStart.AddHours(13), "QuantifiedSelf.Windows.App", 3600, 900, 2700, 0, "Stopped");
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(13), dayStart.AddHours(14), string.Empty, 3600, 100, 3500, 0, "Stopped");
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddDays(-1).AddHours(13), dayStart.AddDays(-1).AddHours(14), "Ignored", 3600, 3600, 0, 0, "Stopped");

        var queryService = new AppUsageQueryService(paths.DatabasePath);

        var apps = await queryService.GetAppUsageForLocalDayAsync(DateOnly.FromDateTime(dayStart), limit: 10);
        var limitedApps = await queryService.GetAppUsageForLocalDayAsync(DateOnly.FromDateTime(dayStart), limit: 3);

        Assert.Equal(["Gamma", "Alpha", "Beta", "CrossMidnight", "QuantifiedSelf.Windows.App", string.Empty], apps.Select(x => x.ProcessName).ToArray());
        Assert.Equal(["Gamma", "Alpha", "Beta"], limitedApps.Select(x => x.ProcessName).ToArray());
        Assert.Equal(1200, apps[0].ActiveDurationSeconds);
        Assert.Equal(7200, apps[0].TotalDurationSeconds);
        Assert.Equal(1000, apps[3].ActiveDurationSeconds);
        Assert.Equal(3600, apps[3].TotalDurationSeconds);
        Assert.Equal(dayStart.AddHours(1).ToUniversalTime(), apps[3].LastUsedAtUtc);
        Assert.Equal("WUJI", apps[4].DisplayName);
        Assert.Equal("Unknown", apps[5].DisplayName);
        Assert.DoesNotContain(apps, x => x.ProcessName == "Ignored");
    }

    [Fact]
    public async Task DataViewQueryServices_ReturnEmptyListsForMissingDatabaseAndMissingTables()
    {
        using var workspace = new TempWorkspace();
        var missingDatabasePath = Path.Combine(workspace.Root, "missing.db");

        Assert.Empty(await new SampleQueryService(missingDatabasePath).GetRecentSamplesAsync());
        Assert.Empty(await new SessionQueryService(missingDatabasePath).GetRecentSessionsAsync());
        Assert.Empty(await new SessionQueryService(missingDatabasePath).GetSessionsForLocalDayAsync(DateOnly.FromDateTime(DateTime.Now)));
        Assert.Empty(await new AppUsageQueryService(missingDatabasePath).GetAppUsageForLocalDayAsync(DateOnly.FromDateTime(DateTime.Now)));

        var emptyDatabasePath = Path.Combine(workspace.Root, "empty.db");
        await using (await SqliteConnectionFactory.OpenAsync(emptyDatabasePath, SqliteOpenMode.ReadWriteCreate))
        {
        }

        Assert.Empty(await new SampleQueryService(emptyDatabasePath).GetRecentSamplesAsync());
        Assert.Empty(await new SessionQueryService(emptyDatabasePath).GetRecentSessionsAsync());
        Assert.Empty(await new SessionQueryService(emptyDatabasePath).GetSessionsForLocalDayAsync(DateOnly.FromDateTime(DateTime.Now)));
        Assert.Empty(await new AppUsageQueryService(emptyDatabasePath).GetAppUsageForLocalDayAsync(DateOnly.FromDateTime(DateTime.Now)));
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
        ConfiguredForegroundSampleProvider sampleProvider)
    {
        return CreateStateMachine(
            paths,
            sampleProvider,
            eventWriter: null,
            logger: null);
    }

    private static AgentStateMachine CreateStateMachine(
        WindowsAgentPaths paths,
        ConfiguredForegroundSampleProvider sampleProvider,
        ILogger<AgentStateMachine>? logger)
    {
        return CreateStateMachine(
            paths,
            sampleProvider,
            eventWriter: null,
            logger: logger);
    }

    private static AgentStateMachine CreateStateMachine(
        WindowsAgentPaths paths,
        ConfiguredForegroundSampleProvider sampleProvider,
        AgentEventWriter? eventWriter = null,
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

        if (eventWriter is null)
        {
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
            eventWriter,
            logger ?? NullLogger<AgentStateMachine>.Instance);
    }

    private static async Task<AgentEventWriter> CreateEventWriterAsync(WindowsAgentPaths paths)
    {
        paths.EnsureDirectories();
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();
        return new AgentEventWriter(new AgentEventRepository(paths.DatabasePath), new AgentEventJournal(paths));
    }

    private static async Task<List<AgentEvent>> ReadEventsAsync(string databasePath)
    {
        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                event_time_utc,
                event_type,
                event_level,
                message,
                source,
                request_id,
                error_code,
                process_name,
                session_id,
                payload_json
            FROM agent_events
            ORDER BY id ASC;
            """;

        var events = new List<AgentEvent>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            events.Add(new AgentEvent
            {
                Id = reader.GetInt64(0),
                EventTimeUtc = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                EventType = Enum.Parse<AgentEventType>(reader.GetString(2), ignoreCase: true),
                EventLevel = Enum.Parse<AgentEventLevel>(reader.GetString(3), ignoreCase: true),
                Message = reader.GetString(4),
                Source = reader.IsDBNull(5) ? null : reader.GetString(5),
                RequestId = reader.IsDBNull(6) ? null : reader.GetString(6),
                ErrorCode = reader.IsDBNull(7) ? null : reader.GetString(7),
                ProcessName = reader.IsDBNull(8) ? null : reader.GetString(8),
                SessionId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                PayloadJson = reader.IsDBNull(10) ? null : reader.GetString(10)
            });
        }

        return events;
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

    private static async Task InsertOpenSessionAsync(
        string databasePath,
        DateTime startedAtLocal,
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
                NULL,
                $process_name,
                $window_title,
                $total_duration_seconds,
                $active_duration_seconds,
                $idle_duration_seconds,
                $unknown_duration_seconds,
                'Open'
            );
            """;

        command.Parameters.AddWithValue("$started_at_utc", startedAtLocal.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$process_name", processName);
        command.Parameters.AddWithValue("$window_title", DBNull.Value);
        command.Parameters.AddWithValue("$total_duration_seconds", totalDurationSeconds);
        command.Parameters.AddWithValue("$active_duration_seconds", activeDurationSeconds);
        command.Parameters.AddWithValue("$idle_duration_seconds", idleDurationSeconds);
        command.Parameters.AddWithValue("$unknown_duration_seconds", unknownDurationSeconds);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertSampleAsync(
        string databasePath,
        DateTime sampleTimeUtc,
        string processName,
        string? windowTitle,
        string activityState)
    {
        await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO foreground_samples (
                sample_time_utc,
                process_name,
                window_title,
                executable_path,
                idle_seconds,
                activity_state
            )
            VALUES (
                $sample_time_utc,
                $process_name,
                $window_title,
                $executable_path,
                $idle_seconds,
                $activity_state
            );
            """;

        command.Parameters.AddWithValue("$sample_time_utc", sampleTimeUtc.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$process_name", processName);
        command.Parameters.AddWithValue("$window_title", (object?)windowTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$executable_path", DBNull.Value);
        command.Parameters.AddWithValue("$idle_seconds", 0);
        command.Parameters.AddWithValue("$activity_state", activityState);

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

    private static async Task<List<string>> GetIndexNamesAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list({tableName});";

        var indexNames = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexNames.Add(reader.GetString(1));
        }

        return indexNames;
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

    private sealed class PathThrowingMockForegroundSampleProvider : MockForegroundSampleProvider
    {
        public override ForegroundSample Capture()
        {
            throw new InvalidOperationException(
                @"Failed to open C:\Users\Alice\secrets\db.sqlite and \\server\share\logs\agent.log");
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
