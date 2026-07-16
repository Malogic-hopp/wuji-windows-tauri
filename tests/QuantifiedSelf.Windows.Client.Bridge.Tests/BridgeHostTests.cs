using System.Text;
using System.Text.Json;
using QuantifiedSelf.Windows.Client.Bridge.Generated;

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

        public FakeWujiClient(Func<CancellationToken, Task>? initialize = null)
        {
            _initialize = initialize ?? (_ => Task.CompletedTask);
        }

        public int InitializeCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int AgentAccessCount { get; private set; }

        public IAgentClient Agent
        {
            get
            {
                AgentAccessCount++;
                throw new InvalidOperationException("Agent must not be accessed by the stage 1 Bridge.");
            }
        }

        public IActivityClient Activity => throw new InvalidOperationException("Activity is not part of stage 1.");

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
