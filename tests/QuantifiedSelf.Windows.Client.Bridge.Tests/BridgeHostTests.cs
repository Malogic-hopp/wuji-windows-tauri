using System.Text;
using System.Text.Json;
using QuantifiedSelf.Windows.ApplicationLayer.Activity;
using QuantifiedSelf.Windows.ApplicationLayer.Models;
using QuantifiedSelf.Windows.Client.Bridge.Generated;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Runtime;

namespace QuantifiedSelf.Windows.Client.Bridge.Tests;

[Trait("Category", "Fast")]
public sealed class BridgeHostTests
{
    [Fact]
    public void LaunchOptions_DefaultsToDevelopmentAndRejectsOtherChannels()
    {
        Assert.Equal("dev", BridgeLaunchOptions.Parse([]).ChannelName);
        Assert.Equal("dev", BridgeLaunchOptions.Parse(["--channel", "DEV"]).ChannelName);

        Assert.Throws<ArgumentException>(() => BridgeLaunchOptions.Parse(["--channel", "prod"]));
        Assert.Throws<ArgumentException>(() => BridgeLaunchOptions.Parse(["--channel", "custom"]));
        Assert.Throws<ArgumentException>(() => BridgeLaunchOptions.Parse(["--data-root", "ignored"]));
    }

    [Fact]
    public async Task HelloInitializeAndShutdown_ReturnTypedResultsAndDisposeClient()
    {
        var client = new FakeWujiClient();
        var result = await RunHostAsync(
            client,
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        Assert.Equal(3, result.Responses.Count);
        Assert.Equal("1.0", Result(result.Responses[0]).GetProperty("apiVersion").GetString());
        Assert.Equal("dev", Result(result.Responses[1]).GetProperty("channelName").GetString());
        Assert.Equal("WUJI Dev", Result(result.Responses[1]).GetProperty("productDisplayName").GetString());
        Assert.True(Result(result.Responses[2]).GetProperty("accepted").GetBoolean());
        Assert.Equal(1, client.InitializeCount);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(string.Empty, result.Log);
    }

    [Fact]
    public async Task Initialize_IsIdempotentAcrossRequestIdsAndDuplicateIdUsesCachedResponse()
    {
        var client = new FakeWujiClient();
        var result = await RunHostAsync(
            client,
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("init-2", BridgeProtocol.InitializeMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        Assert.Equal(result.Responses[1].GetRawText(), result.Responses[2].GetRawText());
        Assert.Equal(1, client.InitializeCount);
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task InitializeBeforeHello_IsRejectedWithoutTouchingClient()
    {
        var client = new FakeWujiClient();
        var result = await RunHostAsync(
            client,
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        Assert.Equal("handshake_required", Error(result.Responses[0]).GetProperty("code").GetString());
        Assert.Equal(0, client.InitializeCount);
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task RequestIdReusedForDifferentMethod_IsRejected()
    {
        var result = await RunHostAsync(
            new FakeWujiClient(),
            Request("same-id", BridgeProtocol.HelloMethod),
            Request("same-id", BridgeProtocol.InitializeMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        Assert.Equal("invalid_request", Error(result.Responses[1]).GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnsupportedApiVersion_IsRejected()
    {
        var result = await RunHostAsync(
            new FakeWujiClient(),
            Request("hello-1", BridgeProtocol.HelloMethod, apiVersion: "2.0"));

        Assert.Equal("unsupported_api_version", Error(result.Responses[0]).GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvalidJsonUnknownMethodAndUnknownProperties_ReturnStableErrors()
    {
        var result = await RunHostAsync(
            new FakeWujiClient(),
            "{not-json}",
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("unknown-1", "system.execute"),
            RequestWithUnknownProperty("invalid-1"),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        Assert.Equal("parse_error", Error(result.Responses[0]).GetProperty("code").GetString());
        Assert.Equal("method_not_found", Error(result.Responses[2]).GetProperty("code").GetString());
        Assert.Equal("invalid_request", Error(result.Responses[3]).GetProperty("code").GetString());
    }

    [Fact]
    public async Task OversizedPayload_ReturnsErrorAndHostContinues()
    {
        var oversized = JsonSerializer.Serialize(new
        {
            value = new string('x', 400)
        });
        var result = await RunHostAsync(
            new FakeWujiClient(),
            new BridgeHostOptions
            {
                MaxPayloadBytes = 300,
                RequestTimeout = TimeSpan.FromSeconds(1)
            },
            oversized,
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        Assert.Equal("payload_too_large", Error(result.Responses[0]).GetProperty("code").GetString());
        Assert.True(Result(result.Responses[2]).GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task InvalidUtf8_ReturnsParseErrorAndHostContinues()
    {
        var validRequests = Encoding.UTF8.GetBytes(string.Join(
            '\n',
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod)) + "\n");
        var bytes = new byte[validRequests.Length + 2];
        bytes[0] = 0xFF;
        bytes[1] = (byte)'\n';
        validRequests.CopyTo(bytes, 2);
        await using var input = new MemoryStream(bytes);
        await using var output = new MemoryStream();
        var client = new FakeWujiClient();
        var host = new BridgeHost(client, new BridgeHostOptions(), TextWriter.Null);

        await host.RunAsync(input, output);

        var responses = Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        Assert.Equal("parse_error", Error(responses[0]).GetProperty("code").GetString());
        Assert.True(Result(responses[2]).GetProperty("accepted").GetBoolean());
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task InitializeTimeout_ReturnsSafeRetryableErrorAndHostCanShutdown()
    {
        var client = new FakeWujiClient(async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var result = await RunHostAsync(
            client,
            new BridgeHostOptions
            {
                RequestTimeout = TimeSpan.FromMilliseconds(25)
            },
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        var error = Error(result.Responses[1]);
        Assert.Equal("request_timeout", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("data").GetProperty("retryable").GetBoolean());
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task HostCancellation_CancelsInitializeAndDisposesWithoutStoppingAgent()
    {
        var initializeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeWujiClient(async cancellationToken =>
        {
            initializeStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        var input = Input(
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod));
        await using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        var host = new BridgeHost(client, new BridgeHostOptions(), TextWriter.Null);

        var runTask = host.RunAsync(input, output, cancellation.Token);
        await initializeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(0, client.AgentAccessCount);
    }

    [Fact]
    public async Task EndOfInput_DisposesClientWithoutAccessingAgent()
    {
        var client = new FakeWujiClient();
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        var host = new BridgeHost(client, new BridgeHostOptions(), TextWriter.Null);

        await host.RunAsync(input, output);

        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(0, client.AgentAccessCount);
    }

    [Fact]
    public async Task AgentMethodBeforeInitialize_IsRejectedWithoutAccessingAgent()
    {
        var client = new FakeWujiClient(agent: new FakeBridgeAgentClient());
        var result = await RunHostAsync(
            client,
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("status-1", BridgeProtocol.AgentGetStatusMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        Assert.Equal("initialization_required", Error(result.Responses[1]).GetProperty("code").GetString());
        Assert.Equal(0, client.AgentAccessCount);
    }

    [Fact]
    public async Task ActivityMethodBeforeInitialize_IsRejectedWithoutAccessingActivity()
    {
        var client = new FakeWujiClient(activity: new FakeBridgeActivityClient());
        var result = await RunHostAsync(
            client,
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("overview-1", BridgeProtocol.ActivityGetOverviewMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        Assert.Equal("initialization_required", Error(result.Responses[1]).GetProperty("code").GetString());
        Assert.Equal(0, client.ActivityAccessCount);
    }

    [Fact]
    public async Task ActivityGetOverview_QueriesInParallelAndReturnsOnlySafeFields()
    {
        const string PrivatePath = "C:\\Users\\private\\apps\\Safe App.exe";
        const string PrivateProcess = "private-process.exe";
        const string PrivateWindowTitle = "Private document title";
        var releaseQueries = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allQueriesStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var activity = new FakeBridgeActivityClient
        {
            Summary = new DashboardSummary
            {
                DateUtc = new DateTime(2026, 7, 16, 0, 0, 0, DateTimeKind.Utc),
                TotalDurationSeconds = 3600,
                ActiveDurationSeconds = 3000,
                IdleDurationSeconds = 500,
                UnknownDurationSeconds = -100,
                SessionCount = 4
            },
            TopApps =
            [
                new AppUsageSummary
                {
                    ProcessName = PrivateProcess,
                    DisplayName = PrivatePath,
                    TotalDurationSeconds = 1800,
                    ActiveDurationSeconds = 1600,
                    IdleDurationSeconds = -150,
                    UnknownDurationSeconds = 50,
                    SessionCount = 2,
                    LastUsedAtUtc = new DateTime(2026, 7, 16, 8, 30, 0, DateTimeKind.Utc)
                }
            ],
            RecentSessions =
            [
                new AppSession
                {
                    Id = 987654,
                    ProcessName = PrivateProcess,
                    DisplayName = "安全应用",
                    WindowTitle = PrivateWindowTitle,
                    StartedAtUtc = new DateTime(2026, 7, 16, 8, 0, 0, DateTimeKind.Utc),
                    EndedAtUtc = new DateTime(2026, 7, 16, 8, 20, 0, DateTimeKind.Utc),
                    TotalDurationSeconds = 1200,
                    ActiveDurationSeconds = 1000,
                    IdleDurationSeconds = 150,
                    UnknownDurationSeconds = -50
                }
            ]
        };

        activity.SummaryHandler = cancellationToken => HoldAsync(activity.Summary, cancellationToken);
        activity.TopAppsHandler = cancellationToken => HoldAsync(activity.TopApps, cancellationToken);
        activity.RecentSessionsHandler = cancellationToken => HoldAsync(activity.RecentSessions, cancellationToken);

        var client = new FakeWujiClient(activity: activity);
        var runTask = RunHostAsync(
            client,
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("overview-1", BridgeProtocol.ActivityGetOverviewMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        await allQueriesStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseQueries.SetResult();
        var result = await runTask;

        var overview = Result(result.Responses[2]);
        Assert.Equal(3600, overview.GetProperty("summary").GetProperty("totalDurationSeconds").GetInt64());
        Assert.Equal(0, overview.GetProperty("summary").GetProperty("unknownDurationSeconds").GetInt64());
        Assert.Equal("Safe App.exe", overview.GetProperty("topApps")[0].GetProperty("displayName").GetString());
        Assert.Equal(0, overview.GetProperty("topApps")[0].GetProperty("idleDurationSeconds").GetInt64());
        Assert.Equal("安全应用", overview.GetProperty("recentSessions")[0].GetProperty("displayName").GetString());
        Assert.Equal("2026-07-16T08:00:00.0000000Z", overview.GetProperty("recentSessions")[0].GetProperty("startedAtUtc").GetString());
        Assert.Equal(0, overview.GetProperty("recentSessions")[0].GetProperty("unknownDurationSeconds").GetInt64());
        Assert.DoesNotContain(PrivatePath, result.RawOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PrivateProcess, result.RawOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PrivateWindowTitle, result.RawOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("987654", result.RawOutput, StringComparison.Ordinal);
        Assert.Equal(1, activity.SummaryCount);
        Assert.Equal(1, activity.TopAppsCount);
        Assert.Equal(1, activity.RecentSessionsCount);
        Assert.Equal(1, client.ActivityAccessCount);
        Assert.Equal(0, client.AgentAccessCount);

        async Task<T> HoldAsync<T>(T value, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref startedCount) == 3)
            {
                allQueriesStarted.SetResult();
            }

            await releaseQueries.Task.WaitAsync(cancellationToken);
            return value;
        }
    }

    [Fact]
    public async Task ActivityGetOverviewFailure_ReturnsSafeErrorWithoutInternalDetails()
    {
        const string Secret = "C:\\Users\\private\\activity.db Private window title";
        var activity = new FakeBridgeActivityClient
        {
            SummaryHandler = _ => Task.FromException<DashboardSummary>(new IOException(Secret))
        };
        var result = await RunHostAsync(
            new FakeWujiClient(activity: activity),
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("overview-1", BridgeProtocol.ActivityGetOverviewMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        var error = Error(result.Responses[2]);
        Assert.Equal("internal_error", error.GetProperty("code").GetString());
        Assert.Equal("Bridge 暂时无法完成请求。", error.GetProperty("message").GetString());
        Assert.DoesNotContain(Secret, result.RawOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, result.Log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("method=activity.getOverview", result.Log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentActualState.NotRunning, false, false, "not_running")]
    [InlineData(AgentActualState.Running, true, false, "running")]
    [InlineData(AgentActualState.Paused, true, false, "paused")]
    [InlineData(AgentActualState.Stale, true, true, "stale")]
    public async Task AgentGetStatus_MapsOnlySafeFields(
        AgentActualState actualState,
        bool isRunning,
        bool isStale,
        string expectedState)
    {
        var agent = new FakeBridgeAgentClient
        {
            CurrentStatus = new AgentStatusSnapshot
            {
                ActualState = actualState,
                IsRunning = isRunning,
                IsHealthy = !isStale,
                IsStale = isStale,
                ProcessText = "PID 12345 PRIVATE-MACHINE PrivateUser",
                RuntimeState = new RuntimeState
                {
                    ProcessId = 12345,
                    State = actualState,
                    LastHeartbeatUtc = new DateTime(2026, 7, 16, 8, 30, 0, DateTimeKind.Utc),
                    LastSampleUtc = new DateTime(2026, 7, 16, 8, 29, 0, DateTimeKind.Utc),
                    MachineName = "PRIVATE-MACHINE",
                    UserName = "PrivateUser"
                }
            }
        };
        var result = await RunHostAsync(
            new FakeWujiClient(agent: agent),
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("status-1", BridgeProtocol.AgentGetStatusMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        var status = Result(result.Responses[2]);
        Assert.Equal(expectedState, status.GetProperty("actualState").GetString());
        Assert.Equal(isRunning, status.GetProperty("isRunning").GetBoolean());
        Assert.Equal(!isStale, status.GetProperty("isHealthy").GetBoolean());
        Assert.Equal(isStale, status.GetProperty("isStale").GetBoolean());
        Assert.Equal("2026-07-16T08:30:00.0000000Z", status.GetProperty("lastHeartbeatUtc").GetString());
        Assert.DoesNotContain("PRIVATE-MACHINE", result.RawOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateUser", result.RawOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("12345", result.RawOutput, StringComparison.Ordinal);
        Assert.Equal(1, agent.StatusCount);
    }

    [Fact]
    public async Task AgentLifecycleMethods_CallClientUseCasesAndReturnCommandResults()
    {
        var agent = new FakeBridgeAgentClient
        {
            StopResult = new AgentStopResult
            {
                IsStopped = true,
                UsedKillFallback = true
            }
        };
        var result = await RunHostAsync(
            new FakeWujiClient(agent: agent),
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("start-1", BridgeProtocol.AgentStartMethod),
            Request("pause-1", BridgeProtocol.AgentPauseMethod),
            Request("resume-1", BridgeProtocol.AgentResumeMethod),
            Request("stop-1", BridgeProtocol.AgentStopMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        Assert.Equal("running", Result(result.Responses[2]).GetProperty("actualState").GetString());
        Assert.Equal("paused", Result(result.Responses[3]).GetProperty("actualState").GetString());
        Assert.Equal("running", Result(result.Responses[4]).GetProperty("actualState").GetString());
        Assert.Equal("stopped", Result(result.Responses[5]).GetProperty("actualState").GetString());
        Assert.True(Result(result.Responses[5]).GetProperty("usedFallback").GetBoolean());
        Assert.Equal(1, agent.StartCount);
        Assert.Equal(1, agent.PauseCount);
        Assert.Equal(1, agent.ResumeCount);
        Assert.Equal(1, agent.StopCount);
    }

    [Fact]
    public async Task DuplicateSideEffectRequest_ReturnsCachedResponseWithoutStartingAgain()
    {
        var agent = new FakeBridgeAgentClient();
        var result = await RunHostAsync(
            new FakeWujiClient(agent: agent),
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("start-1", BridgeProtocol.AgentStartMethod),
            Request("start-1", BridgeProtocol.AgentStartMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        Assert.Equal(result.Responses[2].GetRawText(), result.Responses[3].GetRawText());
        Assert.Equal(1, agent.StartCount);
    }

    [Fact]
    public async Task SideEffectTimeout_IsNotRetryableAndDoesNotLeakInternalDetails()
    {
        const string Secret = "C:\\Users\\private\\agent_control.json";
        var agent = new FakeBridgeAgentClient
        {
            PauseHandler = async cancellationToken =>
            {
                _ = Secret;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException(Secret);
            }
        };
        var result = await RunHostAsync(
            new FakeWujiClient(agent: agent),
            new BridgeHostOptions
            {
                AgentPauseTimeout = TimeSpan.FromMilliseconds(25)
            },
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("pause-1", BridgeProtocol.AgentPauseMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        var error = Error(result.Responses[2]);
        Assert.Equal("request_timeout", error.GetProperty("code").GetString());
        Assert.False(error.GetProperty("data").GetProperty("retryable").GetBoolean());
        Assert.DoesNotContain(Secret, result.RawOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, result.Log, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, agent.PauseCount);
    }

    [Fact]
    public async Task IpcTimeoutCommandResult_IsSanitizedAndRequiresStatusReconciliation()
    {
        const string Secret = "C:\\Users\\private\\pipe-name";
        var agent = new FakeBridgeAgentClient
        {
            PauseHandler = _ => Task.FromResult(new AgentCommandResult
            {
                Accepted = true,
                Completed = false,
                ActualState = AgentActualState.Running,
                Message = Secret,
                ErrorCode = "IpcTimeout"
            })
        };
        var result = await RunHostAsync(
            new FakeWujiClient(agent: agent),
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("pause-1", BridgeProtocol.AgentPauseMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        var command = Result(result.Responses[2]);
        Assert.Equal("ipc_timeout", command.GetProperty("errorCode").GetString());
        Assert.False(command.GetProperty("completed").GetBoolean());
        Assert.DoesNotContain(Secret, result.RawOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShutdownWhileAgentIsRunning_DisposesClientWithoutStoppingAgent()
    {
        var agent = new FakeBridgeAgentClient
        {
            CurrentStatus = new AgentStatusSnapshot
            {
                ActualState = AgentActualState.Running,
                IsRunning = true
            }
        };
        var client = new FakeWujiClient(agent: agent);

        await RunHostAsync(
            client,
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(0, agent.StopCount);
    }

    [Fact]
    public async Task EndOfInputWhileAgentIsRunning_DisposesClientWithoutStoppingAgent()
    {
        var agent = new FakeBridgeAgentClient
        {
            CurrentStatus = new AgentStatusSnapshot
            {
                ActualState = AgentActualState.Running,
                IsRunning = true
            }
        };
        var client = new FakeWujiClient(agent: agent);
        await using var input = Input(
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod));
        await using var output = new MemoryStream();
        var host = new BridgeHost(client, new BridgeHostOptions(), TextWriter.Null);

        await host.RunAsync(input, output);

        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(0, agent.StopCount);
    }

    [Fact]
    public async Task AgentRequestFailure_ReturnsSafeErrorAndNeverStopsAgentDuringDisposal()
    {
        const string Secret = "C:\\Users\\private\\runtime_state.json";
        var agent = new FakeBridgeAgentClient
        {
            StatusHandler = _ => throw new IOException(Secret)
        };
        var client = new FakeWujiClient(agent: agent);
        var result = await RunHostAsync(
            client,
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("status-1", BridgeProtocol.AgentGetStatusMethod));

        Assert.Equal("internal_error", Error(result.Responses[2]).GetProperty("code").GetString());
        Assert.DoesNotContain(Secret, result.RawOutput, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Secret, result.Log, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, client.DisposeCount);
        Assert.Equal(0, agent.StopCount);
    }

    [Fact]
    public async Task InternalFailure_DoesNotLeakExceptionOrPathsToStdoutOrStderr()
    {
        const string Secret = "C:\\Users\\private\\runtime.json";
        var client = new FakeWujiClient(_ => throw new InvalidOperationException(Secret));
        var result = await RunHostAsync(
            client,
            Request("hello-1", BridgeProtocol.HelloMethod),
            Request("init-1", BridgeProtocol.InitializeMethod),
            Request("shutdown-1", BridgeProtocol.ShutdownMethod));

        Assert.Equal("internal_error", Error(result.Responses[1]).GetProperty("code").GetString());
        Assert.DoesNotContain(Secret, result.RawOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, result.Log, StringComparison.Ordinal);
        Assert.Contains("correlationId=corr-init-1", result.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BridgeProgram_RejectsProductionBeforeCreatingClient()
    {
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        using var error = new StringWriter();

        var exitCode = await BridgeProgram.RunAsync(
            ["--channel", "prod"],
            input,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("development channel", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    private static Task<HostRunResult> RunHostAsync(FakeWujiClient client, params string[] requests)
        => RunHostAsync(client, new BridgeHostOptions(), requests);

    private static async Task<HostRunResult> RunHostAsync(
        FakeWujiClient client,
        BridgeHostOptions options,
        params string[] requests)
    {
        await using var input = Input(requests);
        await using var output = new MemoryStream();
        using var log = new StringWriter();
        var host = new BridgeHost(client, options, log);

        await host.RunAsync(input, output);

        var rawOutput = Encoding.UTF8.GetString(output.ToArray());
        var responses = rawOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        return new HostRunResult(responses, rawOutput, log.ToString());
    }

    private static MemoryStream Input(params string[] requests)
    {
        var content = string.Join('\n', requests) + (requests.Length == 0 ? string.Empty : "\n");
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    private static string Request(string id, string method, string apiVersion = "1.0")
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = new { },
            meta = new
            {
                apiVersion,
                correlationId = $"corr-{id}"
            }
        });
    }

    private static string RequestWithUnknownProperty(string id)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = BridgeProtocol.InitializeMethod,
            @params = new { },
            meta = new
            {
                apiVersion = "1.0",
                correlationId = $"corr-{id}"
            },
            executablePath = "forbidden"
        });
    }

    private static JsonElement Result(JsonElement response)
        => response.GetProperty("result");

    private static JsonElement Error(JsonElement response)
        => response.GetProperty("error");

    private sealed record HostRunResult(
        IReadOnlyList<JsonElement> Responses,
        string RawOutput,
        string Log);

    private sealed class FakeWujiClient : IWujiClient
    {
        private readonly Func<CancellationToken, Task> _initialize;
        private readonly IAgentClient? _agent;
        private readonly IActivityClient? _activity;

        public FakeWujiClient(
            Func<CancellationToken, Task>? initialize = null,
            IAgentClient? agent = null,
            IActivityClient? activity = null)
        {
            _initialize = initialize ?? (_ => Task.CompletedTask);
            _agent = agent;
            _activity = activity;
        }

        public int InitializeCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int AgentAccessCount { get; private set; }

        public int ActivityAccessCount { get; private set; }

        public IAgentClient Agent
        {
            get
            {
                AgentAccessCount++;
                return _agent
                    ?? throw new InvalidOperationException("Agent must not be accessed by this Bridge test.");
            }
        }

        public IActivityClient Activity
        {
            get
            {
                ActivityAccessCount++;
                return _activity
                    ?? throw new InvalidOperationException("Activity must not be accessed by this Bridge test.");
            }
        }

        public IDiagnosticsClient Diagnostics => throw new InvalidOperationException("Diagnostics is not part of stage 1.");

        public ISettingsClient Settings => throw new InvalidOperationException("Settings is not part of stage 1.");

        public IStartupClient Startup => throw new InvalidOperationException("Startup is not part of stage 1.");

        public WujiClientContext Context { get; } = new("dev", "WUJI Dev", IsDefaultChannel: false);

        public WujiClientPaths Paths => throw new InvalidOperationException("Paths must not cross the Bridge contract.");

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCount++;
            await _initialize(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
