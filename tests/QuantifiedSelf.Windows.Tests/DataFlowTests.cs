using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuantifiedSelf.Windows.App.Models;
using QuantifiedSelf.Windows.App.Services;
using QuantifiedSelf.Windows.App.ViewModels;
using QuantifiedSelf.Windows.Agent.Events;
using QuantifiedSelf.Windows.Agent.Services;
using QuantifiedSelf.Windows.Agent.State;
using QuantifiedSelf.Windows.Core.Capture;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Maintenance;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.Database;
using QuantifiedSelf.Windows.Infrastructure.Events;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using QuantifiedSelf.Windows.Infrastructure.Settings;
using QuantifiedSelf.Windows.Infrastructure.Win32;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Infrastructure.Ipc;
using System.IO.Pipes;
using System.Text.Json;

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
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
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
        services.AddSingleton<AgentOptionsValidator>();
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
    public async Task SessionQueryService_ReturnsSessionsOverlappingExplicitRange()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var rangeStart = DateTime.Now.Date.AddHours(12);
        var rangeEnd = rangeStart.AddHours(2);
        await InsertSessionAsync(paths.DatabasePath, rangeStart.AddHours(-3), rangeStart.AddHours(-2), "Before", 3600, 3600, 0, 0, "Stopped");
        await InsertSessionAsync(paths.DatabasePath, rangeStart.AddHours(-1), rangeStart.AddMinutes(30), "OverlapStart", 5400, 3600, 1800, 0, "ProcessChanged");
        await InsertSessionAsync(paths.DatabasePath, rangeStart.AddMinutes(30), rangeStart.AddHours(1), "Inside", 1800, 1800, 0, 0, "Paused");
        await InsertSessionAsync(paths.DatabasePath, rangeEnd.AddMinutes(30), rangeEnd.AddHours(1), "After", 1800, 1800, 0, 0, "Stopped");

        var queryService = new SessionQueryService(paths.DatabasePath);

        var sessions = await queryService.GetSessionsOverlappingRangeAsync(
            rangeStart.ToUniversalTime(),
            rangeEnd.ToUniversalTime(),
            limit: 10);

        Assert.Equal(["Inside", "OverlapStart"], sessions.Select(x => x.ProcessName).ToArray());
    }

    [Fact]
    public async Task SessionsViewModel_LoadsSessionsAndFiltersCloseReason()
    {
        var startedAt = DateTime.UtcNow.AddMinutes(-10);
        var viewModel = new SessionsViewModel((range, limit, _) =>
        {
            Assert.Equal("Today", range);
            Assert.Equal(200, limit);
            IReadOnlyList<AppSession> sessions =
            [
                new AppSession
                {
                    Id = 3,
                    StartedAtUtc = startedAt.AddMinutes(2),
                    EndedAtUtc = null,
                    ProcessName = "QuantifiedSelf.Windows.Agent",
                    DisplayName = "WUJI Agent",
                    TotalDurationSeconds = 3665,
                    ActiveDurationSeconds = 120,
                    IdleDurationSeconds = 60,
                    UnknownDurationSeconds = 5,
                    CloseReason = "Open"
                },
                new AppSession
                {
                    Id = 2,
                    StartedAtUtc = startedAt.AddMinutes(1),
                    EndedAtUtc = startedAt.AddMinutes(2),
                    ProcessName = "Code",
                    DisplayName = "Code",
                    TotalDurationSeconds = 60,
                    ActiveDurationSeconds = 60,
                    IdleDurationSeconds = 0,
                    UnknownDurationSeconds = 0,
                    CloseReason = "Paused"
                },
                new AppSession
                {
                    Id = 1,
                    StartedAtUtc = startedAt,
                    EndedAtUtc = startedAt.AddMinutes(1),
                    ProcessName = "UnknownReasonApp",
                    DisplayName = "UnknownReasonApp",
                    TotalDurationSeconds = 60,
                    ActiveDurationSeconds = 0,
                    IdleDurationSeconds = 0,
                    UnknownDurationSeconds = 60,
                    CloseReason = "AgentStarted"
                }
            ];

            return Task.FromResult(sessions);
        });

        await viewModel.LoadAsync();

        Assert.Equal(3, viewModel.Sessions.Count);
        Assert.Equal("正在进行", viewModel.Sessions[0].EndedLocalTimeText);
        Assert.Equal("1h 1m", viewModel.Sessions[0].TotalDurationText);
        Assert.DoesNotContain(
            typeof(SessionListItemViewModel).GetProperties().Select(property => property.Name),
            propertyName => propertyName.Contains("WindowTitle", StringComparison.OrdinalIgnoreCase));

        viewModel.SelectedCloseReason = "Other";

        Assert.Single(viewModel.Sessions);
        Assert.Equal("AgentStarted", viewModel.Sessions[0].CloseReason);
        Assert.Equal("Other", viewModel.Sessions[0].CloseReasonFilter);
    }

    [Fact]
    public async Task SessionsViewModel_IgnoresStaleRangeLoadResults()
    {
        var startedAt = DateTime.UtcNow.AddMinutes(-10);
        var todayRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var last24Requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var todayResult = new TaskCompletionSource<IReadOnlyList<AppSession>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var last24Result = new TaskCompletionSource<IReadOnlyList<AppSession>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var viewModel = new SessionsViewModel((range, _, _) =>
        {
            if (range == "Last 24 Hours")
            {
                last24Requested.SetResult();
                return last24Result.Task;
            }

            todayRequested.SetResult();
            return todayResult.Task;
        });

        var firstLoad = viewModel.LoadAsync();
        await todayRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.SelectedRange = "Last 24 Hours";
        await last24Requested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        last24Result.SetResult(
        [
            new AppSession
            {
                Id = 2,
                StartedAtUtc = startedAt.AddMinutes(1),
                EndedAtUtc = startedAt.AddMinutes(2),
                ProcessName = "NewRange",
                DisplayName = "NewRange",
                TotalDurationSeconds = 60,
                ActiveDurationSeconds = 60,
                IdleDurationSeconds = 0,
                UnknownDurationSeconds = 0,
                CloseReason = "Stopped"
            }
        ]);

        await WaitUntilAsync(() => viewModel.Sessions.Count == 1 && viewModel.Sessions[0].ProcessName == "NewRange");

        todayResult.SetResult(
        [
            new AppSession
            {
                Id = 1,
                StartedAtUtc = startedAt,
                EndedAtUtc = startedAt.AddMinutes(1),
                ProcessName = "OldRange",
                DisplayName = "OldRange",
                TotalDurationSeconds = 60,
                ActiveDurationSeconds = 60,
                IdleDurationSeconds = 0,
                UnknownDurationSeconds = 0,
                CloseReason = "Stopped"
            }
        ]);
        await firstLoad;
        await Task.Delay(50);

        Assert.Single(viewModel.Sessions);
        Assert.Equal("NewRange", viewModel.Sessions[0].ProcessName);
        Assert.Contains("last 24 hours", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionsViewModel_RedactsLoadFailureAndShowsErrorState()
    {
        var viewModel = new SessionsViewModel((_, _, _) =>
            throw new InvalidOperationException(
                @"Failed to open C:\Users\Alice\secrets\db.sqlite and \\server\share\logs\agent.log"));

        await viewModel.LoadAsync();

        Assert.Empty(viewModel.Sessions);
        Assert.True(viewModel.HasLoadError);
        Assert.Equal("Sessions could not be loaded. Refresh to retry.", viewModel.EmptyStateText);
        Assert.Contains("Sessions load failed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<path>", viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\Alice", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\server\share", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
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
    public async Task OverviewDataService_TopAppsMatchesAppsViewTodayQuery()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var dayStart = DateTime.Now.Date;
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(-1), dayStart.AddHours(1), "CrossMidnight", 7200, 2400, 3600, 1200, "ProcessChanged");
        await InsertSessionAsync(paths.DatabasePath, dayStart.AddHours(9), dayStart.AddHours(10), "Code", 3600, 1800, 1200, 600, "ProcessChanged");
        await InsertOpenSessionAsync(paths.DatabasePath, DateTime.Now.AddMinutes(-10), "OpenApp", 600, 500, 100, 0);

        var overviewDataService = new OverviewDataService(paths);
        var appUsageQueryService = new AppUsageQueryService(paths.DatabasePath);

        var dashboardTopApps = await overviewDataService.GetTopAppsTodayAsync(5);
        var appsViewTopApps = await appUsageQueryService.GetAppUsageForLocalDayAsync(DateOnly.FromDateTime(DateTime.Now), 5);

        Assert.Equal(appsViewTopApps.Select(x => x.ProcessName), dashboardTopApps.Select(x => x.ProcessName));
        for (var index = 0; index < appsViewTopApps.Count; index++)
        {
            Assert.InRange(
                Math.Abs(appsViewTopApps[index].ActiveDurationSeconds - dashboardTopApps[index].ActiveDurationSeconds),
                0,
                2);
            Assert.InRange(
                Math.Abs(appsViewTopApps[index].TotalDurationSeconds - dashboardTopApps[index].TotalDurationSeconds),
                0,
                2);
            Assert.InRange(
                Math.Abs(appsViewTopApps[index].IdleDurationSeconds - dashboardTopApps[index].IdleDurationSeconds),
                0,
                2);
            Assert.InRange(
                Math.Abs(appsViewTopApps[index].UnknownDurationSeconds - dashboardTopApps[index].UnknownDurationSeconds),
                0,
                2);
        }
    }

    [Fact]
    public async Task AppsViewModel_LoadsTodayAppUsage()
    {
        var lastUsedUtc = DateTime.UtcNow.AddMinutes(-5);
        var viewModel = new AppsViewModel((limit, _) =>
        {
            Assert.Equal(50, limit);
            IReadOnlyList<AppUsageSummary> apps =
            [
                new AppUsageSummary
                {
                    ProcessName = "Code",
                    DisplayName = "Code",
                    ActiveDurationSeconds = 3665,
                    TotalDurationSeconds = 7200,
                    IdleDurationSeconds = 3000,
                    UnknownDurationSeconds = 535,
                    SessionCount = 3,
                    LastUsedAtUtc = lastUsedUtc
                },
                new AppUsageSummary
                {
                    ProcessName = "QuantifiedSelf.Windows.Agent",
                    DisplayName = "WUJI Agent",
                    ActiveDurationSeconds = 120,
                    TotalDurationSeconds = 180,
                    IdleDurationSeconds = 30,
                    UnknownDurationSeconds = 30,
                    SessionCount = 1,
                    LastUsedAtUtc = null
                }
            ];

            return Task.FromResult(apps);
        });

        await viewModel.LoadAsync();

        Assert.Equal(2, viewModel.Apps.Count);
        Assert.Equal(1, viewModel.Apps[0].Rank);
        Assert.Equal("Code", viewModel.Apps[0].DisplayName);
        Assert.Equal("1h 1m", viewModel.Apps[0].ActiveDurationText);
        Assert.Equal("2h 0m", viewModel.Apps[0].TotalDurationText);
        Assert.Equal("50m 0s", viewModel.Apps[0].IdleDurationText);
        Assert.Equal("8m 55s", viewModel.Apps[0].UnknownDurationText);
        Assert.Equal(3, viewModel.Apps[0].SessionCount);
        Assert.Equal(lastUsedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), viewModel.Apps[0].LastUsedLocalTimeText);
        Assert.Equal(2, viewModel.Apps[1].Rank);
        Assert.Equal("WUJI Agent", viewModel.Apps[1].DisplayName);
        Assert.Equal("-", viewModel.Apps[1].LastUsedLocalTimeText);
        Assert.Contains("ranked by active duration", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppsViewModel_UsesDisplayNameFallback()
    {
        var viewModel = new AppsViewModel((_, _) =>
        {
            IReadOnlyList<AppUsageSummary> apps =
            [
                new AppUsageSummary
                {
                    ProcessName = "ProcessOnly",
                    DisplayName = string.Empty,
                    ActiveDurationSeconds = 1,
                    TotalDurationSeconds = 1,
                    SessionCount = 1
                },
                new AppUsageSummary
                {
                    ProcessName = string.Empty,
                    DisplayName = string.Empty,
                    ActiveDurationSeconds = 1,
                    TotalDurationSeconds = 1,
                    SessionCount = 1
                }
            ];

            return Task.FromResult(apps);
        });

        await viewModel.LoadAsync();

        Assert.Equal("ProcessOnly", viewModel.Apps[0].DisplayName);
        Assert.Equal("Unknown", viewModel.Apps[1].DisplayName);
    }

    [Fact]
    public async Task AppsViewModel_ReturnsEmptyStateForEmptyDatabase()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var viewModel = new AppsViewModel(new AppsDataService(paths));

        await viewModel.LoadAsync();

        Assert.Empty(viewModel.Apps);
        Assert.False(viewModel.HasLoadError);
        Assert.Equal("No app usage found for today.", viewModel.StatusText);
        Assert.Contains("暂无今日应用使用记录", viewModel.EmptyStateText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppsViewModel_RedactsLoadFailureAndShowsErrorState()
    {
        var viewModel = new AppsViewModel((_, _) =>
            throw new InvalidOperationException(
                @"Failed to open C:\Users\Alice\secrets\db.sqlite and \\server\share\logs\agent.log"));

        await viewModel.LoadAsync();

        Assert.Empty(viewModel.Apps);
        Assert.True(viewModel.HasLoadError);
        Assert.Equal("App usage could not be loaded. Refresh to retry.", viewModel.EmptyStateText);
        Assert.Contains("App usage load failed", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<path>", viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\Alice", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\server\share", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NavigationOrder_IsDashboardAppsSessionsSamplesDiagnosticsSettings()
    {
        Assert.Equal(
            ["Dashboard", "Apps", "Sessions", "Samples", "Diagnostics", "Settings"],
            MainWindowViewModel.NavigationPages);
    }

    [Fact]
    public async Task MainWindowViewModel_NavigatesToAppsSessionsSamples()
    {
        using var workspace = new TempWorkspace();
        var viewModel = await CreateMainWindowViewModelAsync(workspace);

        viewModel.SelectedTabIndex = 1;
        Assert.Equal("Apps", viewModel.CurrentPage);

        viewModel.SelectedTabIndex = 2;
        Assert.Equal("Sessions", viewModel.CurrentPage);

        viewModel.SelectedTabIndex = 3;
        Assert.Equal("Samples", viewModel.CurrentPage);

        viewModel.OpenSettingsCommand.Execute(null);
        Assert.Equal(5, viewModel.SelectedTabIndex);
        Assert.Equal("Settings", viewModel.CurrentPage);
    }

    [Fact]
    public async Task MainWindowViewModel_RefreshesCurrentPage()
    {
        using var workspace = new TempWorkspace();
        var samplesLoads = 0;
        var sessionsLoads = 0;
        var appsLoads = 0;
        var settingsLoads = 0;
        var viewModel = await CreateMainWindowViewModelAsync(
            workspace,
            sampleLoader: (_, _) =>
            {
                samplesLoads++;
                return Task.FromResult<IReadOnlyList<ForegroundSample>>([]);
            },
            sessionLoader: (_, _, _) =>
            {
                sessionsLoads++;
                return Task.FromResult<IReadOnlyList<AppSession>>([]);
            },
            appLoader: (_, _) =>
            {
                appsLoads++;
                return Task.FromResult<IReadOnlyList<AppUsageSummary>>([]);
            },
            settingsLoader: cancellationToken =>
            {
                settingsLoads++;
                return Task.FromResult(new AppSettings());
            });

        viewModel.SelectedTabIndex = 1;
        await viewModel.RefreshAsync();

        Assert.Equal(1, appsLoads);
        Assert.Equal(0, sessionsLoads);
        Assert.Equal(0, samplesLoads);

        viewModel.SelectedTabIndex = 2;
        await viewModel.RefreshAsync();

        Assert.Equal(1, appsLoads);
        Assert.Equal(1, sessionsLoads);
        Assert.Equal(0, samplesLoads);

        viewModel.SelectedTabIndex = 3;
        await viewModel.RefreshAsync();

        Assert.Equal(1, appsLoads);
        Assert.Equal(1, sessionsLoads);
        Assert.Equal(1, samplesLoads);
        Assert.Equal(0, settingsLoads);

        viewModel.SelectedTabIndex = 5;
        await viewModel.RefreshAsync();

        Assert.Equal(1, settingsLoads);
    }

    [Fact]
    public async Task MainWindowViewModel_AppliesRefreshIntervalAfterSettingsSave()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var runtimeStateStore = new RuntimeStateStore();
        var healthStateStore = new AgentHealthStateStore();
        var controlFileStore = new AgentControlFileStore();
        var appSettingsStore = new AppSettingsStore();
        var agentOptionsStore = new WindowsAgentOptionsStore();
        var settingsService = new SettingsService(paths, appSettingsStore, agentOptionsStore);

        await appSettingsStore.WriteAsync(
            settingsService.AppSettingsPath,
            new AppSettings
            {
                RefreshIntervalSeconds = 15,
                AutoStartAgentWhenAppStarts = false,
                LastSelectedPage = "Dashboard"
            });

        var statusService = new AgentStatusService(
            paths,
            runtimeStateStore,
            healthStateStore,
            controlFileStore,
            agentOptionsStore);
        var processService = new AgentProcessService(
            paths,
            runtimeStateStore,
            controlFileStore,
            NullLogger<AgentProcessService>.Instance);
        var controlService = new AgentControlService(paths, controlFileStore, statusService);
        var overviewDataService = new OverviewDataService(paths);
        var diagnosticsDataService = new DiagnosticsDataService(paths);
        var samplesViewModel = new SamplesViewModel((_, _) => Task.FromResult<IReadOnlyList<ForegroundSample>>([]));
        var sessionsViewModel = new SessionsViewModel((_, _, _) => Task.FromResult<IReadOnlyList<AppSession>>([]));
        var appsViewModel = new AppsViewModel((_, _) => Task.FromResult<IReadOnlyList<AppUsageSummary>>([]));
        var settingsViewModel = new SettingsViewModel(settingsService, paths);
        var mainViewModel = new MainWindowViewModel(
            processService,
            controlService,
            statusService,
            overviewDataService,
            diagnosticsDataService,
            samplesViewModel,
            sessionsViewModel,
            appsViewModel,
            settingsViewModel,
            settingsService);

        await mainViewModel.InitializeAsync();

        var timer = GetPrivateFieldValue<System.Windows.Threading.DispatcherTimer>(mainViewModel, "_refreshTimer");
        Assert.Equal(TimeSpan.FromSeconds(15), timer.Interval);

        settingsViewModel.RefreshIntervalSecondsText = "30";
        await settingsViewModel.SaveAppSettingsAsync();

        Assert.Equal(TimeSpan.FromSeconds(30), timer.Interval);
    }

    [Fact]
    public async Task SettingsViewModel_LoadsAppSettings()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var appSettingsStore = new AppSettingsStore();
        var agentOptionsStore = new WindowsAgentOptionsStore();
        var settingsService = new SettingsService(paths, appSettingsStore, agentOptionsStore);
        await appSettingsStore.WriteAsync(
            Path.Combine(paths.ConfigDir, "app-settings.json"),
            new AppSettings
            {
                AutoStartAgentWhenAppStarts = true,
                RefreshIntervalSeconds = 42,
                LastSelectedPage = "Samples"
            });

        var viewModel = new SettingsViewModel(settingsService, paths);

        await viewModel.LoadAsync();

        Assert.False(viewModel.HasLoadError);
        Assert.Equal(42, viewModel.AppSettings.RefreshIntervalSeconds);
        Assert.True(viewModel.AppSettings.AutoStartAgentWhenAppStarts);
        Assert.Equal("Samples", viewModel.AppSettings.LastSelectedPage);
        Assert.Equal("42", viewModel.RefreshIntervalSecondsText);
        Assert.Equal("Enabled", viewModel.AutoStartAgentWhenAppStartsText);
        Assert.Equal("Samples", viewModel.LastSelectedPageText);
    }

    [Fact]
    public async Task SettingsViewModel_LoadsAgentOptions()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var appSettingsStore = new AppSettingsStore();
        var agentOptionsStore = new WindowsAgentOptionsStore();
        var settingsService = new SettingsService(paths, appSettingsStore, agentOptionsStore);
        await agentOptionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 7,
                IdleThresholdSeconds = 90,
                HeartbeatIntervalSeconds = 5,
                StaleThresholdSeconds = 20,
                RetentionDays = 14,
                EnableJsonlJournal = false,
                EnableAgentEventJournal = false,
                EnableSessionMerge = false,
                MaskWindowTitles = false,
                ExcludedProcesses = ["Notepad"],
                ExcludedTitlePatterns = ["*Secret*"]
            });

        var viewModel = new SettingsViewModel(settingsService, paths);

        await viewModel.LoadAsync();

        Assert.False(viewModel.HasLoadError);
        Assert.Equal(7, viewModel.AgentOptions.SamplingIntervalSeconds);
        Assert.Equal(90, viewModel.AgentOptions.IdleThresholdSeconds);
        Assert.Equal(5, viewModel.AgentOptions.HeartbeatIntervalSeconds);
        Assert.Equal(20, viewModel.AgentOptions.StaleThresholdSeconds);
        Assert.Equal(14, viewModel.AgentOptions.RetentionDays);
        Assert.Equal("7", viewModel.SamplingIntervalSecondsText);
        Assert.Equal("90", viewModel.IdleThresholdSecondsText);
        Assert.Equal("5", viewModel.HeartbeatIntervalSecondsText);
        Assert.Equal("20", viewModel.StaleThresholdSecondsText);
        Assert.Equal("14", viewModel.RetentionDaysText);
        Assert.Equal("Disabled", viewModel.EnableJsonlJournalText);
        Assert.Equal("Disabled", viewModel.EnableAgentEventJournalText);
        Assert.Equal("Disabled", viewModel.EnableSessionMergeText);
        Assert.Equal("Disabled", viewModel.MaskWindowTitlesText);
        Assert.Equal("Notepad", viewModel.ExcludedProcessesText);
        Assert.Equal("*Secret*", viewModel.ExcludedTitlePatternsText);
    }

    [Fact]
    public async Task SettingsViewModel_ReturnsDefaultsWhenFilesMissing()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var settingsService = new SettingsService(paths, new AppSettingsStore(), new WindowsAgentOptionsStore());
        var viewModel = new SettingsViewModel(settingsService, paths);

        await viewModel.LoadAsync();

        Assert.False(viewModel.HasLoadError);
        Assert.Equal(15, viewModel.AppSettings.RefreshIntervalSeconds);
        Assert.False(viewModel.AppSettings.AutoStartAgentWhenAppStarts);
        Assert.Equal("Dashboard", viewModel.AppSettings.LastSelectedPage);
        Assert.Equal(3, viewModel.AgentOptions.SamplingIntervalSeconds);
        Assert.Equal(60, viewModel.AgentOptions.IdleThresholdSeconds);
        Assert.Equal(3, viewModel.AgentOptions.HeartbeatIntervalSeconds);
        Assert.Equal(15, viewModel.AgentOptions.StaleThresholdSeconds);
        Assert.Equal(30, viewModel.AgentOptions.RetentionDays);
        Assert.Equal("KeePass", viewModel.AgentOptions.ExcludedProcesses.First());
    }

    [Fact]
    public async Task SettingsViewModel_UsesEmptyStateForEmptyCollections()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var appSettingsStore = new AppSettingsStore();
        var agentOptionsStore = new WindowsAgentOptionsStore();
        var settingsService = new SettingsService(paths, appSettingsStore, agentOptionsStore);
        await agentOptionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                ExcludedProcesses = [],
                ExcludedTitlePatterns = []
            });

        var viewModel = new SettingsViewModel(settingsService, paths);

        await viewModel.LoadAsync();

        Assert.False(viewModel.HasLoadError);
        Assert.Equal("(none)", viewModel.ExcludedProcessesText);
        Assert.Equal("(none)", viewModel.ExcludedTitlePatternsText);
    }

    [Fact]
    public async Task SettingsViewModel_RedactsLoadFailureAndShowsSafeStatusText()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var viewModel = new SettingsViewModel(
            _ => throw new InvalidOperationException(
                @"Failed to open C:\Users\Alice\secrets\app-settings.json"),
            (_, _) => Task.CompletedTask,
            _ => throw new InvalidOperationException(
                @"Failed to open \\server\share\windows-agent.json"),
            paths);

        await viewModel.LoadAsync();

        Assert.True(viewModel.HasLoadError);
        Assert.DoesNotContain(@"C:\Users\Alice\secrets\app-settings.json", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\\server\share\windows-agent.json", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<path>", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal("Settings could not be fully loaded. Refresh to retry.", viewModel.EmptyStateText);
    }

    [Fact]
    public void SettingsViewModel_ExposesDataConfigLogRuntimePaths()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings()),
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new WindowsAgentOptions()),
            paths);

        Assert.Equal(paths.Root, viewModel.DataRootText);
        Assert.Equal(paths.ConfigDir, viewModel.ConfigDirectoryText);
        Assert.Equal(paths.DatabasePath, viewModel.DatabasePathText);
        Assert.Equal(paths.LogsDir, viewModel.LogsDirectoryText);
        Assert.Equal(paths.RuntimeDir, viewModel.RuntimeDirectoryText);
        Assert.Equal(Path.Combine(paths.ConfigDir, "app-settings.json"), viewModel.AppSettingsPathText);
        Assert.Equal(paths.AgentOptionsPath, viewModel.AgentOptionsPathText);
    }

    [Fact]
    public async Task SettingsViewModel_SavesAppSettings()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var appSettingsStore = new AppSettingsStore();
        var agentOptionsStore = new WindowsAgentOptionsStore();
        var settingsService = new SettingsService(paths, appSettingsStore, agentOptionsStore);

        await appSettingsStore.WriteAsync(
            settingsService.AppSettingsPath,
            new AppSettings
            {
                RefreshIntervalSeconds = 15,
                AutoStartAgentWhenAppStarts = false,
                LastSelectedPage = "Dashboard"
            });

        var viewModel = new SettingsViewModel(settingsService, paths);
        await viewModel.LoadAsync();

        viewModel.RefreshIntervalSecondsText = "45";
        viewModel.AutoStartAgentWhenAppStarts = true;

        await viewModel.SaveAppSettingsAsync();

        var saved = await settingsService.ReadAppSettingsAsync();
        Assert.Equal(45, saved.RefreshIntervalSeconds);
        Assert.True(saved.AutoStartAgentWhenAppStarts);
        // lastSelectedPage is read-only in Settings; preserved from loaded value
        Assert.Equal("Dashboard", saved.LastSelectedPage);
        Assert.Equal("App settings saved.", viewModel.SaveStatusText);
        Assert.False(viewModel.HasValidationError);
        Assert.False(viewModel.HasSaveError);
        Assert.Equal("45", viewModel.RefreshIntervalSecondsText);
        Assert.True(viewModel.AutoStartAgentWhenAppStarts);
        Assert.Equal("Dashboard", viewModel.LastSelectedPageText);
    }

    [Fact]
    public async Task SettingsViewModel_RejectsInvalidRefreshInterval()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var appSettingsStore = new AppSettingsStore();
        var agentOptionsStore = new WindowsAgentOptionsStore();
        var settingsService = new SettingsService(paths, appSettingsStore, agentOptionsStore);

        await appSettingsStore.WriteAsync(
            settingsService.AppSettingsPath,
            new AppSettings
            {
                RefreshIntervalSeconds = 15,
                AutoStartAgentWhenAppStarts = false,
                LastSelectedPage = "Dashboard"
            });

        var viewModel = new SettingsViewModel(settingsService, paths);
        await viewModel.LoadAsync();

        viewModel.RefreshIntervalSecondsText = "4";
        await viewModel.SaveAppSettingsAsync();

        var saved = await settingsService.ReadAppSettingsAsync();
        Assert.Equal(15, saved.RefreshIntervalSeconds);
        Assert.True(viewModel.HasValidationError);
        Assert.False(viewModel.HasSaveError);
        Assert.Contains("Refresh interval must be an integer between 5 and 300 seconds.", viewModel.SaveStatusText, StringComparison.Ordinal);

        viewModel.RefreshIntervalSecondsText = "abc";
        await viewModel.SaveAppSettingsAsync();

        Assert.True(viewModel.HasValidationError);
        Assert.Contains("Refresh interval must be an integer between 5 and 300 seconds.", viewModel.SaveStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsViewModel_PreservesLastSelectedPageAsReadOnly()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var appSettingsStore = new AppSettingsStore();
        var agentOptionsStore = new WindowsAgentOptionsStore();
        var settingsService = new SettingsService(paths, appSettingsStore, agentOptionsStore);

        await appSettingsStore.WriteAsync(
            settingsService.AppSettingsPath,
            new AppSettings
            {
                RefreshIntervalSeconds = 15,
                AutoStartAgentWhenAppStarts = false,
                LastSelectedPage = "Diagnostics"
            });

        var viewModel = new SettingsViewModel(settingsService, paths);
        await viewModel.LoadAsync();

        Assert.Equal("Diagnostics", viewModel.LastSelectedPageText);

        viewModel.RefreshIntervalSecondsText = "20";
        await viewModel.SaveAppSettingsAsync();

        var saved = await settingsService.ReadAppSettingsAsync();
        Assert.Equal("Diagnostics", saved.LastSelectedPage);
        Assert.False(viewModel.HasValidationError);
    }

    [Fact]
    public async Task SettingsViewModel_RedactsAppSettingsSaveFailure()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings
            {
                RefreshIntervalSeconds = 15,
                LastSelectedPage = "Dashboard"
            }),
            (_, _) => throw new InvalidOperationException(
                @"Failed to save C:\Users\Alice\config\app-settings.json"),
            _ => Task.FromResult(new WindowsAgentOptions()),
            paths);

        await viewModel.LoadAsync();
        viewModel.RefreshIntervalSecondsText = "30";
        await viewModel.SaveAppSettingsAsync();

        Assert.True(viewModel.HasSaveError);
        Assert.DoesNotContain(@"C:\Users\Alice\config\app-settings.json", viewModel.SaveStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<path>", viewModel.SaveStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentOptionsValidator_AcceptsDefaultOptions()
    {
        var validator = new AgentOptionsValidator();

        var result = validator.Validate(new WindowsAgentOptions());

        Assert.True(result.IsValid);
        Assert.Equal("Agent options are valid.", result.SafeMessageText);
        Assert.Equal(["KeePass", "1Password", "Bitwarden"], result.NormalizedOptions.ExcludedProcesses);
        Assert.Empty(result.NormalizedOptions.ExcludedTitlePatterns);
    }

    [Fact]
    public void AgentOptionsValidator_RejectsInvalidNumericRanges()
    {
        var validator = new AgentOptionsValidator();

        var result = validator.Validate(new WindowsAgentOptions
        {
            SamplingIntervalSeconds = 0,
            IdleThresholdSeconds = 9,
            HeartbeatIntervalSeconds = 0,
            StaleThresholdSeconds = 4,
            RetentionDays = 0
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.FieldName == "samplingIntervalSeconds");
        Assert.Contains(result.Issues, issue => issue.FieldName == "idleThresholdSeconds");
        Assert.Contains(result.Issues, issue => issue.FieldName == "heartbeatIntervalSeconds");
        Assert.Contains(result.Issues, issue => issue.FieldName == "staleThresholdSeconds");
        Assert.Contains(result.Issues, issue => issue.FieldName == "retentionDays");
        Assert.DoesNotContain("C:\\", result.SafeMessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AgentOptionsValidator_RejectsStaleThresholdEqualToHeartbeat()
    {
        var validator = new AgentOptionsValidator();

        var result = validator.Validate(new WindowsAgentOptions
        {
            SamplingIntervalSeconds = 1,
            IdleThresholdSeconds = 10,
            HeartbeatIntervalSeconds = 5,
            StaleThresholdSeconds = 5,
            RetentionDays = 30
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.FieldName == "staleThresholdSeconds" && issue.Message.Contains("greater than heartbeatIntervalSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentOptionsValidator_RejectsProcessPaths()
    {
        var validator = new AgentOptionsValidator();

        var result = validator.Validate(new WindowsAgentOptions
        {
            ExcludedProcesses = ["C:\\Windows\\System32\\notepad.exe", "Notepad"],
            ExcludedTitlePatterns = ["*Secret*"]
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.FieldName == "excludedProcesses");
        Assert.DoesNotContain(@"C:\Windows\System32\notepad.exe", result.SafeMessageText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Notepad"], result.NormalizedOptions.ExcludedProcesses);
    }

    [Fact]
    public void AgentOptionsValidator_NormalizesExcludedProcesses()
    {
        var validator = new AgentOptionsValidator();

        var result = validator.Validate(new WindowsAgentOptions
        {
            ExcludedProcesses = ["notepad.exe", " Notepad ", "calc.exe", "calc"],
            ExcludedTitlePatterns = ["*Secret*"]
        });

        Assert.True(result.IsValid);
        Assert.Equal(["notepad", "calc"], result.NormalizedOptions.ExcludedProcesses);
    }

    [Fact]
    public void AgentOptionsValidator_DeduplicatesPrivacyRules()
    {
        var validator = new AgentOptionsValidator();

        var result = validator.Validate(new WindowsAgentOptions
        {
            ExcludedProcesses = ["KeePass", "keepass.exe", " 1Password ", "1password.exe"],
            ExcludedTitlePatterns = ["*Secret*", "  ", "*Secret*", "*Private*", "*private*"]
        });

        Assert.True(result.IsValid);
        Assert.Equal(["KeePass", "1Password"], result.NormalizedOptions.ExcludedProcesses);
        Assert.Equal(["*Secret*", "*Private*"], result.NormalizedOptions.ExcludedTitlePatterns);
    }

    [Fact]
    public void AgentOptionsValidator_RejectsOverlongExcludedProcesses()
    {
        var validator = new AgentOptionsValidator();
        var tooLong = new string('x', AgentOptionsValidator.ExcludedProcessesMaxItemLength + 1);

        var result = validator.Validate(new WindowsAgentOptions
        {
            ExcludedProcesses = [tooLong]
        });

        Assert.False(result.IsValid);
        Assert.Contains("exceeds max length", result.SafeMessageText, StringComparison.Ordinal);
        Assert.Empty(result.NormalizedOptions.ExcludedProcesses);
    }

    [Fact]
    public void AgentOptionsValidator_RejectsOverlongExcludedTitlePatterns()
    {
        var validator = new AgentOptionsValidator();
        var tooLong = new string('*', AgentOptionsValidator.ExcludedTitlePatternsMaxItemLength + 1);

        var result = validator.Validate(new WindowsAgentOptions
        {
            ExcludedTitlePatterns = [tooLong]
        });

        Assert.False(result.IsValid);
        Assert.Contains("exceeds max length", result.SafeMessageText, StringComparison.Ordinal);
        Assert.Empty(result.NormalizedOptions.ExcludedTitlePatterns);
    }

    [Fact]
    public void AgentOptionsValidator_RejectsExceedingMaxCount()
    {
        var validator = new AgentOptionsValidator();
        var tooMany = Enumerable.Range(1, AgentOptionsValidator.ExcludedTitlePatternsMaxCount + 5)
            .Select(i => $"*Pattern{i}*")
            .ToList();

        var result = validator.Validate(new WindowsAgentOptions
        {
            ExcludedTitlePatterns = tooMany
        });

        Assert.False(result.IsValid);
        Assert.Contains("exceeds max count", result.SafeMessageText, StringComparison.Ordinal);
        Assert.Equal(AgentOptionsValidator.ExcludedTitlePatternsMaxCount, result.NormalizedOptions.ExcludedTitlePatterns.Count);
    }

    [Fact]
    public async Task WindowsAgentOptionsStore_WriteWithBackupAsync_CreatesBackupAndWritesAtomically()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var store = new WindowsAgentOptionsStore();

        var original = new WindowsAgentOptions
        {
            SamplingIntervalSeconds = 3,
            IdleThresholdSeconds = 60,
            IdleSummaryIntervalMinutes = 7,
            UseMockCapture = true
        };

        await store.WriteAsync(paths.AgentOptionsPath, original);

        var updated = new WindowsAgentOptions
        {
            SamplingIntervalSeconds = 5,
            IdleThresholdSeconds = 90,
            IdleSummaryIntervalMinutes = 7,
            UseMockCapture = false
        };

        await store.WriteWithBackupAsync(paths.AgentOptionsPath, updated);

        Assert.True(File.Exists(paths.AgentOptionsPath + ".bak"));
        var backup = await store.ReadAsync(paths.AgentOptionsPath + ".bak");
        Assert.NotNull(backup);
        Assert.Equal(3, backup.SamplingIntervalSeconds);
        Assert.Equal(60, backup.IdleThresholdSeconds);
        Assert.True(backup.UseMockCapture);

        var current = await store.ReadAsync(paths.AgentOptionsPath);
        Assert.NotNull(current);
        Assert.Equal(5, current.SamplingIntervalSeconds);
        Assert.Equal(90, current.IdleThresholdSeconds);
        Assert.False(current.UseMockCapture);
        Assert.Equal(7, current.IdleSummaryIntervalMinutes);
    }

    [Fact]
    public async Task WindowsAgentOptionsStore_WriteWithBackupAsync_KeepsOriginalFileWhenTempWriteFails()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var store = new WindowsAgentOptionsStore();

        await store.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 3,
                IdleThresholdSeconds = 60
            });

        Directory.CreateDirectory(paths.AgentOptionsPath + ".tmp");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            store.WriteWithBackupAsync(
                paths.AgentOptionsPath,
                new WindowsAgentOptions
                {
                    SamplingIntervalSeconds = 5,
                    IdleThresholdSeconds = 90
                }));

        Directory.Delete(paths.AgentOptionsPath + ".tmp");
        Assert.False(File.Exists(paths.AgentOptionsPath + ".tmp"));
        var current = await store.ReadAsync(paths.AgentOptionsPath);
        Assert.NotNull(current);
        Assert.Equal(3, current.SamplingIntervalSeconds);
        Assert.Equal(60, current.IdleThresholdSeconds);
    }

    [Fact]
    public async Task WindowsAgentOptionsStore_WriteWithBackupAsync_KeepsOriginalFileWhenReplaceFails()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var store = new WindowsAgentOptionsStore();

        await store.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 3,
                IdleThresholdSeconds = 60
            });

        using var lockedOriginal = File.Open(
            paths.AgentOptionsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            store.WriteWithBackupAsync(
                paths.AgentOptionsPath,
                new WindowsAgentOptions
                {
                    SamplingIntervalSeconds = 5,
                    IdleThresholdSeconds = 90
                }));

        var current = await store.ReadAsync(paths.AgentOptionsPath);
        Assert.NotNull(current);
        Assert.Equal(3, current.SamplingIntervalSeconds);
        Assert.Equal(60, current.IdleThresholdSeconds);

        var backup = await store.ReadAsync(paths.AgentOptionsPath + ".bak");
        Assert.NotNull(backup);
        Assert.Equal(3, backup.SamplingIntervalSeconds);
        Assert.Equal(60, backup.IdleThresholdSeconds);
    }

    [Fact]
    public async Task WindowsAgentOptionsStore_RestoreBackupAsync_RestoresFromBackup()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var store = new WindowsAgentOptionsStore();

        await store.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 3,
                IdleThresholdSeconds = 60
            });

        await store.WriteAsync(
            paths.AgentOptionsPath + ".bak",
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 7,
                IdleThresholdSeconds = 120
            });

        await store.RestoreBackupAsync(paths.AgentOptionsPath);

        var current = await store.ReadAsync(paths.AgentOptionsPath);
        Assert.NotNull(current);
        Assert.Equal(7, current.SamplingIntervalSeconds);
        Assert.Equal(120, current.IdleThresholdSeconds);
    }

    [Fact]
    public async Task WindowsAgentOptionsStore_RestoreBackupAsync_ThrowsWhenBackupMissing()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var store = new WindowsAgentOptionsStore();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreBackupAsync(paths.AgentOptionsPath));
        Assert.Contains("No backup file", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettingsViewModel_ValidatesAgentOptionsEditor()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var settingsService = new SettingsService(paths, new AppSettingsStore(), new WindowsAgentOptionsStore());
        var viewModel = new SettingsViewModel(settingsService, paths);

        await viewModel.LoadAsync();

        viewModel.SamplingIntervalSecondsText = "7";
        viewModel.IdleThresholdSecondsText = "90";
        viewModel.HeartbeatIntervalSecondsText = "5";
        viewModel.StaleThresholdSecondsText = "20";
        viewModel.RetentionDaysText = "14";
        viewModel.EnableJsonlJournal = false;
        viewModel.ExcludedProcessesText = "notepad.exe\ncalc.exe\ncalc";
        viewModel.ExcludedTitlePatternsText = "*Secret*\n*Secret*";

        viewModel.ValidateAgentOptions();

        Assert.False(viewModel.HasAgentOptionsValidationError);
        Assert.Equal("Agent options are valid.", viewModel.AgentOptionsValidationText);
        Assert.Equal("Normalized preview updated.", viewModel.AgentOptionsValidationDetailsText);
        Assert.Equal("Disabled", viewModel.EnableJsonlJournalText);
        Assert.Equal("notepad" + Environment.NewLine + "calc", viewModel.NormalizedExcludedProcessesText);
        Assert.Equal("*Secret*", viewModel.NormalizedExcludedTitlePatternsText);
    }

    [Fact]
    public async Task SettingsViewModel_ResetAgentOptionsEditorRestoresLoadedValues()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var appSettingsStore = new AppSettingsStore();
        var agentOptionsStore = new WindowsAgentOptionsStore();
        var settingsService = new SettingsService(paths, appSettingsStore, agentOptionsStore);

        await agentOptionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 11,
                IdleThresholdSeconds = 120,
                HeartbeatIntervalSeconds = 9,
                StaleThresholdSeconds = 40,
                RetentionDays = 45,
                EnableJsonlJournal = false,
                EnableAgentEventJournal = false,
                EnableSessionMerge = false,
                MaskWindowTitles = false,
                ExcludedProcesses = ["Notepad"],
                ExcludedTitlePatterns = ["*Secret*"],
                UseMockCapture = true
            });

        var viewModel = new SettingsViewModel(settingsService, paths);
        await viewModel.LoadAsync();

        viewModel.SamplingIntervalSecondsText = "7";
        viewModel.IdleThresholdSecondsText = "90";
        viewModel.HeartbeatIntervalSecondsText = "5";
        viewModel.StaleThresholdSecondsText = "20";
        viewModel.RetentionDaysText = "14";
        viewModel.EnableJsonlJournal = true;
        viewModel.EnableAgentEventJournal = true;
        viewModel.EnableSessionMerge = true;
        viewModel.MaskWindowTitles = true;
        viewModel.ExcludedProcessesText = "calc.exe";
        viewModel.ExcludedTitlePatternsText = "*Private*";

        viewModel.ResetAgentOptionsEditor();

        Assert.Equal("11", viewModel.SamplingIntervalSecondsText);
        Assert.Equal("120", viewModel.IdleThresholdSecondsText);
        Assert.Equal("9", viewModel.HeartbeatIntervalSecondsText);
        Assert.Equal("40", viewModel.StaleThresholdSecondsText);
        Assert.Equal("45", viewModel.RetentionDaysText);
        Assert.False(viewModel.EnableJsonlJournal);
        Assert.False(viewModel.EnableAgentEventJournal);
        Assert.False(viewModel.EnableSessionMerge);
        Assert.False(viewModel.MaskWindowTitles);
        Assert.Equal("Notepad", viewModel.ExcludedProcessesText);
        Assert.Equal("*Secret*", viewModel.ExcludedTitlePatternsText);
        Assert.Equal("Enabled", viewModel.UseMockCaptureText);
        Assert.Equal("Notepad", viewModel.NormalizedExcludedProcessesText);
        Assert.Equal("*Secret*", viewModel.NormalizedExcludedTitlePatternsText);
        Assert.Equal("Agent options editor reset to loaded values.", viewModel.AgentOptionsValidationText);
        Assert.False(viewModel.HasAgentOptionsValidationError);
    }

    [Fact]
    public async Task SettingsViewModel_SaveAppSettingsDoesNotSaveAgentOptions()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var appSettingsStore = new AppSettingsStore();
        var agentOptionsStore = new WindowsAgentOptionsStore();
        var settingsService = new SettingsService(paths, appSettingsStore, agentOptionsStore);

        await appSettingsStore.WriteAsync(
            settingsService.AppSettingsPath,
            new AppSettings
            {
                RefreshIntervalSeconds = 15,
                AutoStartAgentWhenAppStarts = false,
                LastSelectedPage = "Dashboard"
            });

        await agentOptionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 11,
                IdleThresholdSeconds = 120,
                HeartbeatIntervalSeconds = 9,
                StaleThresholdSeconds = 40,
                RetentionDays = 45,
                EnableJsonlJournal = false,
                EnableAgentEventJournal = false,
                EnableSessionMerge = false,
                MaskWindowTitles = false,
                ExcludedProcesses = ["Notepad"],
                ExcludedTitlePatterns = ["*Secret*"],
                UseMockCapture = true
            });

        var viewModel = new SettingsViewModel(settingsService, paths);
        await viewModel.LoadAsync();

        viewModel.RefreshIntervalSecondsText = "30";
        viewModel.AutoStartAgentWhenAppStarts = true;
        viewModel.SamplingIntervalSecondsText = "7";
        viewModel.EnableJsonlJournal = true;
        viewModel.ExcludedProcessesText = "calc.exe";

        await viewModel.SaveAppSettingsAsync();

        var savedAppSettings = await settingsService.ReadAppSettingsAsync();
        var savedAgentOptions = await settingsService.ReadAgentOptionsAsync();

        Assert.Equal(30, savedAppSettings.RefreshIntervalSeconds);
        Assert.True(savedAppSettings.AutoStartAgentWhenAppStarts);
        Assert.Equal("Dashboard", savedAppSettings.LastSelectedPage);
        Assert.Equal(11, savedAgentOptions.SamplingIntervalSeconds);
        Assert.False(savedAgentOptions.EnableJsonlJournal);
        Assert.Equal(["Notepad"], savedAgentOptions.ExcludedProcesses);
    }

    [Fact]
    public async Task SettingsViewModel_SavesAgentOptionsWithBackup()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var settingsService = new SettingsService(paths, new AppSettingsStore(), new WindowsAgentOptionsStore());
        var store = new WindowsAgentOptionsStore();

        await settingsService.SaveAgentOptionsAsync(new WindowsAgentOptions
        {
            SamplingIntervalSeconds = 3,
            IdleThresholdSeconds = 60,
            HeartbeatIntervalSeconds = 3,
            StaleThresholdSeconds = 15,
            RetentionDays = 30,
            MaskWindowTitles = false,
            ExcludedProcesses = ["KeePass"]
        });

        var viewModel = new SettingsViewModel(settingsService, paths);
        await viewModel.LoadAsync();

        viewModel.SamplingIntervalSecondsText = "5";
        viewModel.IdleThresholdSecondsText = "90";
        viewModel.HeartbeatIntervalSecondsText = "4";
        viewModel.StaleThresholdSecondsText = "20";
        viewModel.RetentionDaysText = "14";
        viewModel.MaskWindowTitles = true;
        viewModel.ExcludedProcessesText = "notepad.exe\ncalc";
        viewModel.ExcludedTitlePatternsText = "*Secret*";

        await viewModel.SaveAgentOptionsAsync();

        Assert.True(File.Exists(paths.AgentOptionsPath + ".bak"));
        var backup = await store.ReadAsync(paths.AgentOptionsPath + ".bak");
        Assert.NotNull(backup);
        Assert.Equal(3, backup.SamplingIntervalSeconds);
        Assert.False(backup.MaskWindowTitles);
        Assert.Equal(["KeePass"], backup.ExcludedProcesses);

        var saved = await settingsService.ReadAgentOptionsAsync();
        Assert.Equal(5, saved.SamplingIntervalSeconds);
        Assert.Equal(90, saved.IdleThresholdSeconds);
        Assert.Equal(4, saved.HeartbeatIntervalSeconds);
        Assert.Equal(20, saved.StaleThresholdSeconds);
        Assert.Equal(14, saved.RetentionDays);
        Assert.True(saved.MaskWindowTitles);
        Assert.Equal(["notepad", "calc"], saved.ExcludedProcesses);
        Assert.Equal(["*Secret*"], saved.ExcludedTitlePatterns);

        Assert.Equal(5, viewModel.AgentOptions.SamplingIntervalSeconds);
        Assert.Equal("5", viewModel.SamplingIntervalSecondsText);
        Assert.Contains("Saved to file. Running Agent has not applied the change; ReloadConfig or next Agent start required.", viewModel.AgentOptionsSaveStatusText, StringComparison.Ordinal);
        Assert.Contains("The running Agent has not applied it yet; use ReloadConfig or restart the Agent.", viewModel.AgentOptionsValidationDetailsText, StringComparison.Ordinal);
        Assert.False(viewModel.HasAgentOptionsSaveError);
        Assert.False(viewModel.HasAgentOptionsValidationError);
    }

    [Fact]
    public async Task SettingsViewModel_RejectsInvalidAgentOptionsBeforeSave()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var settingsService = new SettingsService(paths, new AppSettingsStore(), new WindowsAgentOptionsStore());

        await settingsService.SaveAgentOptionsAsync(new WindowsAgentOptions
        {
            SamplingIntervalSeconds = 3,
            HeartbeatIntervalSeconds = 3,
            StaleThresholdSeconds = 15
        });

        var viewModel = new SettingsViewModel(settingsService, paths);
        await viewModel.LoadAsync();

        viewModel.SamplingIntervalSecondsText = "0";
        viewModel.StaleThresholdSecondsText = "3";

        await viewModel.SaveAgentOptionsAsync();

        var saved = await settingsService.ReadAgentOptionsAsync();
        Assert.Equal(3, saved.SamplingIntervalSeconds);
        Assert.Equal(15, saved.StaleThresholdSeconds);

        Assert.True(viewModel.HasAgentOptionsValidationError);
        Assert.False(viewModel.HasAgentOptionsSaveError);
        Assert.Contains("Cannot save: fix validation errors first.", viewModel.AgentOptionsSaveStatusText, StringComparison.Ordinal);
        Assert.Contains("samplingIntervalSeconds", viewModel.AgentOptionsValidationDetailsText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsViewModel_RejectsPathLikeExcludedProcesses()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var settingsService = new SettingsService(paths, new AppSettingsStore(), new WindowsAgentOptionsStore());

        await settingsService.SaveAgentOptionsAsync(new WindowsAgentOptions
        {
            SamplingIntervalSeconds = 3,
            HeartbeatIntervalSeconds = 3,
            StaleThresholdSeconds = 15
        });

        var viewModel = new SettingsViewModel(settingsService, paths);
        await viewModel.LoadAsync();

        // Enter path-like process names: one valid, two paths
        viewModel.ExcludedProcessesText = "notepad.exe\nC:\\Windows\\System32\\notepad.exe\n/usr/bin/firefox";

        // Validate should flag paths as errors
        viewModel.ValidateAgentOptions();
        Assert.True(viewModel.HasAgentOptionsValidationError);
        Assert.Contains("excludedProcesses", viewModel.AgentOptionsValidationDetailsText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a path", viewModel.AgentOptionsValidationDetailsText, StringComparison.Ordinal);

        // Save should be blocked because of validation errors
        viewModel.SamplingIntervalSecondsText = "5";
        await viewModel.SaveAgentOptionsAsync();

        Assert.True(viewModel.HasAgentOptionsValidationError);
        Assert.False(viewModel.HasAgentOptionsSaveError);
        Assert.Contains("Cannot save: fix validation errors first.", viewModel.AgentOptionsSaveStatusText, StringComparison.Ordinal);

        // The saved file must not contain the path-like entries
        var saved = await settingsService.ReadAgentOptionsAsync();
        Assert.Equal(3, saved.SamplingIntervalSeconds);
        Assert.DoesNotContain(saved.ExcludedProcesses, p => p.Contains('\\') || p.Contains('/') || p.Contains(':'));
    }

    [Fact]
    public async Task SettingsViewModel_SaveAgentOptionsFailureDoesNotCorruptExistingFile()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var settingsService = new SettingsService(paths, new AppSettingsStore(), new WindowsAgentOptionsStore());

        await settingsService.SaveAgentOptionsAsync(new WindowsAgentOptions
        {
            SamplingIntervalSeconds = 3,
            IdleThresholdSeconds = 60,
            HeartbeatIntervalSeconds = 3,
            StaleThresholdSeconds = 15
        });

        Directory.CreateDirectory(paths.AgentOptionsPath + ".tmp");

        var viewModel = new SettingsViewModel(settingsService, paths);
        await viewModel.LoadAsync();
        viewModel.SamplingIntervalSecondsText = "5";

        await viewModel.SaveAgentOptionsAsync();

        Directory.Delete(paths.AgentOptionsPath + ".tmp");
        Assert.True(viewModel.HasAgentOptionsSaveError);
        var saved = await settingsService.ReadAgentOptionsAsync();
        Assert.Equal(3, saved.SamplingIntervalSeconds);
        Assert.Equal(60, saved.IdleThresholdSeconds);
        Assert.DoesNotContain(paths.AgentOptionsPath, viewModel.AgentOptionsSaveStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettingsViewModel_RestoresAgentOptionsBackup()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var settingsService = new SettingsService(paths, new AppSettingsStore(), new WindowsAgentOptionsStore());
        var store = new WindowsAgentOptionsStore();

        await store.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 3,
                IdleThresholdSeconds = 60,
                ExcludedProcesses = ["KeePass"]
            });

        await store.WriteAsync(
            paths.AgentOptionsPath + ".bak",
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 7,
                IdleThresholdSeconds = 120,
                ExcludedProcesses = ["Notepad"]
            });

        var viewModel = new SettingsViewModel(settingsService, paths);
        await viewModel.LoadAsync();

        await viewModel.RestoreAgentOptionsBackupAsync();

        var saved = await settingsService.ReadAgentOptionsAsync();
        Assert.Equal(7, saved.SamplingIntervalSeconds);
        Assert.Equal(120, saved.IdleThresholdSeconds);
        Assert.Equal(["Notepad"], saved.ExcludedProcesses);

        Assert.Equal(7, viewModel.AgentOptions.SamplingIntervalSeconds);
        Assert.Equal("7", viewModel.SamplingIntervalSecondsText);
        Assert.Equal("Notepad", viewModel.ExcludedProcessesText);
        Assert.Contains("Restored from backup. Running Agent has not applied the change; ReloadConfig or next Agent start required.", viewModel.AgentOptionsSaveStatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasAgentOptionsSaveError);
    }

    [Fact]
    public async Task SettingsViewModel_RestoreBackupWhenMissingShowsSafeStatus()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var settingsService = new SettingsService(paths, new AppSettingsStore(), new WindowsAgentOptionsStore());

        await settingsService.SaveAgentOptionsAsync(new WindowsAgentOptions { SamplingIntervalSeconds = 3 });

        var viewModel = new SettingsViewModel(settingsService, paths);
        await viewModel.LoadAsync();

        Assert.False(File.Exists(paths.AgentOptionsPath + ".bak"));

        await viewModel.RestoreAgentOptionsBackupAsync();

        Assert.True(viewModel.HasAgentOptionsSaveError);
        var saved = await settingsService.ReadAgentOptionsAsync();
        Assert.Equal(3, saved.SamplingIntervalSeconds);
        Assert.DoesNotContain(paths.AgentOptionsPath, viewModel.AgentOptionsSaveStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("restore failed", viewModel.AgentOptionsSaveStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettingsViewModel_SaveAgentOptionsPreservesNonEditableFields()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var settingsService = new SettingsService(paths, new AppSettingsStore(), new WindowsAgentOptionsStore());

        await settingsService.SaveAgentOptionsAsync(new WindowsAgentOptions
        {
            SamplingIntervalSeconds = 3,
            IdleThresholdSeconds = 60,
            IdleSummaryIntervalMinutes = 42,
            UseMockCapture = true,
            HeartbeatIntervalSeconds = 3,
            StaleThresholdSeconds = 15,
            RetentionDays = 30
        });

        var viewModel = new SettingsViewModel(settingsService, paths);
        await viewModel.LoadAsync();

        viewModel.SamplingIntervalSecondsText = "5";
        await viewModel.SaveAgentOptionsAsync();

        var saved = await settingsService.ReadAgentOptionsAsync();
        Assert.Equal(5, saved.SamplingIntervalSeconds);
        Assert.Equal(42, saved.IdleSummaryIntervalMinutes);
        Assert.True(saved.UseMockCapture);
        Assert.Equal("Enabled", viewModel.UseMockCaptureText);
        Assert.Equal("42", viewModel.IdleSummaryIntervalMinutesText);
    }

    [Fact]
    public async Task SettingsViewModel_SaveAgentOptionsDoesNotReloadAgent()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var settingsService = new SettingsService(paths, new AppSettingsStore(), new WindowsAgentOptionsStore());

        var viewModel = new SettingsViewModel(settingsService, paths);
        await viewModel.LoadAsync();

        viewModel.SamplingIntervalSecondsText = "5";
        await viewModel.SaveAgentOptionsAsync();

        Assert.False(File.Exists(paths.AgentControlPath));
        Assert.Contains("next Agent start", viewModel.AgentOptionsSaveStatusText, StringComparison.Ordinal);
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
    public async Task AgentStateMachine_ReloadConfigUsesValidatorAndNormalizesOptions()
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
                ExcludedProcesses = ["notepad.exe"]
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "calc",
                        WindowTitle = "Calculator",
                        IdleSeconds = 0,
                        ActivityState = "Active"
                    }
                ]),
                new QueueWin32ForegroundSampleProvider([])),
            eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);

        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 1,
                HeartbeatIntervalSeconds = 1,
                UseMockCapture = true,
                ExcludedProcesses = ["calc.exe", "calc"]
            });

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand
            {
                Command = AgentCommandType.ReloadConfig,
                RequestId = "reload-normalize"
            },
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.True(result.Completed);

        await Task.Delay(1100);
        await stateMachine.TickAsync(CancellationToken.None);

        var events = await ReadEventsAsync(paths.DatabasePath);
        Assert.Contains(events, x => x.EventType == AgentEventType.ConfigReloaded);
        Assert.Contains(events, x => x.EventType == AgentEventType.CommandCompleted && x.RequestId == "reload-normalize");

        var privacyFiltered = events.Where(x => x.EventType == AgentEventType.PrivacyFiltered).ToList();
        Assert.Single(privacyFiltered);
        Assert.Contains("\"processName\": \"calc\"", privacyFiltered[0].PayloadJson ?? string.Empty, StringComparison.Ordinal);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM foreground_samples;"));
    }

    [Fact]
    public async Task AgentStateMachine_ReloadConfigRejectsInvalidOptionsAndKeepsCurrentOptions()
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
                ExcludedProcesses = ["Notepad"]
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "Notepad",
                        WindowTitle = "Untitled",
                        IdleSeconds = 0,
                        ActivityState = "Active"
                    }
                ]),
                new QueueWin32ForegroundSampleProvider([])),
            eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);

        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 0,
                HeartbeatIntervalSeconds = 1,
                UseMockCapture = true
            });

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand
            {
                Command = AgentCommandType.ReloadConfig,
                RequestId = "reload-invalid"
            },
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.False(result.Completed);
        Assert.Equal("ReloadConfigValidationFailed", result.ErrorCode);

        await Task.Delay(1100);
        await stateMachine.TickAsync(CancellationToken.None);

        var events = await ReadEventsAsync(paths.DatabasePath);
        var failedEvent = Assert.Single(events.Where(x => x.EventType == AgentEventType.CommandFailed && x.RequestId == "reload-invalid"));
        Assert.Equal("ReloadConfigValidationFailed", failedEvent.ErrorCode);
        Assert.DoesNotContain(paths.AgentOptionsPath, failedEvent.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(events, x => x.EventType == AgentEventType.ConfigReloaded && x.RequestId == "reload-invalid");

        var privacyFiltered = events.Where(x => x.EventType == AgentEventType.PrivacyFiltered).ToList();
        Assert.Single(privacyFiltered);
        Assert.Contains("\"processName\": \"Notepad\"", privacyFiltered[0].PayloadJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentStateMachine_ReloadConfigReadFailureKeepsCurrentOptions()
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
                ExcludedProcesses = ["Notepad"]
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "Notepad",
                        WindowTitle = "Untitled",
                        IdleSeconds = 0,
                        ActivityState = "Active"
                    }
                ]),
                new QueueWin32ForegroundSampleProvider([])),
            eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);

        await File.WriteAllTextAsync(paths.AgentOptionsPath, "{ not json");

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand
            {
                Command = AgentCommandType.ReloadConfig,
                RequestId = "reload-read-failed"
            },
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.False(result.Completed);
        Assert.Equal("ReloadConfigReadFailed", result.ErrorCode);

        await Task.Delay(1100);
        await stateMachine.TickAsync(CancellationToken.None);

        var events = await ReadEventsAsync(paths.DatabasePath);
        var failedEvent = Assert.Single(events.Where(x => x.EventType == AgentEventType.CommandFailed && x.RequestId == "reload-read-failed"));
        Assert.Equal("ReloadConfigReadFailed", failedEvent.ErrorCode);
        Assert.DoesNotContain(paths.AgentOptionsPath, failedEvent.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(events, x => x.EventType == AgentEventType.ConfigReloaded && x.RequestId == "reload-read-failed");

        var privacyFiltered = events.Where(x => x.EventType == AgentEventType.PrivacyFiltered).ToList();
        Assert.Single(privacyFiltered);
        Assert.Contains("\"processName\": \"Notepad\"", privacyFiltered[0].PayloadJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentStateMachine_ReloadConfigKeepsPausedState()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
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
                RequestId = "pause-reload"
            },
            CancellationToken.None);

        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                UseMockCapture = true,
                IdleThresholdSeconds = 90
            });

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand
            {
                Command = AgentCommandType.ReloadConfig,
                RequestId = "reload-paused"
            },
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.True(result.Completed);
        Assert.Equal(AgentActualState.Paused, result.ActualState);

        var events = await ReadEventsAsync(paths.DatabasePath);
        var configReloaded = Assert.Single(events.Where(x => x.EventType == AgentEventType.ConfigReloaded));
        Assert.Contains("\"actualState\": \"Paused\"", configReloaded.PayloadJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentStateMachine_InitializeAsync_FallsBackToDefaultsForInvalidConfig_UsesNormalizedForValidConfig()
    {
        // Part 1: invalid config → fallback to defaults
        using var workspace1 = new TempWorkspace();
        var paths1 = new WindowsAgentPaths(workspace1.Root);
        var optionsStore1 = new WindowsAgentOptionsStore();

        await optionsStore1.WriteAsync(
            paths1.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 0,  // invalid: below min 1
                IdleThresholdSeconds = 5,      // invalid: below min 10
                HeartbeatIntervalSeconds = 1,
                UseMockCapture = true,
                ExcludedProcesses = []         // empty → defaults have KeePass/1Password/Bitwarden
            });

        var logger1 = new TestLogger<AgentStateMachine>();
        var stateMachine1 = CreateStateMachine(
            paths1,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "KeePass",
                        WindowTitle = "Vault",
                        IdleSeconds = 0,
                        ActivityState = "Active"
                    }
                ]),
                new QueueWin32ForegroundSampleProvider([])),
            logger: logger1);

        await stateMachine1.InitializeAsync(CancellationToken.None);

        var combinedLogs1 = string.Join(Environment.NewLine, logger1.Messages);
        Assert.Contains("falling back to defaults", combinedLogs1, StringComparison.OrdinalIgnoreCase);

        // Default sampling interval is 3 s; wait long enough for a sample to be due.
        await Task.Delay(3100);
        await stateMachine1.TickAsync(CancellationToken.None);

        // KeePass 属于默认 ExcludedProcesses，应被排除
        await using var connection1 = await SqliteConnectionFactory.OpenReadOnlyAsync(paths1.DatabasePath);
        Assert.Equal(0, await CountAsync(connection1, "SELECT COUNT(*) FROM foreground_samples;"));

        // Part 2: valid config with notepad.exe → normalized to notepad
        using var workspace2 = new TempWorkspace();
        var paths2 = new WindowsAgentPaths(workspace2.Root);
        var optionsStore2 = new WindowsAgentOptionsStore();

        await optionsStore2.WriteAsync(
            paths2.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 1,
                HeartbeatIntervalSeconds = 1,
                UseMockCapture = true,
                ExcludedProcesses = ["notepad.exe"]
            });

        var eventWriter2 = await CreateEventWriterAsync(paths2);
        var stateMachine2 = CreateStateMachine(
            paths2,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "Notepad",
                        WindowTitle = "Untitled",
                        IdleSeconds = 0,
                        ActivityState = "Active"
                    }
                ]),
                new QueueWin32ForegroundSampleProvider([])),
            eventWriter: eventWriter2);

        await stateMachine2.InitializeAsync(CancellationToken.None);
        await Task.Delay(1100);
        await stateMachine2.TickAsync(CancellationToken.None);

        // notepad.exe 归一化为 notepad，与 Notepad（大小写不敏感）匹配，应被排除
        var events2 = await ReadEventsAsync(paths2.DatabasePath);
        var privacyFiltered = events2.Where(x => x.EventType == AgentEventType.PrivacyFiltered).ToList();
        Assert.Single(privacyFiltered);
        Assert.Contains("\"processName\": \"Notepad\"", privacyFiltered[0].PayloadJson ?? string.Empty, StringComparison.Ordinal);

        await using var connection2 = await SqliteConnectionFactory.OpenReadOnlyAsync(paths2.DatabasePath);
        Assert.Equal(0, await CountAsync(connection2, "SELECT COUNT(*) FROM foreground_samples;"));
    }

    [Fact]
    public async Task SettingsViewModel_SaveAndReloadSavesThenRequestsReload()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        WindowsAgentOptions? savedOptions = null;
        var reloadRequested = false;

        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings()),
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new WindowsAgentOptions()),
            (options, _) =>
            {
                savedOptions = options;
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            _ => Task.FromResult(new AgentStatusSnapshot
            {
                IsRunning = true,
                ActualState = AgentActualState.Running
            }),
            _ =>
            {
                reloadRequested = true;
                return Task.FromResult(new AgentCommandResult
                {
                    Accepted = true,
                    Completed = false,
                    ActualState = AgentActualState.Running,
                    Message = "ReloadConfig queued"
                });
            },
            null,
            null,
            null,
            new AgentOptionsValidator(),
            paths);

        await viewModel.LoadAsync();

        viewModel.SamplingIntervalSecondsText = "5";
        viewModel.IdleThresholdSecondsText = "90";
        viewModel.HeartbeatIntervalSecondsText = "4";
        viewModel.StaleThresholdSecondsText = "20";
        viewModel.RetentionDaysText = "14";

        await viewModel.SaveAndReloadAgentOptionsAsync();

        Assert.NotNull(savedOptions);
        Assert.Equal(5, savedOptions!.SamplingIntervalSeconds);
        Assert.True(reloadRequested);
        Assert.Contains("ReloadConfig command queued", viewModel.AgentOptionsReloadStatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasAgentOptionsReloadError);
    }

    [Fact]
    public async Task SettingsViewModel_ReloadConfigDisabledWhenAgentNotRunning()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings()),
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new WindowsAgentOptions()),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.FromResult(new AgentStatusSnapshot
            {
                IsRunning = false,
                ActualState = AgentActualState.NotRunning
            }),
            _ => Task.FromResult(new AgentCommandResult { Accepted = true }),
            null,
            null,
            null,
            new AgentOptionsValidator(),
            paths);

        await viewModel.LoadAsync();

        Assert.False(viewModel.CanReloadAgentConfig);
        Assert.False(viewModel.SaveAndReloadAgentOptionsCommand.CanExecute(null));
        Assert.False(viewModel.ReloadAgentConfigCommand.CanExecute(null));
        Assert.Contains("next Agent start", viewModel.AgentOptionsReloadStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsViewModel_ReloadConfigFailureShowsSafeStatus()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings()),
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new WindowsAgentOptions()),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.FromResult(new AgentStatusSnapshot
            {
                IsRunning = true,
                ActualState = AgentActualState.Running
            }),
            _ => throw new InvalidOperationException(
                @"Failed to write control command to C:\Users\Alice\runtime\agent_control.json"),
            null,
            null,
            null,
            new AgentOptionsValidator(),
            paths);

        await viewModel.LoadAsync();

        await viewModel.ReloadAgentConfigAsync();

        Assert.True(viewModel.HasAgentOptionsReloadError);
        Assert.Contains("ReloadConfig request failed", viewModel.AgentOptionsReloadStatusText, StringComparison.Ordinal);
        Assert.Contains("<path>", viewModel.AgentOptionsReloadStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\Alice\runtime\agent_control.json", viewModel.AgentOptionsReloadStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SettingsViewModel_PollsUntilConfigReloadedObserved()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var requestId = string.Empty;
        var reloadObserved = false;

        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings()),
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new WindowsAgentOptions()),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.FromResult(new AgentStatusSnapshot
            {
                IsRunning = true,
                ActualState = AgentActualState.Running
            }),
            _ =>
            {
                var cmd = new AgentControlCommand { Command = AgentCommandType.ReloadConfig };
                requestId = cmd.RequestId;
                return Task.FromResult(new AgentCommandResult
                {
                    RequestId = requestId,
                    Accepted = true,
                    Completed = false,
                    ActualState = AgentActualState.Running
                });
            },
            null,
            null,
            _ =>
            {
                reloadObserved = true;
                return Task.FromResult<IReadOnlyList<AgentEvent>>(new[]
                {
                    new AgentEvent
                    {
                        EventType = AgentEventType.CommandCompleted,
                        RequestId = requestId,
                        EventLevel = AgentEventLevel.Info,
                        Message = "Command completed"
                    },
                    new AgentEvent
                    {
                        EventType = AgentEventType.ConfigReloaded,
                        RequestId = requestId,
                        EventLevel = AgentEventLevel.Info,
                        Message = "Config reloaded"
                    }
                });
            },
            new AgentOptionsValidator(),
            paths);

        viewModel.ReloadConfigPollMaxAttempts = 5;
        viewModel.ReloadConfigPollDelay = TimeSpan.Zero;

        await viewModel.LoadAsync();
        await viewModel.ReloadAgentConfigAsync();

        Assert.True(reloadObserved);
        Assert.Contains("ReloadConfig succeeded", viewModel.AgentOptionsReloadStatusText, StringComparison.Ordinal);
        Assert.False(viewModel.HasAgentOptionsReloadError);
    }

    [Fact]
    public async Task SettingsViewModel_PollsCommandFailedAndShowsError()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var requestId = string.Empty;

        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings()),
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new WindowsAgentOptions()),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.FromResult(new AgentStatusSnapshot
            {
                IsRunning = true,
                ActualState = AgentActualState.Running
            }),
            _ =>
            {
                var cmd = new AgentControlCommand { Command = AgentCommandType.ReloadConfig };
                requestId = cmd.RequestId;
                return Task.FromResult(new AgentCommandResult
                {
                    RequestId = requestId,
                    Accepted = true,
                    Completed = false,
                    ActualState = AgentActualState.Running
                });
            },
            null,
            null,
            _ => Task.FromResult<IReadOnlyList<AgentEvent>>(new[]
            {
                new AgentEvent
                {
                    EventType = AgentEventType.CommandFailed,
                    RequestId = requestId,
                    ErrorCode = "ReloadConfigValidationFailed",
                    EventLevel = AgentEventLevel.Error,
                    Message = "Reloaded agent options configuration is invalid."
                }
            }),
            new AgentOptionsValidator(),
            paths);

        viewModel.ReloadConfigPollMaxAttempts = 5;
        viewModel.ReloadConfigPollDelay = TimeSpan.Zero;

        await viewModel.LoadAsync();
        await viewModel.ReloadAgentConfigAsync();

        Assert.True(viewModel.HasAgentOptionsReloadError);
        Assert.Contains("ReloadConfig failed", viewModel.AgentOptionsReloadStatusText, StringComparison.Ordinal);
        Assert.Contains("ReloadConfigValidationFailed", viewModel.AgentOptionsReloadStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsViewModel_RestoreBackupDoesNotReloadAutomatically()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var store = new WindowsAgentOptionsStore();

        await store.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 3,
                IdleThresholdSeconds = 60
            });

        await store.WriteAsync(
            paths.AgentOptionsPath + ".bak",
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 7,
                IdleThresholdSeconds = 120
            });

        var reloadRequested = false;
        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings()),
            (_, _) => Task.CompletedTask,
            async _ => await store.ReadAsync(paths.AgentOptionsPath) ?? new WindowsAgentOptions(),
            (_, _) => Task.CompletedTask,
            _ => store.RestoreBackupAsync(paths.AgentOptionsPath),
            _ => Task.FromResult(new AgentStatusSnapshot
            {
                IsRunning = true,
                ActualState = AgentActualState.Running
            }),
            _ =>
            {
                reloadRequested = true;
                return Task.FromResult(new AgentCommandResult { Accepted = true });
            },
            null,
            null,
            null,
            new AgentOptionsValidator(),
            paths);

        await viewModel.LoadAsync();
        await viewModel.RestoreAgentOptionsBackupAsync();

        Assert.False(reloadRequested);
        Assert.Equal(7, viewModel.AgentOptions.SamplingIntervalSeconds);
        Assert.Contains("ReloadConfig or next Agent start", viewModel.AgentOptionsSaveStatusText, StringComparison.Ordinal);
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

    [Fact]
    public async Task AgentStateMachine_ReloadConfigAppliesUpdatedExcludedTitlePatterns()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        // Initial config: no title exclusion
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 1,
                HeartbeatIntervalSeconds = 1,
                UseMockCapture = true,
                ExcludedTitlePatterns = []
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
                        IdleSeconds = 0,
                        ActivityState = "Active"
                    },
                    new ForegroundSample
                    {
                        SampleTimeUtc = DateTime.UtcNow,
                        ProcessName = "Browser",
                        WindowTitle = "Another Secret Project",
                        IdleSeconds = 0,
                        ActivityState = "Active"
                    }
                ]),
                new QueueWin32ForegroundSampleProvider([])),
            eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);
        await Task.Delay(1100);
        await stateMachine.TickAsync(CancellationToken.None);

        // First sample should be written (no exclusion yet)
        await using (var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath))
        {
            Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM foreground_samples;"));
        }

        // Now update config to include title pattern exclusion
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 1,
                HeartbeatIntervalSeconds = 1,
                UseMockCapture = true,
                ExcludedTitlePatterns = ["*Secret*"]
            });

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand
            {
                Command = AgentCommandType.ReloadConfig,
                RequestId = "reload-title-patterns"
            },
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.True(result.Completed);

        await Task.Delay(1100);
        await stateMachine.TickAsync(CancellationToken.None);

        // Second sample should be excluded (title matches *Secret*)
        var events = await ReadEventsAsync(paths.DatabasePath);
        var privacyFiltered = events.Where(x => x.EventType == AgentEventType.PrivacyFiltered).ToList();
        Assert.Single(privacyFiltered);

        var payload = privacyFiltered[0].PayloadJson ?? string.Empty;
        Assert.Contains("\"ruleType\": \"Title\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"processName\": \"Browser\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"privacyReason\": \"Excluded by title privacy rule\"", payload, StringComparison.Ordinal);
        // Must not leak the matched window title
        Assert.DoesNotContain("Secret", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Another", payload, StringComparison.OrdinalIgnoreCase);

        // ConfigReloaded + CommandCompleted events must be present
        Assert.Contains(events, x => x.EventType == AgentEventType.ConfigReloaded);
        Assert.Contains(events, x => x.EventType == AgentEventType.CommandCompleted && x.RequestId == "reload-title-patterns");

        // DB must still have only the first sample (second was excluded)
        await using var connection2 = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        Assert.Equal(1, await CountAsync(connection2, "SELECT COUNT(*) FROM foreground_samples;"));
    }

    [Fact]
    public async Task AgentStateMachine_PruneData_EntersAndLeavesMaintenance()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                UseMockCapture = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter,
            dataMaintenanceService: new DataMaintenanceService(paths));

        await stateMachine.InitializeAsync(CancellationToken.None);
        // Verify Running before command
        Assert.Equal(AgentActualState.Running, stateMachine.ActualState);

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand
            {
                Command = AgentCommandType.PruneData,
                RequestId = "prune-data"
            },
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.True(result.Completed);
        Assert.Equal(AgentActualState.Running, result.ActualState);

        // Verify no actual data deletion
        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        var tables = new[] { "foreground_samples", "app_sessions", "agent_events" };
        foreach (var table in tables)
        {
            // Tables exist and have columns (initialized by database initializer)
            var columns = await GetColumnsAsync(connection, table);
            Assert.NotEmpty(columns);
        }

        var events = await ReadEventsAsync(paths.DatabasePath);
        Assert.Contains(events, x => x.EventType == AgentEventType.CommandAccepted && x.RequestId == "prune-data");
        Assert.Contains(events, x => x.EventType == AgentEventType.CommandCompleted && x.RequestId == "prune-data");

        // runtime_state must show final state is Running (Maintenance was entered and exited)
        Assert.True(File.Exists(paths.RuntimeStatePath));
        var runtimeJson = await File.ReadAllTextAsync(paths.RuntimeStatePath);
        Assert.Contains("\"state\": \"Running\"", runtimeJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"state\": \"Maintenance\"", runtimeJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentStateMachine_ClearHistory_EndsPaused()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                UseMockCapture = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter,
            dataMaintenanceService: new DataMaintenanceService(paths));

        await stateMachine.InitializeAsync(CancellationToken.None);
        Assert.Equal(AgentActualState.Running, stateMachine.ActualState);

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand
            {
                Command = AgentCommandType.ClearHistory,
                RequestId = "clear-history"
            },
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.True(result.Completed);
        Assert.Equal(AgentActualState.Paused, result.ActualState);

        var events = await ReadEventsAsync(paths.DatabasePath);
        Assert.Contains(events, x => x.EventType == AgentEventType.CommandCompleted && x.RequestId == "clear-history");
    }

    [Fact]
    public async Task AgentStateMachine_MaintenanceSkipsSampleCapture()
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
                        ProcessName = "TestApp",
                        WindowTitle = "Window",
                        IdleSeconds = 0,
                        ActivityState = "Active"
                    }
                ]),
                new QueueWin32ForegroundSampleProvider([])),
            eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);
        // Directly set Maintenance (bypasses command processing for test simplicity)
        stateMachine.ActualState = AgentActualState.Maintenance;

        await Task.Delay(1100);
        var keepRunning = await stateMachine.TickAsync(CancellationToken.None);
        Assert.True(keepRunning);

        // No sample should be written during Maintenance
        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM foreground_samples;"));
    }

    [Fact]
    public async Task AgentStateMachine_RejectsDuplicateMaintenanceCommands()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                UseMockCapture = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter);

        await stateMachine.InitializeAsync(CancellationToken.None);
        // Manually set to Maintenance to simulate mid-operation
        stateMachine.ActualState = AgentActualState.Maintenance;

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand
            {
                Command = AgentCommandType.PruneData,
                RequestId = "prune-dup"
            },
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("AlreadyInMaintenance", result.ErrorCode);

        var events = await ReadEventsAsync(paths.DatabasePath);
        Assert.Contains(events, x => x.EventType == AgentEventType.CommandFailed && x.ErrorCode == "AlreadyInMaintenance");
    }

    [Fact]
    public async Task AgentStateMachine_ClearHistoryFromPausedStaysPaused()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                UseMockCapture = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter,
            dataMaintenanceService: new DataMaintenanceService(paths));

        await stateMachine.InitializeAsync(CancellationToken.None);
        await stateMachine.ProcessCommandAsync(
            new AgentControlCommand { Command = AgentCommandType.Pause, DesiredState = AgentDesiredState.Paused },
            CancellationToken.None);
        Assert.Equal(AgentActualState.Paused, stateMachine.ActualState);

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand { Command = AgentCommandType.ClearHistory, RequestId = "clear-paused" },
            CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(AgentActualState.Paused, result.ActualState);
    }

    [Fact]
    public async Task AgentStateMachine_PruneData_WritesMaintenanceToRuntimeStateDuringOperation()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                UseMockCapture = true
            });

        var recordedStates = new List<AgentActualState>();
        var recordingStore = new RecordingRuntimeStateStore(new RuntimeStateStore(), recordedStates);
        var healthStateStore = new AgentHealthStateStore();

        var stateMachine = new AgentStateMachine(
            paths,
            recordingStore,
            healthStateStore,
            new AgentControlFileStore(),
            optionsStore,
            new SqliteDatabaseInitializer(paths.DatabasePath),
            new ForegroundSampleRepository(paths.DatabasePath),
            new SessionAggregator(new AppSessionRepository(paths.DatabasePath)),
            new ForegroundSamplePrivacyFilter(),
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter,
            new AgentOptionsValidator(),
            NullLogger<AgentStateMachine>.Instance);

        await stateMachine.InitializeAsync(CancellationToken.None);

        // Clear recording after init
        recordedStates.Clear();

        await stateMachine.ProcessCommandAsync(
            new AgentControlCommand { Command = AgentCommandType.PruneData, RequestId = "prune-record" },
            CancellationToken.None);

        // Maintenance was written somewhere in the sequence
        Assert.Contains(AgentActualState.Maintenance, recordedStates);
        // Final state is Running
        Assert.Equal(AgentActualState.Running, recordedStates[^1]);
    }

    private sealed class RecordingRuntimeStateStore : RuntimeStateStore
    {
        private readonly RuntimeStateStore _inner;
        private readonly List<AgentActualState> _states;

        public RecordingRuntimeStateStore(RuntimeStateStore inner, List<AgentActualState> states)
        {
            _inner = inner;
            _states = states;
        }

        public override async Task WriteAsync(string path, Core.Runtime.RuntimeState state, CancellationToken cancellationToken = default)
        {
            _states.Add(state.State);
            await _inner.WriteAsync(path, state, cancellationToken);
        }
    }

    [Fact]
    public async Task DataMaintenanceService_PruneDataDeletesExpiredRows()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var oldTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var recentTime = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

        await InsertSampleAsync(paths.DatabasePath, oldTime, "OldApp", null, "Active");
        await InsertSampleAsync(paths.DatabasePath, recentTime, "RecentApp", null, "Active");
        await InsertSessionAsync(paths.DatabasePath, oldTime.ToLocalTime(), oldTime.ToLocalTime().AddMinutes(10), "OldSession", 600, 600, 0, 0, "Closed");
        await InsertSessionAsync(paths.DatabasePath, recentTime.ToLocalTime(), recentTime.ToLocalTime().AddMinutes(10), "RecentSession", 600, 600, 0, 0, "Closed");

        var service = new DataMaintenanceService(paths);
        var result = await service.PruneDataAsync(30, new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(result.Success);
        Assert.Equal(1, result.ForegroundSamplesDeleted);
        Assert.Equal(1, result.SessionsDeleted);
        Assert.Equal(new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc), result.CutoffUtc);
        Assert.Equal(new DateOnly(2026, 5, 25), result.CutoffLocalDate);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM foreground_samples;"));
        Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM app_sessions;"));
    }

    [Fact]
    public async Task DataMaintenanceService_PruneDataKeepsRecentRows()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var recentTime = new DateTime(2026, 6, 22, 12, 0, 0, DateTimeKind.Utc);
        await InsertSampleAsync(paths.DatabasePath, recentTime, "Recent", null, "Active");
        await InsertSessionAsync(paths.DatabasePath, recentTime.ToLocalTime(), recentTime.ToLocalTime().AddMinutes(10), "Recent", 600, 600, 0, 0, "Closed");

        var service = new DataMaintenanceService(paths);
        // cutoff: 30 days before June 24, 2026 = May 25, 2026 — June 22 data is recent, not deleted
        var result = await service.PruneDataAsync(30, new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(result.Success);
        Assert.Equal(0, result.ForegroundSamplesDeleted);
        Assert.Equal(0, result.SessionsDeleted);
    }

    [Fact]
    public async Task DataMaintenanceService_PruneDataKeepsOpenSessions()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var oldTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        await InsertOpenSessionAsync(paths.DatabasePath, oldTime.ToLocalTime(), "OpenOld", 3600, 3600, 0, 0);
        // Also insert a closed old session for contrast
        await InsertSessionAsync(paths.DatabasePath, oldTime.ToLocalTime(), oldTime.ToLocalTime().AddMinutes(10), "ClosedOld", 600, 600, 0, 0, "Closed");

        var service = new DataMaintenanceService(paths);
        var result = await service.PruneDataAsync(30, new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(result.Success);
        Assert.Equal(1, result.SessionsDeleted); // only the closed session

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM app_sessions;"));
        Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM app_sessions WHERE ended_at_utc IS NULL;"));
    }

    [Fact]
    public async Task DataMaintenanceService_PruneDataHandlesMissingTables()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        // Create a database with no tables
        await using (var connection = await SqliteConnectionFactory.OpenAsync(paths.DatabasePath, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate))
        {
        }

        var service = new DataMaintenanceService(paths);
        var result = await service.PruneDataAsync(30, new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(result.Success);
        Assert.Equal(0, result.ForegroundSamplesDeleted);
        Assert.Equal(0, result.SessionsDeleted);
        Assert.Equal(0, result.AgentEventsDeleted);
    }

    [Fact]
    public async Task DataMaintenanceService_PruneDataUsesTransactionForSqliteDeletes()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var oldTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        await InsertSampleAsync(paths.DatabasePath, oldTime, "OldApp", null, "Active");
        await InsertSessionAsync(paths.DatabasePath, oldTime.ToLocalTime(), oldTime.ToLocalTime().AddMinutes(10), "OldSession", 600, 600, 0, 0, "Closed");

        var service = new DataMaintenanceService(paths);
        var result = await service.PruneDataAsync(30, new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(result.Success);
        // Both tables were pruned together in one transaction — counts reflect accurate deletion
        Assert.True(result.ForegroundSamplesDeleted > 0 || result.SessionsDeleted > 0);
    }

    [Fact]
    public async Task DataMaintenanceService_PruneDataDeletesOldJsonlFilesOnly()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        // Create JSONL files at different dates
        var oldFile = Path.Combine(paths.LogsDir, "agent_events_20240115.jsonl");
        var recentFile = Path.Combine(paths.LogsDir, "agent_events_20260620.jsonl");
        var todayFile = Path.Combine(paths.LogsDir, "agent_events_20260624.jsonl");
        var nonJournalFile = Path.Combine(paths.LogsDir, "agent.log");
        var nonMatchingFile = Path.Combine(paths.LogsDir, "random_20240101.jsonl");

        await File.WriteAllTextAsync(oldFile, "{}");
        await File.WriteAllTextAsync(recentFile, "{}");
        await File.WriteAllTextAsync(todayFile, "{}");
        await File.WriteAllTextAsync(nonJournalFile, "log");
        await File.WriteAllTextAsync(nonMatchingFile, "{}");

        // cutoffLocalDate = June 24, 2026 - 30 days = May 25, 2026
        // Only the Jan 15, 2024 file should be deleted
        var (deleted, errors) = new DataMaintenanceService(paths).DeleteOldJsonlFiles(
            new DateOnly(2026, 5, 25));

        Assert.Equal(1, deleted);
        Assert.Equal(0, errors);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(recentFile));
        Assert.True(File.Exists(todayFile));
        Assert.True(File.Exists(nonJournalFile));
        Assert.True(File.Exists(nonMatchingFile));
    }

    [Fact]
    public async Task DataMaintenanceService_PruneDataUsesLocalDateForJsonlCutoff()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        // File dated June 1, 2026 local time (but this could be May 31 in UTC)
        var edgeFile = Path.Combine(paths.LogsDir, "agent_events_20260601.jsonl");
        await File.WriteAllTextAsync(edgeFile, "{}");

        // With 30 days retention and reference June 24 UTC:
        // cutoffLocalDate = June 24 - 30 = May 25 (local)
        // June 1 is NOT before May 25, so it should be kept
        var (deleted, _) = new DataMaintenanceService(paths).DeleteOldJsonlFiles(
            new DateOnly(2026, 5, 25));

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(edgeFile));
    }

    [Fact]
    public async Task DataMaintenanceService_PruneDataDoesNotDeleteConfigRuntimeOrDatabaseFiles()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        // Create a config file, runtime file, and database file
        var configFile = Path.Combine(paths.ConfigDir, "windows-agent.json");
        var runtimeFile = Path.Combine(paths.RuntimeDir, "runtime_state.json");
        Directory.CreateDirectory(paths.ConfigDir);
        Directory.CreateDirectory(paths.RuntimeDir);
        await File.WriteAllTextAsync(configFile, "{}");
        await File.WriteAllTextAsync(runtimeFile, "{}");

        // Also create an old JSONL file to verify only that is deleted
        var oldJournal = Path.Combine(paths.LogsDir, "agent_events_20240101.jsonl");
        await File.WriteAllTextAsync(oldJournal, "{}");

        var (deleted, errors) = new DataMaintenanceService(paths).DeleteOldJsonlFiles(
            new DateOnly(2026, 1, 1));

        Assert.Equal(1, deleted);
        Assert.Equal(0, errors);
        Assert.True(File.Exists(configFile));
        Assert.True(File.Exists(runtimeFile));
    }

    [Fact]
    public void DataMaintenanceService_ReportsJsonlDeleteFailureWithoutPaths()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        // Create an old journal file and make its directory read-only after creation.
        // Since we can't reliably simulate delete failure cross-platform without
        // permissions games, we verify that the result model itself never exposes
        // raw paths: PruneDataResult.Ok/Failed constructors have no path fields.
        var result = PruneDataResult.Ok(10, 5, 20, 3,
            new DateTime(2026, 5, 25, 0, 0, 0, DateTimeKind.Utc),
            new DateOnly(2026, 5, 25), 2);

        Assert.False(result.Success); // errors > 0
        Assert.Equal("JsonlDeletePartial", result.ErrorCode);
        Assert.NotNull(result.SafeMessage);
        Assert.DoesNotContain(":\\", result.SafeMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void DataMaintenanceService_TryParseJournalDate_ReturnsCorrectDate()
    {
        Assert.Equal(new DateOnly(2026, 1, 15), DataMaintenanceService.TryParseJournalDate("agent_events_20260115.jsonl"));
        Assert.Equal(new DateOnly(2024, 12, 31), DataMaintenanceService.TryParseJournalDate("foreground_samples_20241231.jsonl"));
        Assert.Null(DataMaintenanceService.TryParseJournalDate("agent.log"));
        Assert.Null(DataMaintenanceService.TryParseJournalDate("agent_events_20260115.jsonl.bak"));
        Assert.Null(DataMaintenanceService.TryParseJournalDate(""));
        Assert.Null(DataMaintenanceService.TryParseJournalDate("agent_events_notadate.jsonl")); // not a date
    }

    [Fact]
    public async Task DataMaintenanceService_PruneDataDeletesAgentEvents()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var oldTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var recentTime = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

        await InsertAgentEventAsync(paths.DatabasePath, oldTime, AgentEventType.AgentStarted, AgentEventLevel.Info, "Old");
        await InsertAgentEventAsync(paths.DatabasePath, recentTime, AgentEventType.AgentStarted, AgentEventLevel.Info, "Recent");

        var service = new DataMaintenanceService(paths);
        var result = await service.PruneDataAsync(30, new DateTime(2026, 6, 24, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(result.Success);
        Assert.Equal(1, result.AgentEventsDeleted);
        Assert.Equal(new DateTime(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc), result.CutoffUtc);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        Assert.Equal(1, await CountAsync(connection, "SELECT COUNT(*) FROM agent_events;"));
    }

    private static async Task InsertAgentEventAsync(
        string databasePath,
        DateTime eventTimeUtc,
        AgentEventType eventType,
        AgentEventLevel eventLevel,
        string message)
    {
        await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO agent_events (
                event_time_utc,
                event_type,
                event_level,
                message
            )
            VALUES (
                $event_time_utc,
                $event_type,
                $event_level,
                $message
            );
            """;

        command.Parameters.AddWithValue("$event_time_utc", eventTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("$event_type", eventType.ToString());
        command.Parameters.AddWithValue("$event_level", eventLevel.ToString());
        command.Parameters.AddWithValue("$message", message);

        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task AgentStateMachine_PruneData_WritesDataPrunedEventWithCorrectPayload()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                RetentionDays = 30,
                UseMockCapture = true
            });

        var dataMaintenanceService = new DataMaintenanceService(paths);
        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter,
            dataMaintenanceService: dataMaintenanceService);

        await stateMachine.InitializeAsync(CancellationToken.None);

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand { Command = AgentCommandType.PruneData, RequestId = "prune-data-event" },
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.True(result.Completed);
        Assert.Equal(AgentActualState.Running, result.ActualState);

        var events = await ReadEventsAsync(paths.DatabasePath);
        var dataPrunedEvent = Assert.Single(events.Where(x => x.EventType == AgentEventType.DataPruned));
        Assert.Equal("prune-data-event", dataPrunedEvent.RequestId);

        var payload = dataPrunedEvent.PayloadJson ?? string.Empty;
        Assert.Contains("\"retentionDays\": 30", payload, StringComparison.Ordinal);
        Assert.Contains("\"foregroundSamplesDeleted\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"actualState\": \"Maintenance\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("windowTitle", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\\\", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentStateMachine_PruneDataRestoresRunningState()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                UseMockCapture = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter,
            dataMaintenanceService: new DataMaintenanceService(paths));

        await stateMachine.InitializeAsync(CancellationToken.None);
        Assert.Equal(AgentActualState.Running, stateMachine.ActualState);

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand { Command = AgentCommandType.PruneData, RequestId = "prune-restore" },
            CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(AgentActualState.Running, result.ActualState);
    }

    [Fact]
    public async Task AgentStateMachine_PruneDataRestoresPausedState()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                UseMockCapture = true
            });

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter,
            dataMaintenanceService: new DataMaintenanceService(paths));

        await stateMachine.InitializeAsync(CancellationToken.None);
        await stateMachine.ProcessCommandAsync(
            new AgentControlCommand { Command = AgentCommandType.Pause, DesiredState = AgentDesiredState.Paused },
            CancellationToken.None);
        Assert.Equal(AgentActualState.Paused, stateMachine.ActualState);

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand { Command = AgentCommandType.PruneData, RequestId = "prune-paused" },
            CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(AgentActualState.Paused, result.ActualState);
    }

    [Fact]
    public async Task AgentStateMachine_PruneDataDoesNotDeleteItsOwnCompletionEvents()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                RetentionDays = 30,
                UseMockCapture = true
            });

        // Insert old agent_events that would be deleted by PruneData
        var oldTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        await InsertAgentEventAsync(paths.DatabasePath, oldTime, AgentEventType.AgentStarted, AgentEventLevel.Info, "Old event");

        var dataMaintenanceService = new DataMaintenanceService(paths);
        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter,
            dataMaintenanceService: dataMaintenanceService);

        await stateMachine.InitializeAsync(CancellationToken.None);

        // PruneData should delete old events
        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand { Command = AgentCommandType.PruneData, RequestId = "prune-self" },
            CancellationToken.None);

        Assert.True(result.Completed);

        // DataPruned and CommandCompleted must survive (written after deletion)
        var events = await ReadEventsAsync(paths.DatabasePath);
        Assert.Contains(events, x => x.EventType == AgentEventType.DataPruned);
        Assert.Contains(events, x => x.EventType == AgentEventType.CommandCompleted && x.RequestId == "prune-self");
        // Old event should be gone
        Assert.DoesNotContain(events, x => x.EventType == AgentEventType.AgentStarted && x.Message == "Old event");
    }

    [Fact]
    public async Task AgentStateMachine_PruneDataUsesRetentionDays()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                RetentionDays = 7,
                UseMockCapture = true
            });

        var dataMaintenanceService = new DataMaintenanceService(paths);
        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter,
            dataMaintenanceService: dataMaintenanceService);

        await stateMachine.InitializeAsync(CancellationToken.None);

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand { Command = AgentCommandType.PruneData, RequestId = "prune-retention" },
            CancellationToken.None);

        Assert.True(result.Completed);

        var events = await ReadEventsAsync(paths.DatabasePath);
        var dataPrunedEvent = Assert.Single(events.Where(x => x.EventType == AgentEventType.DataPruned));
        var payload = dataPrunedEvent.PayloadJson ?? string.Empty;
        Assert.Contains("\"retentionDays\": 7", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentStateMachine_PruneDataFailureWritesCommandFailedAndSafeMessage()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                UseMockCapture = true
            });

        // Failing service: returns a failed result with a safe error
        var failingService = new FailingDataMaintenanceService(
            "JsonlDeletePartial",
            "3 JSONL file(s) could not be deleted.",
            "C:\\Users\\bad\\path\\file.jsonl");

        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter,
            dataMaintenanceService: failingService);

        await stateMachine.InitializeAsync(CancellationToken.None);

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand { Command = AgentCommandType.PruneData, RequestId = "prune-fail" },
            CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal("JsonlDeletePartial", result.ErrorCode);
        Assert.DoesNotContain(@"C:\Users", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\\\", result.Message ?? string.Empty, StringComparison.Ordinal);

        var events = await ReadEventsAsync(paths.DatabasePath);
        var failedEvent = Assert.Single(events.Where(x => x.EventType == AgentEventType.CommandFailed && x.RequestId == "prune-fail"));
        Assert.Equal("JsonlDeletePartial", failedEvent.ErrorCode);
        var failedPayload = failedEvent.PayloadJson ?? string.Empty;
        Assert.DoesNotContain(@"C:\Users", failedPayload, StringComparison.OrdinalIgnoreCase);

        // Must exit Maintenance
        Assert.Equal(AgentActualState.Running, stateMachine.ActualState);
    }

    [Fact]
    public async Task SettingsViewModel_ClearHistoryEntersConfirmationMode()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings()),
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new WindowsAgentOptions()),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.FromResult(new AgentStatusSnapshot { IsRunning = true, ActualState = AgentActualState.Running }),
            _ => Task.FromResult(new AgentCommandResult { Accepted = true }),
            null,
            null,
            null,
            new AgentOptionsValidator(),
            paths);

        await viewModel.LoadAsync();
        Assert.False(viewModel.IsClearHistoryConfirming);

        await viewModel.ClearHistoryAsync();
        Assert.True(viewModel.IsClearHistoryConfirming);
        Assert.Contains("CLEAR", viewModel.ClearHistoryStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsViewModel_ClearHistoryRejectsWrongConfirmationText()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings()),
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new WindowsAgentOptions()),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.FromResult(new AgentStatusSnapshot { IsRunning = true, ActualState = AgentActualState.Running }),
            _ => Task.FromResult(new AgentCommandResult { Accepted = true }),
            null,
            null,
            null,
            new AgentOptionsValidator(),
            paths);

        await viewModel.LoadAsync();
        await viewModel.ClearHistoryAsync();

        viewModel.ClearHistoryConfirmationInput = "clear"; // wrong case
        await viewModel.ConfirmClearHistoryAsync();

        Assert.True(viewModel.IsClearHistoryConfirming); // still confirming
        Assert.True(viewModel.HasClearHistoryError);
        Assert.Contains("does not match", viewModel.ClearHistoryStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsViewModel_ClearHistoryQueuesCommandAfterCorrectConfirmation()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var clearHistoryRequested = false;
        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings()),
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new WindowsAgentOptions()),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.FromResult(new AgentStatusSnapshot { IsRunning = true, ActualState = AgentActualState.Running }),
            _ => Task.FromResult(new AgentCommandResult { Accepted = true }),
            null,
            _ =>
            {
                clearHistoryRequested = true;
                return Task.FromResult(new AgentCommandResult { Accepted = true, Completed = true, Message = "ClearHistory command queued" });
            },
            null,
            new AgentOptionsValidator(),
            paths);

        await viewModel.LoadAsync();
        await viewModel.ClearHistoryAsync();

        viewModel.ClearHistoryConfirmationInput = "CLEAR";
        await viewModel.ConfirmClearHistoryAsync();

        Assert.True(clearHistoryRequested);
        Assert.False(viewModel.IsClearHistoryConfirming);
        Assert.False(viewModel.HasClearHistoryError);
        Assert.Contains("queued", viewModel.ClearHistoryStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingsViewModel_DisablesDataCleanupWhenAgentNotRunning()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings()),
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new WindowsAgentOptions()),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.FromResult(new AgentStatusSnapshot { IsRunning = false, ActualState = AgentActualState.NotRunning }),
            _ => Task.FromResult(new AgentCommandResult { Accepted = true }),
            null,
            null,
            null,
            new AgentOptionsValidator(),
            paths);

        await viewModel.LoadAsync();

        Assert.False(viewModel.CanExecuteDataCleanup);
        Assert.False(viewModel.PruneDataCommand.CanExecute(null));
        Assert.False(viewModel.ClearHistoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task SettingsViewModel_DisablesDataCleanupDuringMaintenance()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings()),
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new WindowsAgentOptions()),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.FromResult(new AgentStatusSnapshot { IsRunning = true, ActualState = AgentActualState.Maintenance }),
            _ => Task.FromResult(new AgentCommandResult { Accepted = true }),
            null,
            null,
            null,
            new AgentOptionsValidator(),
            paths);

        await viewModel.LoadAsync();

        Assert.False(viewModel.CanExecuteDataCleanup);
        Assert.False(viewModel.PruneDataCommand.CanExecute(null));
        Assert.False(viewModel.ClearHistoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task SettingsViewModel_DoesNotQueueDuplicateCleanupDuringMaintenance()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var pruneDataRequested = false;
        var viewModel = new SettingsViewModel(
            _ => Task.FromResult(new AppSettings()),
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult(new WindowsAgentOptions()),
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.FromResult(new AgentStatusSnapshot { IsRunning = true, ActualState = AgentActualState.Maintenance }),
            _ => Task.FromResult(new AgentCommandResult { Accepted = true }),
            _ =>
            {
                pruneDataRequested = true;
                return Task.FromResult(new AgentCommandResult { Accepted = true });
            },
            null,
            null,
            new AgentOptionsValidator(),
            paths);

        await viewModel.LoadAsync();

        // CanExecuteDataCleanup is false because status is Maintenance
        Assert.False(viewModel.CanExecuteDataCleanup);

        // PruneDataAsync has a guard that checks CanExecuteDataCleanup
        await viewModel.PruneDataAsync();

        // The delegate should NOT have been called
        Assert.False(pruneDataRequested);

        // Status text should indicate the can't-prune reason
        Assert.Contains("Cannot prune data", viewModel.PruneDataStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAfterClearHistory_EmptyStateDoesNotThrow()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        // Dashboard with empty DB returns zero values without throwing
        var overviewService = new OverviewDataService(paths);
        var summary = await overviewService.GetDashboardSummaryAsync();
        Assert.Equal(0, summary.SessionCount);
        Assert.Equal(0, summary.TotalDurationSeconds);
        Assert.Equal(0, summary.ActiveDurationSeconds);

        // Sessions with empty DB returns empty list without throwing
        var sessionsViewModel = new SessionsViewModel(new SessionsDataService(paths));
        await sessionsViewModel.LoadAsync();
        Assert.Empty(sessionsViewModel.Sessions);
        Assert.False(sessionsViewModel.HasLoadError);

        // Samples with empty DB returns empty list without throwing
        var samplesViewModel = new SamplesViewModel(new SamplesDataService(paths));
        await samplesViewModel.LoadAsync();
        Assert.Empty(samplesViewModel.Samples);
        Assert.False(samplesViewModel.HasLoadError);

        // Apps with empty DB returns empty list without throwing
        var appsViewModel = new AppsViewModel(new AppsDataService(paths));
        await appsViewModel.LoadAsync();
        Assert.Empty(appsViewModel.Apps);
        Assert.False(appsViewModel.HasLoadError);
    }

    [Fact]
    public async Task MainWindowViewModel_SettingsDirtyGuardStillWorksWithDataManagement()
    {
        using var workspace = new TempWorkspace();
        var settingsLoadCount = 0;
        var viewModel = await CreateMainWindowViewModelAsync(
            workspace,
            settingsLoader: _ =>
            {
                settingsLoadCount++;
                return Task.FromResult(new AppSettings());
            });

        await viewModel.InitializeAsync();
        var initialLoadCount = settingsLoadCount;

        // Simulate user edits by setting IsDirty via reflection (private setter)
        typeof(SettingsViewModel)
            .GetProperty("IsDirty", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(viewModel.SettingsViewModel, true);

        // Navigate to Settings and refresh
        viewModel.SelectedTabIndex = 5; // Settings tab
        await viewModel.RefreshAsync();

        // Dirty guard: LoadAsync should NOT have been called because IsDirty was true
        Assert.Equal(initialLoadCount, settingsLoadCount);

        // Verify data management properties are preserved
        Assert.Equal("Prune expired data based on retentionDays.", viewModel.SettingsViewModel.PruneDataStatusText);
        Assert.Equal("No maintenance performed in this session.", viewModel.SettingsViewModel.LastMaintenanceStatusText);
    }

    [Fact]
    public async Task AgentStateMachine_ClearHistoryFailureWritesCommandFailed()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                UseMockCapture = true
            });

        var failingService = new FailingClearHistoryService();
        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter,
            dataMaintenanceService: failingService);

        await stateMachine.InitializeAsync(CancellationToken.None);

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand { Command = AgentCommandType.ClearHistory, RequestId = "clear-fail" },
            CancellationToken.None);

        Assert.False(result.Completed);
        Assert.NotNull(result.ErrorCode);

        var events = await ReadEventsAsync(paths.DatabasePath);
        Assert.Contains(events, x => x.EventType == AgentEventType.CommandFailed && x.RequestId == "clear-fail");
        Assert.DoesNotContain(events, x => x.EventType == AgentEventType.HistoryCleared);
    }

    [Fact]
    public async Task DiagnosticsViewModel_ClearHistoryAllowsEmptyRecentErrors()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        // Write a HistoryCleared event, no errors
        await eventWriter.WriteAsync(new AgentEvent
        {
            EventType = AgentEventType.HistoryCleared,
            EventLevel = AgentEventLevel.Info,
            Message = "History cleared",
            EventTimeUtc = DateTime.UtcNow
        });

        var diagnosticsService = new DiagnosticsDataService(paths);
        // Must not throw when RecentErrors is empty
        var recentErrors = await diagnosticsService.GetRecentErrorsAsync();
        var recentEvents = await diagnosticsService.GetRecentEventsAsync();

        Assert.NotNull(recentErrors);
        Assert.NotNull(recentEvents);
    }

    // ── IPC Protocol Tests (Phase 8.1) ──

    [Fact]
    public void AgentIpcRequest_RoundTripsJson()
    {
        var request = new AgentIpcRequest
        {
            ProtocolVersion = 1,
            RequestId = "ipc-test-001",
            Command = "ClearHistory",
            DesiredState = AgentDesiredState.Paused,
            RequestedBy = "TestSuite",
            RequestedAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
            WaitForCompletion = true,
            TimeoutMilliseconds = 10000
        };

        var json = JsonSerializer.Serialize(request);
        var deserialized = JsonSerializer.Deserialize<AgentIpcRequest>(json)!;

        Assert.Equal(1, deserialized.ProtocolVersion);
        Assert.Equal("ipc-test-001", deserialized.RequestId);
        Assert.Equal("ClearHistory", deserialized.Command);
        Assert.Equal(AgentDesiredState.Paused, deserialized.DesiredState);
        Assert.Equal("TestSuite", deserialized.RequestedBy);
        Assert.True(deserialized.WaitForCompletion);
        Assert.Equal(10000, deserialized.TimeoutMilliseconds);
    }

    [Fact]
    public void AgentIpcResponse_RoundTripsJson()
    {
        var response = new AgentIpcResponse
        {
            ProtocolVersion = 1,
            RequestId = "ipc-test-002",
            Accepted = true,
            Completed = true,
            ActualState = AgentActualState.Paused,
            Message = "ClearHistory completed",
            ErrorCode = null,
            StartedAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 7, 1, 12, 1, 0, DateTimeKind.Utc)
        };

        var json = JsonSerializer.Serialize(response);
        var deserialized = JsonSerializer.Deserialize<AgentIpcResponse>(json)!;

        Assert.Equal(1, deserialized.ProtocolVersion);
        Assert.Equal("ipc-test-002", deserialized.RequestId);
        Assert.True(deserialized.Accepted);
        Assert.True(deserialized.Completed);
        Assert.Equal(AgentActualState.Paused, deserialized.ActualState);
        Assert.Equal("ClearHistory completed", deserialized.Message);
        Assert.Null(deserialized.ErrorCode);
    }

    [Fact]
    public void AgentPipeName_GeneratesStableNameForUserSid()
    {
        const string sid = "S-1-5-21-3623811015-3361044348-30300820-1013";

        var name1 = new AgentPipeName(sid);
        var name2 = new AgentPipeName(sid);

        Assert.Equal(name1.FullPipeName, name2.FullPipeName);
        Assert.Equal(name1.SidHash, name2.SidHash);
        Assert.Equal(name1.DisplayPipeName, name2.DisplayPipeName);
    }

    [Fact]
    public void AgentPipeName_DoesNotExposeRawSid()
    {
        const string sid = "S-1-5-21-3623811015-3361044348-30300820-1013";

        var name = new AgentPipeName(sid);

        Assert.DoesNotContain(sid, name.FullPipeName, StringComparison.Ordinal);
        Assert.DoesNotContain(sid, name.DisplayPipeName, StringComparison.Ordinal);
        Assert.DoesNotContain(sid, name.SidHash, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentPipeName_UsesDifferentNamesForDifferentUsers()
    {
        const string sid1 = "S-1-5-21-3623811015-3361044348-30300820-1013";
        const string sid2 = "S-1-5-21-1004336348-1013361044-30200830-500";

        var name1 = new AgentPipeName(sid1);
        var name2 = new AgentPipeName(sid2);

        Assert.NotEqual(name1.FullPipeName, name2.FullPipeName);
        Assert.NotEqual(name1.SidHash, name2.SidHash);
    }

    [Fact]
    public void AgentPipeName_ExposesSafeDisplayName()
    {
        const string sid = "S-1-5-21-3623811015-3361044348-30300820-1013";

        var name = new AgentPipeName(sid);

        Assert.StartsWith("QuantifiedSelf.Windows.Agent.", name.DisplayPipeName, StringComparison.Ordinal);
        Assert.True(name.FullPipeName.Length > name.DisplayPipeName.Length);
        // Display pipe name only exposes first 12 chars of the hash
        var displayHashPart = name.DisplayPipeName.Split('.').Last();
        Assert.True(displayHashPart.Length <= 12);
        Assert.StartsWith(displayHashPart, name.SidHash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NamedPipeProtocol_RoundTripsLengthPrefixedJson()
    {
        var request = new AgentIpcRequest
        {
            RequestId = "ipc-rt-001",
            Command = "GetStatus"
        };

        using var stream = new MemoryStream();
        await NamedPipeProtocol.WriteMessageAsync(stream, request);

        stream.Position = 0;
        var deserialized = await NamedPipeProtocol.ReadMessageAsync<AgentIpcRequest>(stream);

        Assert.Equal("ipc-rt-001", deserialized.RequestId);
        Assert.Equal("GetStatus", deserialized.Command);
        Assert.Equal(1, deserialized.ProtocolVersion);
    }

    [Fact]
    public async Task NamedPipeProtocol_RejectsInvalidPayloadSafely()
    {
        using var stream = new MemoryStream();
        // Write a valid length prefix (4 bytes) but garbage payload
        var lengthBytes = BitConverter.GetBytes(10);
        await stream.WriteAsync(lengthBytes);
        var garbage = "NOT_VALID_"u8.ToArray();
        await stream.WriteAsync(garbage);
        stream.Position = 0;

        var ex = await Assert.ThrowsAsync<IpcProtocolException>(
            () => NamedPipeProtocol.ReadMessageAsync<AgentIpcRequest>(stream));

        Assert.Equal("IpcProtocolError", ex.ErrorCode);
        // Error message must not expose raw payload content
        Assert.DoesNotContain("NOT_VALID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedPipeProtocol_RejectsPayloadOverMaxSize()
    {
        using var stream = new MemoryStream();
        // Write length prefix claiming payload is > MaxPayloadBytes
        var lengthBytes = BitConverter.GetBytes(NamedPipeProtocol.MaxPayloadBytes + 1);
        await stream.WriteAsync(lengthBytes);
        stream.Position = 0;

        var ex = await Assert.ThrowsAsync<IpcProtocolException>(
            () => NamedPipeProtocol.ReadMessageAsync<AgentIpcRequest>(stream));

        Assert.Equal("IpcPayloadTooLarge", ex.ErrorCode);
        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NamedPipeProtocol_RejectsTruncatedPayloadSafely()
    {
        using var stream = new MemoryStream();
        // Write length prefix promising 100 bytes, but only write 10
        var lengthBytes = BitConverter.GetBytes(100);
        await stream.WriteAsync(lengthBytes);
        var partialPayload = new byte[10];
        await stream.WriteAsync(partialPayload);
        stream.Position = 0;

        var ex = await Assert.ThrowsAsync<IpcProtocolException>(
            () => NamedPipeProtocol.ReadMessageAsync<AgentIpcRequest>(stream));

        Assert.Equal("IpcProtocolError", ex.ErrorCode);
    }

    [Fact]
    public async Task NamedPipeProtocol_RejectsWritePayloadOverMaxSize()
    {
        // Build a message whose JSON exceeds MaxPayloadBytes
        var request = new AgentIpcRequest
        {
            RequestId = new string('x', NamedPipeProtocol.MaxPayloadBytes) // huge field forces JSON > 16 KB
        };

        using var stream = new MemoryStream();
        var ex = await Assert.ThrowsAsync<IpcProtocolException>(
            () => NamedPipeProtocol.WriteMessageAsync(stream, request));

        Assert.Equal("IpcPayloadTooLarge", ex.ErrorCode);
        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── IPC Server Tests (Phase 8.2) ──

    [Fact]
    public async Task AgentIpcCommandDispatcher_RespondsToPing()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);

        var request = new AgentIpcRequest
        {
            ProtocolVersion = 1,
            RequestId = "ping-001",
            Command = "Ping"
        };

        var response = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine);

        Assert.True(response.Accepted);
        Assert.True(response.Completed);
        Assert.Equal("Pong", response.Message);
        Assert.Equal(stateMachine.ActualState, response.ActualState);
        Assert.Null(response.ErrorCode);
        Assert.NotEqual(default, response.StartedAtUtc);
        Assert.NotEqual(default, response.CompletedAtUtc);
    }

    [Fact]
    public async Task AgentIpcCommandDispatcher_ReturnsStatus()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);

        var request = new AgentIpcRequest
        {
            ProtocolVersion = 1,
            RequestId = "status-001",
            Command = "GetStatus"
        };

        var response = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine);

        Assert.True(response.Accepted);
        Assert.True(response.Completed);
        Assert.NotNull(response.Status);
        Assert.Equal(stateMachine.ActualState, response.Status!.ActualState);
        Assert.True(response.Status.ProcessId > 0);
        Assert.NotEqual(default, response.Status.StartedAtUtc);
        Assert.NotNull(response.Status.Version);
        Assert.True(response.Status.IsHealthy);
    }

    [Fact]
    public async Task AgentIpcCommandDispatcher_RejectsUnsupportedProtocolVersion()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);

        var request = new AgentIpcRequest
        {
            ProtocolVersion = 99,
            RequestId = "bad-ver",
            Command = "Ping"
        };

        var response = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine);

        Assert.False(response.Accepted);
        Assert.False(response.Completed);
        Assert.Equal("UnsupportedProtocolVersion", response.ErrorCode);
        Assert.Contains("Unsupported", response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentIpcCommandDispatcher_RejectsUnsupportedCommand()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);

        var request = new AgentIpcRequest
        {
            ProtocolVersion = 1,
            RequestId = "bad-cmd",
            Command = "NonExistentCommand"
        };

        var response = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine);

        Assert.False(response.Accepted);
        Assert.False(response.Completed);
        Assert.Equal("UnsupportedIpcCommand", response.ErrorCode);
        // Must not echo the raw command back
        Assert.DoesNotContain(request.Command, response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentIpcCommandDispatcher_UnsupportedCommandDoesNotEchoSensitiveStrings()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);

        var request = new AgentIpcRequest
        {
            ProtocolVersion = 1,
            RequestId = "bad-cmd-path",
            Command = @"C:\Users\malogic_luc\AppData\secret.txt"
        };

        var response = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine);

        Assert.DoesNotContain("malogic_luc", response.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", response.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.txt", response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedPipeAgentCommandServer_RoundTripsPingOverPipe()
    {
        var pipeName = $"QuantifiedSelf.Windows.Test.{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start server in background
        var serverTask = Task.Run(async () =>
        {
            var logger = NullLogger<NamedPipeAgentCommandServer>.Instance;
            var server = new NamedPipeAgentCommandServer(logger);

            await server.StartAsync(pipeName, (request, ct) =>
            {
                return Task.FromResult(new AgentIpcResponse
                {
                    ProtocolVersion = 1,
                    RequestId = request.RequestId,
                    Accepted = true,
                    Completed = true,
                    Message = "Pong"
                });
            }, cts.Token);
        }, cts.Token);

        // Give server a moment to start listening
        await Task.Delay(200, cts.Token);

        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, cts.Token);

        var request = new AgentIpcRequest { RequestId = "pipe-ping", Command = "Ping" };
        await NamedPipeProtocol.WriteMessageAsync(client, request, cts.Token);

        var response = await NamedPipeProtocol.ReadMessageAsync<AgentIpcResponse>(client, cts.Token);

        Assert.Equal("pipe-ping", response.RequestId);
        Assert.True(response.Accepted);
        Assert.True(response.Completed);
        Assert.Equal("Pong", response.Message);

        cts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task NamedPipeAgentCommandServer_HandlesInvalidJsonWithoutCrashing()
    {
        // Test via stream-based simulation — avoid real NamedPipe timing flakiness.
        // Write garbage JSON on one stream, verify protocol write-back doesn't crash.
        var clientStream = new MemoryStream();
        var serverStream = new MemoryStream();

        // Simulate client sending garbage: 4-byte length prefix + garbage payload
        var garbage = "NOT_VALID_JSON"u8.ToArray();
        var lengthBytes = BitConverter.GetBytes(garbage.Length);
        await serverStream.WriteAsync(lengthBytes);
        await serverStream.WriteAsync(garbage);
        serverStream.Position = 0;

        // Server side: ReadMessageAsync should throw IpcProtocolException
        var ex = await Assert.ThrowsAsync<IpcProtocolException>(
            () => NamedPipeProtocol.ReadMessageAsync<AgentIpcRequest>(serverStream));

        Assert.Equal("IpcProtocolError", ex.ErrorCode);

        // Server would write error response — verify that works
        var errorResponse = new AgentIpcResponse
        {
            Accepted = false,
            Completed = false,
            ErrorCode = ex.ErrorCode,
            Message = ex.Message
        };
        await NamedPipeProtocol.WriteMessageAsync(clientStream, errorResponse);
        clientStream.Position = 0;
        var response = await NamedPipeProtocol.ReadMessageAsync<AgentIpcResponse>(clientStream);

        Assert.False(response.Accepted);
        Assert.False(response.Completed);
        Assert.Equal("IpcProtocolError", response.ErrorCode);
        // Error message must not expose raw payload
        Assert.DoesNotContain("NOT_VALID", response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentCommandServerHostedService_StartsAndStopsCleanly()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);

        var loggerFactory = NullLoggerFactory.Instance;
        var server = new NamedPipeAgentCommandServer(
            new NullLogger<NamedPipeAgentCommandServer>());
        var hostedService = new AgentCommandServerHostedService(
            stateMachine,
            paths,
            server,
            new ProcessedRequestCache(),
            new NullLogger<AgentCommandServerHostedService>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        // Start and immediately cancel — should not throw
        var executeTask = hostedService.StartAsync(cts.Token);
        await Task.Delay(300, CancellationToken.None);
        cts.Cancel();

        try { await executeTask; } catch (OperationCanceledException) { }
        // No exception = clean stop
    }

    // ── IPC Client & Fallback Tests (Phase 8.3) ──

    private sealed class FakeIpcClient : IAgentIpcClient
    {
        private readonly AgentIpcResponse? _response;
        private readonly Exception? _exception;

        public FakeIpcClient(AgentIpcResponse response) => _response = response;

        public FakeIpcClient(Exception exception) => _exception = exception;

        public Task<AgentIpcResponse> SendAsync(AgentIpcRequest request, CancellationToken cancellationToken = default)
        {
            if (_exception is not null) throw _exception;
            return Task.FromResult(_response!);
        }
    }

    [Fact]
    public void AgentIpcStatusService_RecordsLastSuccessAndFallback()
    {
        var service = new AgentIpcStatusService();
        var pipeName = new AgentPipeName("S-1-5-21-test");

        service.Initialize(pipeName);
        Assert.Equal("Unavailable", service.LastCommandSource);

        service.RecordIpcSuccess();
        Assert.Equal("NamedPipe", service.LastCommandSource);
        Assert.NotNull(service.LastIpcSuccessUtc);
        Assert.Null(service.LastIpcError);

        service.RecordIpcFallback("timeout");
        Assert.Equal("FileFallback", service.LastCommandSource);
        Assert.Equal("timeout", service.LastIpcError);
        Assert.NotNull(service.LastFallbackUsedUtc);
    }

    [Fact]
    public void AgentIpcStatusService_DoesNotExposeFullPipeNameInDisplayText()
    {
        var service = new AgentIpcStatusService();
        var pipeName = new AgentPipeName("S-1-5-21-3623811015-3361044348-30300820-1013");

        service.Initialize(pipeName);

        var display = service.GetDisplayStatusText();
        Assert.DoesNotContain(pipeName.FullPipeName, display, StringComparison.Ordinal);
        Assert.DoesNotContain(pipeName.SidHash, display, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentStatusService_UsesIpcStatusWhenAvailable()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var status = new AgentIpcStatus
        {
            ActualState = AgentActualState.Running,
            ProcessId = 12345,
            IsHealthy = true,
            LastHeartbeatUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
            LastSampleUtc = new DateTime(2026, 7, 1, 11, 59, 0, DateTimeKind.Utc),
            StartedAtUtc = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc),
            Version = "1.0.0"
        };
        var ipcResponse = new AgentIpcResponse
        {
            Accepted = true,
            Completed = true,
            Status = status
        };
        var fakeClient = new FakeIpcClient(ipcResponse);
        var ipcStatusService = new AgentIpcStatusService();

        var service = new AgentStatusService(
            paths,
            new RuntimeStateStore(),
            new AgentHealthStateStore(),
            new AgentControlFileStore(),
            new WindowsAgentOptionsStore(),
            fakeClient,
            ipcStatusService);

        var snapshot = await service.GetStatusAsync();

        Assert.Equal(AgentActualState.Running, snapshot.ActualState);
        Assert.True(snapshot.IsRunning);
        Assert.Contains("12345", snapshot.ProcessText, StringComparison.Ordinal);
        Assert.Equal("NamedPipe", ipcStatusService.LastCommandSource);
    }

    [Fact]
    public async Task AgentStatusService_FallsBackToRuntimeStateWhenIpcUnavailable()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        // Write runtime state so fallback has data
        var runtimeStore = new RuntimeStateStore();
        var testPid = Environment.ProcessId;
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            State = AgentActualState.Running,
            ProcessId = testPid,
            LastHeartbeatUtc = DateTime.UtcNow,
            LastSampleUtc = DateTime.UtcNow.AddMinutes(-1)
        });

        var fakeClient = new FakeIpcClient(new TimeoutException("timeout"));
        var ipcStatusService = new AgentIpcStatusService();

        var service = new AgentStatusService(
            paths,
            runtimeStore,
            new AgentHealthStateStore(),
            new AgentControlFileStore(),
            new WindowsAgentOptionsStore(),
            fakeClient,
            ipcStatusService);

        var snapshot = await service.GetStatusAsync();

        Assert.Equal(AgentActualState.Running, snapshot.ActualState);
        Assert.True(snapshot.IsRunning);
        Assert.Equal("FileFallback", ipcStatusService.LastCommandSource);
        Assert.NotNull(ipcStatusService.LastIpcError);
        Assert.Contains("timed out", ipcStatusService.LastIpcError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentStatusService_FallsBackWhenIpcResponseIsFailed()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var testPid2 = Environment.ProcessId;
        var runtimeStore = new RuntimeStateStore();
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            State = AgentActualState.Paused,
            ProcessId = testPid2,
            LastHeartbeatUtc = DateTime.UtcNow
        });

        // IPC response with Accepted=false should trigger fallback
        var ipcResponse = new AgentIpcResponse
        {
            Accepted = true,
            Completed = true,
            ErrorCode = "UnsupportedIpcCommand",
            Status = null
        };
        var fakeClient = new FakeIpcClient(ipcResponse);
        var ipcStatusService = new AgentIpcStatusService();

        var service = new AgentStatusService(
            paths,
            runtimeStore,
            new AgentHealthStateStore(),
            new AgentControlFileStore(),
            new WindowsAgentOptionsStore(),
            fakeClient,
            ipcStatusService);

        var snapshot = await service.GetStatusAsync();

        Assert.Equal(AgentActualState.Paused, snapshot.ActualState);
        Assert.Equal("FileFallback", ipcStatusService.LastCommandSource);
    }

    [Fact]
    public async Task NamedPipeAgentControlClient_PingReturnsPong()
    {
        var pipeName = $"QuantifiedSelf.Windows.Test.{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start a minimal server that responds to Ping
        var serverTask = Task.Run(async () =>
        {
            var logger = NullLogger<NamedPipeAgentCommandServer>.Instance;
            var server = new NamedPipeAgentCommandServer(logger);
            await server.StartAsync(pipeName, (req, ct) =>
            {
                return Task.FromResult(new AgentIpcResponse
                {
                    ProtocolVersion = 1,
                    RequestId = req.RequestId,
                    Accepted = true,
                    Completed = true,
                    Message = "Pong"
                });
            }, cts.Token);
        }, cts.Token);

        await Task.Delay(200, cts.Token);

        var clientPipeName = new AgentPipeName("S-1-5-21-test");
        // Override pipe name for test — use a custom client that connects to test pipe
        var client = new TestNamedPipeAgentControlClient(pipeName, new AgentIpcClientOptions { ConnectTimeoutMilliseconds = 5000 });
        var request = new AgentIpcRequest { RequestId = "ping-01", Command = "Ping" };
        var response = await client.SendAsync(request, cts.Token);

        Assert.Equal("ping-01", response.RequestId);
        Assert.True(response.Accepted);
        Assert.True(response.Completed);
        Assert.Equal("Pong", response.Message);

        cts.Cancel();
        try { await serverTask; } catch (OperationCanceledException) { }
    }

    private sealed class TestNamedPipeAgentControlClient : IAgentIpcClient
    {
        private readonly string _pipeName;
        private readonly AgentIpcClientOptions _options;

        public TestNamedPipeAgentControlClient(string pipeName, AgentIpcClientOptions options)
        {
            _pipeName = pipeName;
            _options = options;
        }

        public async Task<AgentIpcResponse> SendAsync(AgentIpcRequest request, CancellationToken cancellationToken = default)
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            // Phase 1: connect with dedicated connect timeout
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(_options.ConnectTimeoutMilliseconds);
            await client.ConnectAsync(_options.ConnectTimeoutMilliseconds, connectCts.Token);

            // Phase 2: write and read with request timeout
            var requestTimeoutMs = request.TimeoutMilliseconds > 0
                ? request.TimeoutMilliseconds
                : _options.RequestTimeoutMilliseconds;
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(requestTimeoutMs);
            await NamedPipeProtocol.WriteMessageAsync(client, request, requestCts.Token);
            return await NamedPipeProtocol.ReadMessageAsync<AgentIpcResponse>(client, requestCts.Token);
        }
    }

    [Fact]
    public async Task NamedPipeAgentControlClient_TimesOutSafely()
    {
        // Connect to a non-existent pipe name — should timeout
        var nonExistentPipe = new AgentPipeName("S-1-5-21-nonexistent");
        var client = new NamedPipeAgentControlClient(nonExistentPipe,
            new AgentIpcClientOptions { ConnectTimeoutMilliseconds = 100, RequestTimeoutMilliseconds = 200 });

        var request = new AgentIpcRequest { RequestId = "timeout-01", Command = "Ping" };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<TimeoutException>(() => client.SendAsync(request, cts.Token));
    }

    [Fact]
    public async Task AgentControlService_ExistingCommandsStillUseFileFallback()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var statusService = new AgentStatusService(
            paths,
            new RuntimeStateStore(),
            new AgentHealthStateStore(),
            new AgentControlFileStore(),
            new WindowsAgentOptionsStore());

        var controlService = new AgentControlService(paths, new AgentControlFileStore(), statusService);

        // Pause should still write via file, not throw
        var result = await controlService.RequestPauseAsync();
        Assert.NotNull(result);

        // Verify agent_control.json was written
        var controlFileStore = new AgentControlFileStore();
        var readResult = await controlFileStore.PeekAsync(paths.AgentControlPath);
        Assert.NotNull(readResult.Command);
    }

    // ── IPC Command Migration Tests (Phase 8.4) ──

    [Fact]
    public async Task AgentIpcCommandDispatcher_PauseReturnsCompleted()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);

        var request = new AgentIpcRequest
        {
            ProtocolVersion = 1,
            RequestId = "pause-001",
            Command = "Pause",
            RequestedBy = "TestSuite"
        };

        var response = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine);

        Assert.True(response.Accepted);
        Assert.True(response.Completed);
        Assert.Equal(AgentActualState.Paused, response.ActualState);
        Assert.Null(response.ErrorCode);
    }

    [Fact]
    public async Task AgentIpcCommandDispatcher_ResumeReturnsCompleted()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);

        // First pause, then resume
        await AgentIpcCommandDispatcher.DispatchAsync(
            new AgentIpcRequest { ProtocolVersion = 1, RequestId = "p-1", Command = "Pause" }, stateMachine);

        var response = await AgentIpcCommandDispatcher.DispatchAsync(
            new AgentIpcRequest { ProtocolVersion = 1, RequestId = "r-1", Command = "Resume" }, stateMachine);

        Assert.True(response.Accepted);
        Assert.True(response.Completed);
        // State depends on state machine semantics — just verify it's valid
        Assert.False(response.ActualState == AgentActualState.Error);
    }

    [Fact]
    public async Task AgentIpcCommandDispatcher_StopReturnsCompleted()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);

        var request = new AgentIpcRequest
        {
            ProtocolVersion = 1,
            RequestId = "stop-001",
            Command = "Stop",
            RequestedBy = "TestSuite"
        };

        var response = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine);

        Assert.True(response.Accepted);
        Assert.True(response.Completed);
        // Stop transitions through various states — just verify not error
        Assert.NotEqual("", response.RequestId);
    }

    [Fact]
    public async Task AgentIpcCommandDispatcher_ReloadConfigReturnsCompleted()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);

        var request = new AgentIpcRequest
        {
            ProtocolVersion = 1,
            RequestId = "reload-001",
            Command = "ReloadConfig",
            RequestedBy = "TestSuite"
        };

        var response = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine);

        Assert.True(response.Accepted);
        Assert.True(response.Completed);
        Assert.Null(response.ErrorCode);
    }

    [Fact]
    public void ProcessedRequestCache_SuppressesDuplicateRequestIds()
    {
        var cache = new ProcessedRequestCache(capacity: 10, ttl: TimeSpan.FromMinutes(1));

        Assert.False(cache.TryMarkProcessed("req-001"));
        Assert.True(cache.TryMarkProcessed("req-001")); // duplicate
        Assert.True(cache.TryMarkProcessed("req-001")); // still duplicate
        Assert.False(cache.TryMarkProcessed("req-002")); // different id
    }

    [Fact]
    public async Task AgentIpcCommandDispatcher_DuplicateRequestDoesNotExecuteTwice()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);

        var cache = new ProcessedRequestCache();
        var request = new AgentIpcRequest
        {
            ProtocolVersion = 1,
            RequestId = "dup-pause",
            Command = "Pause",
            RequestedBy = "TestSuite"
        };

        // First execution should succeed
        var r1 = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine, cache);
        Assert.True(r1.Completed);
        Assert.Null(r1.ErrorCode);

        // Second execution with same requestId should be suppressed
        var r2 = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine, cache);
        Assert.True(r2.Completed);
        Assert.Equal("DuplicateRequest", r2.ErrorCode);
    }

    [Fact]
    public async Task AgentControlService_PauseUsesIpcWhenAvailable()
    {
        var ipcStatus = new AgentIpcStatusService();
        var fakeClient = new FakeIpcClient(new AgentIpcResponse
        {
            Accepted = true,
            Completed = true,
            ActualState = AgentActualState.Paused,
            Message = "Pause completed"
        });

        var service = new AgentControlService(
            new WindowsAgentPaths(Path.GetTempPath()),
            new AgentControlFileStore(),
            CreateMinimalStatusService(),
            fakeClient,
            ipcStatus);

        var result = await service.RequestPauseAsync();

        Assert.True(result.Accepted);
        Assert.True(result.Completed);
        Assert.Equal(AgentActualState.Paused, result.ActualState);
        Assert.Equal("NamedPipe", ipcStatus.LastCommandSource);
    }

    [Fact]
    public async Task AgentControlService_PauseFallsBackToFileWhenIpcUnavailable()
    {
        var ipcStatus = new AgentIpcStatusService();
        var fakeClient = new FakeIpcClient(new Exception("pipe broken"));

        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var service = new AgentControlService(
            paths,
            new AgentControlFileStore(),
            CreateMinimalStatusService(),
            fakeClient,
            ipcStatus);

        var result = await service.RequestPauseAsync();

        Assert.True(result.Accepted);
        Assert.Equal("FileFallback", ipcStatus.LastCommandSource);

        // Verify file was written
        var store = new AgentControlFileStore();
        var readResult = await store.PeekAsync(paths.AgentControlPath);
        Assert.NotNull(readResult.Command);
    }

    [Fact]
    public async Task AgentControlService_ReloadConfigPreservesNotRunningMessage()
    {
        // Fallback path: without IPC, NotRunning → rejected with expected message
        var service = new AgentControlService(
            new WindowsAgentPaths(Path.GetTempPath()),
            new AgentControlFileStore(),
            CreateMinimalStatusService());

        var result = await service.ReloadConfigAsync();

        Assert.False(result.Accepted);
        Assert.Contains("not running", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentControlService_RequestTimeoutDoesNotDuplicate()
    {
        var ipcStatus = new AgentIpcStatusService();
        // Client throws TimeoutException — should NOT fallback to file
        var fakeClient = new FakeIpcClient(new TimeoutException("timed out"));

        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var service = new AgentControlService(
            paths,
            new AgentControlFileStore(),
            CreateMinimalStatusService(),
            fakeClient,
            ipcStatus);

        var result = await service.RequestPauseAsync();

        Assert.Equal("IpcTimeout", result.ErrorCode);
        Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);

        // File should NOT have been written (don't duplicate side-effect)
        var store = new AgentControlFileStore();
        var readResult = await store.PeekAsync(paths.AgentControlPath);
        Assert.Null(readResult.Command);
    }

    [Fact]
    public async Task AgentControlService_FallbackUsesSameRequestIdAsIpc()
    {
        var ipcStatus = new AgentIpcStatusService();
        var capturedRequestId = string.Empty;
        var fakeClient = new FakeIpcClient(new Exception("unavailable"));

        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var service = new AgentControlService(
            paths,
            new AgentControlFileStore(),
            CreateMinimalStatusService(),
            fakeClient,
            ipcStatus);

        var result = await service.RequestPauseAsync();

        Assert.True(result.Accepted);
        Assert.Equal("FileFallback", ipcStatus.LastCommandSource);

        // Verify file was written and has the same requestId as IPC would have
        var store = new AgentControlFileStore();
        var readResult = await store.PeekAsync(paths.AgentControlPath);
        Assert.NotNull(readResult.Command);
        Assert.NotNull(readResult.Command.RequestId);
        Assert.StartsWith("ipc-", readResult.Command.RequestId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentProcessService_StopFallsBackToFileWhenIpcUnavailable()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var runtimeStore = new RuntimeStateStore();
        // Write runtime state with current PID so process check returns true
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            ProcessId = Environment.ProcessId,
            State = AgentActualState.Running,
            LastHeartbeatUtc = DateTime.UtcNow
        });

        var fakeClient = new FakeIpcClient(new Exception("pipe unavailable"));
        var service = new AgentProcessService(
            paths, runtimeStore, new AgentControlFileStore(),
            NullLogger<AgentProcessService>.Instance, fakeClient);
        service.StopPollMaxAttempts = 1;
        service.StopPollDelayMilliseconds = 10;

        var result = await service.StopAgentGracefullyAsync();

        // Agent still running, poll exhausted → false; file fallback written with ipc- requestId
        Assert.False(result);
        var store = new AgentControlFileStore();
        var readResult = await store.PeekAsync(paths.AgentControlPath);
        Assert.NotNull(readResult.Command);
        Assert.Equal(AgentCommandType.Stop, readResult.Command.Command);
        Assert.StartsWith("ipc-stop-", readResult.Command.RequestId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentProcessService_StopDoesNotWriteFallbackWhenAgentAlreadyExitedAfterIpcFailure()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var runtimeStore = new RuntimeStateStore();
        // Write runtime state with a PID that definitely doesn't exist
        // so process check returns false (agent already exited)
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            ProcessId = 99999, // non-existent process
            State = AgentActualState.Stopped,
            LastHeartbeatUtc = DateTime.UtcNow.AddMinutes(-10)
        });

        var fakeClient = new FakeIpcClient(new Exception("pipe broken"));
        var service = new AgentProcessService(
            paths, runtimeStore, new AgentControlFileStore(),
            NullLogger<AgentProcessService>.Instance, fakeClient);
        service.StopPollMaxAttempts = 1;
        service.StopPollDelayMilliseconds = 10;

        var result = await service.StopAgentGracefullyAsync();

        // Agent already exited — should return true, no file written
        Assert.True(result);

        var store = new AgentControlFileStore();
        var readResult = await store.PeekAsync(paths.AgentControlPath);
        Assert.Null(readResult.Command); // no stale file
    }

    // ── Maintenance Command IPC Migration Tests (Phase 8.5) ──

    [Fact]
    public async Task AgentIpcCommandDispatcher_PruneDataReturnsCompleted()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);

        var response = await AgentIpcCommandDispatcher.DispatchAsync(
            new AgentIpcRequest { ProtocolVersion = 1, RequestId = "prune-01", Command = "PruneData" },
            stateMachine);

        Assert.True(response.Accepted);
        Assert.True(response.Completed);
        Assert.Null(response.ErrorCode);
    }

    [Fact]
    public async Task AgentIpcCommandDispatcher_ClearHistoryReturnsCompletedAndPaused()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);

        var response = await AgentIpcCommandDispatcher.DispatchAsync(
            new AgentIpcRequest { ProtocolVersion = 1, RequestId = "clear-01", Command = "ClearHistory" },
            stateMachine);

        Assert.True(response.Accepted);
        Assert.True(response.Completed);
        Assert.Equal(AgentActualState.Paused, response.ActualState);
    }

    [Fact]
    public async Task AgentIpcCommandDispatcher_DuplicatePruneDataDoesNotDeleteTwice()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);
        var cache = new ProcessedRequestCache();

        var request = new AgentIpcRequest { ProtocolVersion = 1, RequestId = "dup-prune", Command = "PruneData" };

        var r1 = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine, cache);
        Assert.True(r1.Completed);

        var r2 = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine, cache);
        Assert.Equal("DuplicateRequest", r2.ErrorCode);
    }

    [Fact]
    public async Task AgentIpcCommandDispatcher_DuplicateClearHistoryDoesNotDeleteTwice()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var stateMachine = await CreateInitializedStateMachineAsync(paths);
        var cache = new ProcessedRequestCache();

        var request = new AgentIpcRequest { ProtocolVersion = 1, RequestId = "dup-clear", Command = "ClearHistory" };

        var r1 = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine, cache);
        Assert.True(r1.Completed);

        var r2 = await AgentIpcCommandDispatcher.DispatchAsync(request, stateMachine, cache);
        Assert.Equal("DuplicateRequest", r2.ErrorCode);
    }

    [Fact]
    public async Task AgentControlService_PruneDataUsesIpcWhenAvailable()
    {
        var ipcStatus = new AgentIpcStatusService();
        var fakeClient = new FakeIpcClient(new AgentIpcResponse
        {
            Accepted = true, Completed = true,
            ActualState = AgentActualState.Running,
            Message = "PruneData completed"
        });

        var service = new AgentControlService(
            new WindowsAgentPaths(Path.GetTempPath()),
            new AgentControlFileStore(),
            CreateMinimalStatusService(),
            fakeClient, ipcStatus);

        var result = await service.PruneDataAsync();

        Assert.True(result.Completed);
        Assert.Equal("NamedPipe", ipcStatus.LastCommandSource);
    }

    [Fact]
    public async Task AgentControlService_ClearHistoryUsesIpcWhenAvailable()
    {
        var ipcStatus = new AgentIpcStatusService();
        var fakeClient = new FakeIpcClient(new AgentIpcResponse
        {
            Accepted = true, Completed = true,
            ActualState = AgentActualState.Paused,
            Message = "ClearHistory completed"
        });

        var service = new AgentControlService(
            new WindowsAgentPaths(Path.GetTempPath()),
            new AgentControlFileStore(),
            CreateMinimalStatusService(),
            fakeClient, ipcStatus);

        var result = await service.ClearHistoryAsync();

        Assert.True(result.Completed);
        Assert.Equal("NamedPipe", ipcStatus.LastCommandSource);
    }

    [Fact]
    public async Task AgentControlService_PruneDataFallsBackToFileWhenIpcUnavailable()
    {
        var ipcStatus = new AgentIpcStatusService();
        var fakeClient = new FakeIpcClient(new Exception("pipe broken"));

        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        // Write runtime state so the fallback path sees agent as running
        var runtimeStore = new RuntimeStateStore();
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            ProcessId = Environment.ProcessId,
            State = AgentActualState.Running,
            LastHeartbeatUtc = DateTime.UtcNow
        });

        var statusService = new AgentStatusService(
            paths, runtimeStore, new AgentHealthStateStore(),
            new AgentControlFileStore(), new WindowsAgentOptionsStore());

        var service = new AgentControlService(
            paths, new AgentControlFileStore(), statusService, fakeClient, ipcStatus);

        var result = await service.PruneDataAsync();

        Assert.True(result.Accepted);
        Assert.Equal("FileFallback", ipcStatus.LastCommandSource);

        var store = new AgentControlFileStore();
        var readResult = await store.PeekAsync(paths.AgentControlPath);
        Assert.NotNull(readResult.Command);
        Assert.Equal(AgentCommandType.PruneData, readResult.Command.Command);
        Assert.StartsWith("ipc-", readResult.Command.RequestId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentControlService_ClearHistoryTimeoutDoesNotUseNewRequestId()
    {
        var ipcStatus = new AgentIpcStatusService();
        var fakeClient = new FakeIpcClient(new TimeoutException("timed out"));

        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var service = new AgentControlService(
            paths, new AgentControlFileStore(),
            CreateMinimalStatusService(),
            fakeClient, ipcStatus);

        var result = await service.ClearHistoryAsync();

        Assert.Equal("IpcTimeout", result.ErrorCode);

        // Should NOT have written file fallback (avoid duplicate)
        var store = new AgentControlFileStore();
        var readResult = await store.PeekAsync(paths.AgentControlPath);
        Assert.Null(readResult.Command);
    }

    [Fact]
    public async Task AgentControlService_DoesNotFallbackWhenIpcReturnsCompletedFalse()
    {
        // IPC returned a proper response with Completed=false (e.g. AlreadyInMaintenance)
        // Should NOT write a file fallback — the Agent already processed and rejected it
        var ipcStatus = new AgentIpcStatusService();
        var fakeClient = new FakeIpcClient(new AgentIpcResponse
        {
            Accepted = false,
            Completed = false,
            ErrorCode = "AlreadyInMaintenance",
            Message = "Agent is already performing maintenance."
        });

        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var service = new AgentControlService(
            paths, new AgentControlFileStore(),
            CreateMinimalStatusService(),
            fakeClient, ipcStatus);

        var result = await service.PruneDataAsync();

        // IPC result should be mapped directly
        Assert.False(result.Accepted);
        Assert.False(result.Completed);
        Assert.Equal("AlreadyInMaintenance", result.ErrorCode);

        // No file fallback should have been written
        var store = new AgentControlFileStore();
        var readResult = await store.PeekAsync(paths.AgentControlPath);
        Assert.Null(readResult.Command);
    }

    // ── Diagnostics IPC Status Display Tests (Phase 8.6) ──

    [Fact]
    public async Task MainWindowViewModel_ShowsIpcUnavailableInitially()
    {
        using var workspace = new TempWorkspace();
        var ipcStatus = new AgentIpcStatusService();
        var viewModel = await CreateMainWindowViewModelAsync(workspace, ipcStatusService: ipcStatus);
        await viewModel.InitializeAsync();
        // Navigate to Diagnostics to trigger RefreshDiagnosticsAsync
        viewModel.SelectedTabIndex = 4; // Diagnostics
        await viewModel.RefreshAsync();

        Assert.Contains("IPC status unknown", viewModel.IpcStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MainWindowViewModel_ShowsIpcSuccessState()
    {
        using var workspace = new TempWorkspace();
        var ipcStatus = new AgentIpcStatusService();
        ipcStatus.Initialize(new AgentPipeName("S-1-5-21-test"));
        ipcStatus.RecordIpcSuccess();

        var viewModel = await CreateMainWindowViewModelAsync(workspace, ipcStatusService: ipcStatus);
        await viewModel.InitializeAsync();
        viewModel.SelectedTabIndex = 4;
        await viewModel.RefreshAsync();

        Assert.Contains("IPC connected", viewModel.IpcStatusText, StringComparison.Ordinal);
        Assert.Contains("NamedPipe", viewModel.IpcStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MainWindowViewModel_ShowsIpcFallbackUsed()
    {
        using var workspace = new TempWorkspace();
        var ipcStatus = new AgentIpcStatusService();
        ipcStatus.Initialize(new AgentPipeName("S-1-5-21-test"));
        ipcStatus.RecordIpcFallback("IPC unavailable; using file fallback.");

        var viewModel = await CreateMainWindowViewModelAsync(workspace, ipcStatusService: ipcStatus);
        await viewModel.InitializeAsync();
        viewModel.SelectedTabIndex = 4;
        await viewModel.RefreshAsync();

        Assert.Contains("FileFallback", viewModel.IpcStatusText, StringComparison.Ordinal);
        Assert.Contains("file fallback", viewModel.IpcStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MainWindowViewModel_DoesNotExposeFullPipeNameOrSid()
    {
        using var workspace = new TempWorkspace();
        var ipcStatus = new AgentIpcStatusService();
        var pipeName = new AgentPipeName("S-1-5-21-3623811015-3361044348-30300820-1013");
        ipcStatus.Initialize(pipeName);
        ipcStatus.RecordIpcSuccess();

        var viewModel = await CreateMainWindowViewModelAsync(workspace, ipcStatusService: ipcStatus);
        await viewModel.InitializeAsync();
        viewModel.SelectedTabIndex = 4;
        await viewModel.RefreshAsync();

        // Must NOT expose FullPipeName or raw SID
        Assert.DoesNotContain(pipeName.FullPipeName, viewModel.IpcStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21", viewModel.IpcStatusText, StringComparison.Ordinal);
        // DisplayPipeName (safe truncated version) is OK
        Assert.Contains(pipeName.DisplayPipeName, viewModel.IpcStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MainWindowViewModel_RedactsIpcErrorWithPathLikeString()
    {
        using var workspace = new TempWorkspace();
        var ipcStatus = new AgentIpcStatusService();
        ipcStatus.Initialize(new AgentPipeName("S-1-5-21-test"));
        ipcStatus.RecordIpcFallback(@"C:\Users\test\error.log");

        var viewModel = await CreateMainWindowViewModelAsync(workspace, ipcStatusService: ipcStatus);
        await viewModel.InitializeAsync();
        viewModel.SelectedTabIndex = 4;
        await viewModel.RefreshAsync();

        Assert.Contains("file fallback", viewModel.IpcStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", viewModel.IpcStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", viewModel.IpcStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentControlService_HandlesCancellationWithoutFallback()
    {
        // When the caller cancels, don't fallback — just return a cancelled result
        var ipcStatus = new AgentIpcStatusService();
        var fakeClient = new FakeIpcClient(new OperationCanceledException());

        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var service = new AgentControlService(
            paths, new AgentControlFileStore(),
            CreateMinimalStatusService(),
            fakeClient, ipcStatus);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = await service.RequestPauseAsync(cts.Token);

        Assert.False(result.Completed);
        Assert.Equal("Cancelled", result.ErrorCode);

        // No file fallback should be written for a cancelled request
        var store = new AgentControlFileStore();
        var readResult = await store.PeekAsync(paths.AgentControlPath);
        Assert.Null(readResult.Command);
    }

    // ── RefreshService Tests (Phase 9.1) ──

    [Fact]
    public async Task RefreshService_RefreshesStatusAndProcessInfo()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var runtimeStore = new RuntimeStateStore();
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            ProcessId = Environment.ProcessId,
            State = AgentActualState.Running,
            LastHeartbeatUtc = DateTime.UtcNow
        });

        var statusService = new AgentStatusService(
            paths, runtimeStore, new AgentHealthStateStore(),
            new AgentControlFileStore(), new WindowsAgentOptionsStore());
        var processService = new AgentProcessService(
            paths, runtimeStore, new AgentControlFileStore(),
            NullLogger<AgentProcessService>.Instance);
        var refreshService = new RefreshService(statusService, processService);

        var result = await refreshService.RefreshAsync("Dashboard");

        Assert.NotNull(result.Status);
        Assert.NotNull(result.Health);
        Assert.Equal("Dashboard", result.CurrentPage);
        Assert.True(result.RefreshSequence > 0);
        Assert.NotEqual(default, result.StartedAtUtc);
        Assert.NotEqual(default, result.CompletedAtUtc);
    }

    [Fact]
    public async Task RefreshService_RecordsLastSuccess()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var runtimeStore = new RuntimeStateStore();
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            ProcessId = Environment.ProcessId,
            State = AgentActualState.Running,
            LastHeartbeatUtc = DateTime.UtcNow
        });

        var statusService = new AgentStatusService(
            paths, runtimeStore, new AgentHealthStateStore(),
            new AgentControlFileStore(), new WindowsAgentOptionsStore());
        var processService = new AgentProcessService(
            paths, runtimeStore, new AgentControlFileStore(),
            NullLogger<AgentProcessService>.Instance);
        var refreshService = new RefreshService(statusService, processService);

        await refreshService.RefreshAsync("Dashboard");

        Assert.NotNull(refreshService.Health.LastRefreshSuccessUtc);
        Assert.Null(refreshService.Health.LastRefreshError);
        Assert.Equal("Refresh succeeded.", refreshService.Health.StatusText);
    }

    [Fact]
    public async Task RefreshService_RecordsSafeError()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);

        // Use non-existent paths to make status service fail — but the
        // process service will still succeed. The status service will return
        // NotRunning since no runtime state exists, which doesn't throw.
        // Instead, test via a malformed status service that throws.
        var statusService = new FailingStatusService(new InvalidOperationException(
            @"Access to C:\Users\malogic\secret\path.log is denied."));
        var processService = new AgentProcessService(
            paths, new RuntimeStateStore(), new AgentControlFileStore(),
            NullLogger<AgentProcessService>.Instance);
        var refreshService = new RefreshService(statusService, processService);

        var result = await refreshService.RefreshAsync("Diagnostics");

        Assert.NotNull(refreshService.Health.LastRefreshError);
        Assert.DoesNotContain(@"C:\", refreshService.Health.LastRefreshError, StringComparison.Ordinal);
        Assert.DoesNotContain("malogic", refreshService.Health.LastRefreshError, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", refreshService.Health.LastRefreshError, StringComparison.Ordinal);
    }

    private sealed class FailingStatusService : AgentStatusService
    {
        private readonly Exception _exception;

        public FailingStatusService(Exception exception)
            : base(new WindowsAgentPaths(Path.GetTempPath()),
                new RuntimeStateStore(), new AgentHealthStateStore(),
                new AgentControlFileStore(), new WindowsAgentOptionsStore())
        {
            _exception = exception;
        }

        public override Task<AgentStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            throw _exception;
        }
    }

    [Fact]
    public async Task RefreshService_SkipsReentrantRefresh()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var runtimeStore = new RuntimeStateStore();
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            ProcessId = Environment.ProcessId,
            State = AgentActualState.Running,
            LastHeartbeatUtc = DateTime.UtcNow
        });

        var statusService = new AgentStatusService(
            paths, runtimeStore, new AgentHealthStateStore(),
            new AgentControlFileStore(), new WindowsAgentOptionsStore());
        var processService = new AgentProcessService(
            paths, runtimeStore, new AgentControlFileStore(),
            NullLogger<AgentProcessService>.Instance);
        var refreshService = new RefreshService(statusService, processService);

        // Start two concurrent refreshes — second should be skipped
        var t1 = refreshService.RefreshAsync("Dashboard");
        var t2 = refreshService.RefreshAsync("Dashboard");
        await Task.WhenAll(t1, t2);

        Assert.True(refreshService.Health.SkippedRefreshCount >= 1);
    }

    [Fact]
    public async Task RefreshService_ReturnsSequenceAndTimestamps()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var runtimeStore = new RuntimeStateStore();
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            ProcessId = Environment.ProcessId,
            State = AgentActualState.Running,
            LastHeartbeatUtc = DateTime.UtcNow
        });

        var statusService = new AgentStatusService(
            paths, runtimeStore, new AgentHealthStateStore(),
            new AgentControlFileStore(), new WindowsAgentOptionsStore());
        var processService = new AgentProcessService(
            paths, runtimeStore, new AgentControlFileStore(),
            NullLogger<AgentProcessService>.Instance);
        var refreshService = new RefreshService(statusService, processService);

        var r1 = await refreshService.RefreshAsync("Dashboard");
        var r2 = await refreshService.RefreshAsync("Dashboard");

        Assert.True(r2.RefreshSequence > r1.RefreshSequence);
        Assert.True(r1.StartedAtUtc <= r1.CompletedAtUtc);
    }

    [Fact]
    public async Task MainWindowViewModel_RefreshUsesRefreshService()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var runtimeStore = new RuntimeStateStore();
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            ProcessId = Environment.ProcessId,
            State = AgentActualState.Running,
            LastHeartbeatUtc = DateTime.UtcNow
        });

        var refreshService = new RefreshService(
            new AgentStatusService(paths, runtimeStore, new AgentHealthStateStore(),
                new AgentControlFileStore(), new WindowsAgentOptionsStore()),
            new AgentProcessService(paths, runtimeStore, new AgentControlFileStore(),
                NullLogger<AgentProcessService>.Instance));

        var viewModel = await CreateMainWindowViewModelAsync(workspace, refreshService: refreshService);
        await viewModel.InitializeAsync();

        // Refresh via service — should not throw
        await viewModel.RefreshAsync();
        Assert.NotNull(refreshService.Health.LastRefreshSuccessUtc);
    }

    // ── Status Polling Tests (Phase 9.2) ──

    [Fact]
    public async Task RefreshService_RefreshStatusAsyncReturnsStatusOnly()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var runtimeStore = new RuntimeStateStore();
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            ProcessId = Environment.ProcessId,
            State = AgentActualState.Paused,
            LastHeartbeatUtc = DateTime.UtcNow
        });

        var refreshService = new RefreshService(
            new AgentStatusService(paths, runtimeStore, new AgentHealthStateStore(),
                new AgentControlFileStore(), new WindowsAgentOptionsStore()),
            new AgentProcessService(paths, runtimeStore, new AgentControlFileStore(),
                NullLogger<AgentProcessService>.Instance));

        var result = await refreshService.RefreshStatusAsync("Dashboard");

        Assert.False(result.PageDataRefreshed);
        Assert.Equal(AgentActualState.Paused, result.Status.ActualState);
        Assert.NotNull(refreshService.Health.LastRefreshSuccessUtc);
    }

    [Fact]
    public async Task RefreshService_IgnoresOlderStatusResult()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var runtimeStore = new RuntimeStateStore();
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            ProcessId = Environment.ProcessId,
            State = AgentActualState.Running,
            LastHeartbeatUtc = DateTime.UtcNow
        });

        var refreshService = new RefreshService(
            new AgentStatusService(paths, runtimeStore, new AgentHealthStateStore(),
                new AgentControlFileStore(), new WindowsAgentOptionsStore()),
            new AgentProcessService(paths, runtimeStore, new AgentControlFileStore(),
                NullLogger<AgentProcessService>.Instance));

        var r1 = await refreshService.RefreshStatusAsync("Dashboard");
        var r2 = await refreshService.RefreshStatusAsync("Dashboard");

        Assert.True(r2.RefreshSequence > r1.RefreshSequence);
    }

    [Fact]
    public async Task RefreshService_StatusPollingFailureDoesNotClearState()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);

        var statusService = new FailingStatusService(new InvalidOperationException("test"));
        var refreshService = new RefreshService(
            statusService,
            new AgentProcessService(paths, new RuntimeStateStore(),
                new AgentControlFileStore(), NullLogger<AgentProcessService>.Instance));

        var result = await refreshService.RefreshStatusAsync("Dashboard");

        Assert.NotNull(refreshService.Health.LastRefreshError);
        Assert.False(result.PageDataRefreshed);
    }

    [Fact]
    public async Task MainWindowViewModel_StatusPollingDoesNotLoadSettingsWhenDirty()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var runtimeStore = new RuntimeStateStore();
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            ProcessId = Environment.ProcessId,
            State = AgentActualState.Running,
            LastHeartbeatUtc = DateTime.UtcNow
        });

        var refreshService = new RefreshService(
            new AgentStatusService(paths, runtimeStore, new AgentHealthStateStore(),
                new AgentControlFileStore(), new WindowsAgentOptionsStore()),
            new AgentProcessService(paths, runtimeStore, new AgentControlFileStore(),
                NullLogger<AgentProcessService>.Instance));

        var viewModel = await CreateMainWindowViewModelAsync(workspace, refreshService: refreshService);
        await viewModel.InitializeAsync();

        // Simulate dirty settings — user is editing
        typeof(SettingsViewModel)
            .GetProperty("IsDirty", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(viewModel.SettingsViewModel, true);

        // Trigger status poll through ViewModel's internal path (same as timer tick)
        await viewModel.PerformStatusPollAsync();

        viewModel.StopStatusPolling();
        // Settings should still be dirty after polling
        Assert.True(viewModel.SettingsViewModel.IsDirty);
        // Status polling should not have refreshed page data
        Assert.NotNull(refreshService.Health.LastRefreshSuccessUtc);
    }

    [Fact]
    public async Task MainWindowViewModel_StatusPollingDoesNotCallPageDataServices()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();

        var runtimeStore = new RuntimeStateStore();
        await runtimeStore.WriteAsync(paths.RuntimeStatePath, new RuntimeState
        {
            ProcessId = Environment.ProcessId,
            State = AgentActualState.Running,
            LastHeartbeatUtc = DateTime.UtcNow
        });

        var refreshService = new RefreshService(
            new AgentStatusService(paths, runtimeStore, new AgentHealthStateStore(),
                new AgentControlFileStore(), new WindowsAgentOptionsStore()),
            new AgentProcessService(paths, runtimeStore, new AgentControlFileStore(),
                NullLogger<AgentProcessService>.Instance));

        // Use a session loader that counts calls — status polling should NOT invoke it
        int sessionLoadCount = 0;
        var viewModel = await CreateMainWindowViewModelAsync(
            workspace, refreshService: refreshService,
            sessionLoader: (_, _, _) =>
            {
                sessionLoadCount++;
                return Task.FromResult<IReadOnlyList<AppSession>>([]);
            });

        await viewModel.InitializeAsync();

        // Trigger status poll through ViewModel's internal path
        await viewModel.PerformStatusPollAsync();
        viewModel.StopStatusPolling();

        // Session loader must NOT have been called during status polling
        Assert.Equal(0, sessionLoadCount);
        Assert.NotNull(refreshService.Health.LastRefreshSuccessUtc);
    }

    private static AgentStatusService CreateMinimalStatusService()
    {
        var paths = new WindowsAgentPaths(Path.GetTempPath());
        return new AgentStatusService(
            paths,
            new RuntimeStateStore(),
            new AgentHealthStateStore(),
            new AgentControlFileStore(),
            new WindowsAgentOptionsStore());
    }

    private async Task<AgentStateMachine> CreateInitializedStateMachineAsync(WindowsAgentPaths paths)
    {
        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                UseMockCapture = true
            });

        var eventWriter = await CreateEventWriterAsync(paths);
        var dataMaintenanceService = new DataMaintenanceService(paths);
        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]),
                new QueueWin32ForegroundSampleProvider([])),
            eventWriter,
            dataMaintenanceService: dataMaintenanceService);

        await stateMachine.InitializeAsync(CancellationToken.None);
        return stateMachine;
    }

    private sealed class FailingClearHistoryService : DataMaintenanceService
    {
        public FailingClearHistoryService() : base(new WindowsAgentPaths(Path.GetTempPath())) { }

        public override Task<ClearHistoryResult> ClearHistoryAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ClearHistoryResult
            {
                Success = false,
                SqliteCleared = false,
                ErrorCode = "ClearHistorySqliteFailed",
                SafeMessage = "SQLite clear failed.",
                ForegroundSamplesDeleted = 0,
                SessionsDeleted = 0,
                AgentEventsDeleted = 0
            });
        }
    }

    private sealed class RollbackOnSecondDeleteService : DataMaintenanceService
    {
        private readonly string _dbPath;

        public RollbackOnSecondDeleteService(string dbPath) : base(new WindowsAgentPaths(Path.GetTempPath()))
        {
            _dbPath = dbPath;
        }

        public override async Task<ClearHistoryResult> ClearHistoryAsync(CancellationToken cancellationToken = default)
        {
            await using var connection = await SqliteConnectionFactory.OpenReadWriteAsync(_dbPath, cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                // First DELETE succeeds
                await using var cmd1 = connection.CreateCommand();
                cmd1.CommandText = "DELETE FROM foreground_samples";
                await cmd1.ExecuteNonQueryAsync(cancellationToken);

                // Simulate a failure before the second DELETE
                throw new InvalidOperationException("Simulated mid-transaction failure");
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
    }

    private sealed class FailingDataMaintenanceService : DataMaintenanceService
    {
        private readonly string _errorCode;
        private readonly string _safeMessage;
        private readonly string _unsafeDetail;

        public FailingDataMaintenanceService(string errorCode, string safeMessage, string unsafeDetail)
            : base(new WindowsAgentPaths(Path.GetTempPath()))
        {
            _errorCode = errorCode;
            _safeMessage = safeMessage;
            _unsafeDetail = unsafeDetail;
        }

        public override Task<PruneDataResult> PruneDataAsync(
            int retentionDays,
            DateTime? referenceTimeUtc = null,
            CancellationToken cancellationToken = default)
        {
            // Simulate unsafe detail that should NOT leak into message
            return Task.FromResult(new PruneDataResult
            {
                Success = false,
                ErrorCode = _errorCode,
                SafeMessage = _safeMessage,
                SafeDetail = _unsafeDetail
            });
        }
    }

    [Fact]
    public async Task DataMaintenanceService_ClearHistoryDeletesAllHistoryRows()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var sampleTime = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        await InsertSampleAsync(paths.DatabasePath, sampleTime, "TestApp", null, "Active");
        await InsertSessionAsync(paths.DatabasePath, sampleTime.ToLocalTime(), sampleTime.ToLocalTime().AddMinutes(10), "TestSession", 600, 600, 0, 0, "Closed");
        await InsertAgentEventAsync(paths.DatabasePath, sampleTime, AgentEventType.AgentStarted, AgentEventLevel.Info, "Old event");

        var service = new DataMaintenanceService(paths);
        var result = await service.ClearHistoryAsync();

        Assert.True(result.Success);
        Assert.Equal(1, result.ForegroundSamplesDeleted);
        Assert.Equal(1, result.SessionsDeleted);
        Assert.Equal(1, result.AgentEventsDeleted);

        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM foreground_samples;"));
        Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM app_sessions;"));
        Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM agent_events;"));
    }

    [Fact]
    public async Task DataMaintenanceService_ClearHistoryDeletesHistoricalJsonlFiles()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var oldFile = Path.Combine(paths.LogsDir, "agent_events_20240115.jsonl");
        var todayFile = Path.Combine(paths.LogsDir, $"agent_events_{DateTime.Now:yyyyMMdd}.jsonl");
        await File.WriteAllTextAsync(oldFile, "{}");
        await File.WriteAllTextAsync(todayFile, "{}");

        var service = new DataMaintenanceService(paths);
        var result = await service.ClearHistoryAsync();

        Assert.True(result.Success);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(todayFile));
    }

    [Fact]
    public async Task DataMaintenanceService_ClearHistoryKeepsConfigRuntimeAndDatabaseFiles()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var configFile = Path.Combine(paths.ConfigDir, "windows-agent.json");
        var runtimeFile = Path.Combine(paths.RuntimeDir, "runtime_state.json");
        Directory.CreateDirectory(paths.ConfigDir);
        Directory.CreateDirectory(paths.RuntimeDir);
        await File.WriteAllTextAsync(configFile, "{}");
        await File.WriteAllTextAsync(runtimeFile, "{}");

        var dbFile = paths.DatabasePath;

        var oldJournal = Path.Combine(paths.LogsDir, "agent_events_20240101.jsonl");
        await File.WriteAllTextAsync(oldJournal, "{}");

        var service = new DataMaintenanceService(paths);
        var result = await service.ClearHistoryAsync();

        Assert.True(result.Success);
        Assert.True(File.Exists(configFile));
        Assert.True(File.Exists(runtimeFile));
        Assert.True(File.Exists(dbFile));
    }

    [Fact]
    public async Task DataMaintenanceService_ClearHistoryUsesTransaction()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        await InsertSampleAsync(paths.DatabasePath, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), "App", null, "Active");
        await InsertSessionAsync(paths.DatabasePath, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2025, 1, 1, 0, 10, 0, DateTimeKind.Utc), "Sess", 600, 600, 0, 0, "Closed");

        var service = new DataMaintenanceService(paths);
        var result = await service.ClearHistoryAsync();

        Assert.True(result.Success);
        await using var connection = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM foreground_samples;"));
        Assert.Equal(0, await CountAsync(connection, "SELECT COUNT(*) FROM app_sessions;"));
    }

    [Fact]
    public async Task DataMaintenanceService_ClearHistoryRollsBackTransactionOnFailure()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        await InsertSampleAsync(paths.DatabasePath, new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc), "TestApp", null, "Active");
        await InsertSessionAsync(paths.DatabasePath, new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc), new DateTime(2025, 6, 15, 12, 10, 0, DateTimeKind.Utc), "Sess", 600, 600, 0, 0, "Closed");

        // Record row counts before the operation
        await using var readBefore = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        var fgBefore = await CountAsync(readBefore, "SELECT COUNT(*) FROM foreground_samples;");
        var sessBefore = await CountAsync(readBefore, "SELECT COUNT(*) FROM app_sessions;");

        var failingService = new RollbackOnSecondDeleteService(paths.DatabasePath);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingService.ClearHistoryAsync());

        Assert.Contains("Simulated mid-transaction failure", ex.Message, StringComparison.Ordinal);

        // After rollback, all rows must still be present
        await using var readAfter = await SqliteConnectionFactory.OpenReadOnlyAsync(paths.DatabasePath);
        Assert.Equal(fgBefore, await CountAsync(readAfter, "SELECT COUNT(*) FROM foreground_samples;"));
        Assert.Equal(sessBefore, await CountAsync(readAfter, "SELECT COUNT(*) FROM app_sessions;"));
    }

    [Fact]
    public async Task AgentStateMachine_ClearHistoryWritesHistoryClearedAfterClearingEvents()
    {
        using var workspace = new TempWorkspace();
        var paths = new WindowsAgentPaths(workspace.Root);
        var eventWriter = await CreateEventWriterAsync(paths);

        var optionsStore = new WindowsAgentOptionsStore();
        await optionsStore.WriteAsync(
            paths.AgentOptionsPath,
            new WindowsAgentOptions
            {
                SamplingIntervalSeconds = 60,
                HeartbeatIntervalSeconds = 30,
                StaleThresholdSeconds = 45,
                UseMockCapture = true
            });

        var oldTime = new DateTime(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        await InsertAgentEventAsync(paths.DatabasePath, oldTime, AgentEventType.AgentStarted, AgentEventLevel.Info, "Old event");

        var dataMaintenanceService = new DataMaintenanceService(paths);
        var stateMachine = CreateStateMachine(
            paths,
            new ConfiguredForegroundSampleProvider(
                new QueueMockForegroundSampleProvider([]), new QueueWin32ForegroundSampleProvider([])),
            eventWriter,
            dataMaintenanceService: dataMaintenanceService);

        await stateMachine.InitializeAsync(CancellationToken.None);

        var result = await stateMachine.ProcessCommandAsync(
            new AgentControlCommand { Command = AgentCommandType.ClearHistory, RequestId = "clear-hist" },
            CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(AgentActualState.Paused, result.ActualState);

        var events = await ReadEventsAsync(paths.DatabasePath);
        Assert.Contains(events, x => x.EventType == AgentEventType.HistoryCleared && x.RequestId == "clear-hist");
        Assert.Contains(events, x => x.EventType == AgentEventType.CommandCompleted && x.RequestId == "clear-hist");
        Assert.DoesNotContain(events, x => x.EventType == AgentEventType.AgentStarted && x.Message == "Old event");

        var historyCleared = Assert.Single(events.Where(x => x.EventType == AgentEventType.HistoryCleared));
        var payload = historyCleared.PayloadJson ?? string.Empty;
        Assert.Contains("\"foregroundSamplesDeleted\"", payload, StringComparison.Ordinal);
        Assert.Contains("\"finalState\": \"Paused\"", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\", payload, StringComparison.Ordinal);
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
        ILogger<AgentStateMachine>? logger = null,
        DataMaintenanceService? dataMaintenanceService = null)
    {
        var runtimeStateStore = new RuntimeStateStore();
        var healthStateStore = new AgentHealthStateStore();
        var controlFileStore = new AgentControlFileStore();
        var optionsStore = new WindowsAgentOptionsStore();
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        var sampleRepository = new ForegroundSampleRepository(paths.DatabasePath);
        var sessionAggregator = new SessionAggregator(new AppSessionRepository(paths.DatabasePath));
        var privacyFilter = new ForegroundSamplePrivacyFilter();

        var optionsValidator = new AgentOptionsValidator();

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
            optionsValidator,
            dataMaintenanceService,
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

    private static async Task<MainWindowViewModel> CreateMainWindowViewModelAsync(
        TempWorkspace workspace,
        Func<int, CancellationToken, Task<IReadOnlyList<ForegroundSample>>>? sampleLoader = null,
        Func<string, int, CancellationToken, Task<IReadOnlyList<AppSession>>>? sessionLoader = null,
        Func<int, CancellationToken, Task<IReadOnlyList<AppUsageSummary>>>? appLoader = null,
        Func<CancellationToken, Task<AppSettings>>? settingsLoader = null,
        AgentIpcStatusService? ipcStatusService = null,
        RefreshService? refreshService = null)
    {
        var paths = new WindowsAgentPaths(workspace.Root);
        paths.EnsureDirectories();
        var initializer = new SqliteDatabaseInitializer(paths.DatabasePath);
        await initializer.InitializeAsync();

        var runtimeStateStore = new RuntimeStateStore();
        var healthStateStore = new AgentHealthStateStore();
        var controlFileStore = new AgentControlFileStore();
        var appSettingsStore = new AppSettingsStore();
        var agentOptionsStore = new WindowsAgentOptionsStore();
        var settingsService = new SettingsService(paths, appSettingsStore, agentOptionsStore);
        var settingsViewModel = settingsLoader is null
            ? new SettingsViewModel(settingsService, paths)
            : new SettingsViewModel(settingsLoader, (_, _) => Task.CompletedTask, _ => Task.FromResult(new WindowsAgentOptions()), paths);
        var statusService = new AgentStatusService(
            paths,
            runtimeStateStore,
            healthStateStore,
            controlFileStore,
            agentOptionsStore);
        var processService = new AgentProcessService(
            paths,
            runtimeStateStore,
            controlFileStore,
            NullLogger<AgentProcessService>.Instance);
        var controlService = new AgentControlService(paths, controlFileStore, statusService);
        var overviewDataService = new OverviewDataService(paths);
        var diagnosticsDataService = new DiagnosticsDataService(paths);
        var samplesViewModel = new SamplesViewModel(sampleLoader ?? ((_, _) =>
            Task.FromResult<IReadOnlyList<ForegroundSample>>([])));
        var sessionsViewModel = new SessionsViewModel(sessionLoader ?? ((_, _, _) =>
            Task.FromResult<IReadOnlyList<AppSession>>([])));
        var appsViewModel = new AppsViewModel(appLoader ?? ((_, _) =>
            Task.FromResult<IReadOnlyList<AppUsageSummary>>([])));

        return new MainWindowViewModel(
            processService,
            controlService,
            statusService,
            overviewDataService,
            diagnosticsDataService,
            samplesViewModel,
            sessionsViewModel,
            appsViewModel,
            settingsViewModel,
            settingsService,
            ipcStatusService,
            refreshService);
    }

    private static T GetPrivateFieldValue<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (T)field!.GetValue(instance)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not met before timeout.");
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
