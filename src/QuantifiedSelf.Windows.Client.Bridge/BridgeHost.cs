using System.Text;
using System.Text.Json;
using QuantifiedSelf.Windows.Client;
using QuantifiedSelf.Windows.Client.Bridge.Generated;

namespace QuantifiedSelf.Windows.Client.Bridge;

internal sealed class BridgeHost
{
    private static readonly HashSet<string> RequestProperties =
    [
        "jsonrpc",
        "id",
        "method",
        "params",
        "meta"
    ];

    private static readonly HashSet<string> MetaProperties =
    [
        "apiVersion",
        "correlationId"
    ];

    private readonly IWujiClient _client;
    private readonly BridgeHostOptions _options;
    private readonly TextWriter _log;
    private readonly Dictionary<string, CachedResponse> _completedRequests = new(StringComparer.Ordinal);
    private bool _handshakeCompleted;
    private bool _initialized;
    private bool _runStarted;

    public BridgeHost(IWujiClient client, BridgeHostOptions options, TextWriter log)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _options.Validate();
    }

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (_runStarted)
        {
            throw new InvalidOperationException("Bridge host can only run once.");
        }
        _runStarted = true;

        var reader = new BoundedNdjsonReader(input, _options.MaxPayloadBytes);
        await using var writer = new StreamWriter(
            output,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NdjsonReadResult readResult;
                try
                {
                    readResult = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (readResult.IsEndOfStream)
                {
                    break;
                }

                ResponseOutcome outcome;
                if (readResult.IsPayloadTooLarge)
                {
                    outcome = Serialize(BridgeProtocol.Failure(
                        string.Empty,
                        "unknown",
                        "payload_too_large",
                        "请求内容过大。",
                        BridgeErrorKind.Validation,
                        retryable: false));
                }
                else if (readResult.HasInvalidEncoding)
                {
                    outcome = Serialize(BridgeProtocol.Failure(
                        string.Empty,
                        "unknown",
                        "parse_error",
                        "无法解析请求。",
                        BridgeErrorKind.Validation,
                        retryable: false));
                }
                else
                {
                    outcome = await ProcessLineAsync(readResult.Line!, cancellationToken).ConfigureAwait(false);
                }

                await writer.WriteLineAsync(outcome.SerializedResponse).ConfigureAwait(false);
                if (outcome.ShouldShutdown)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<ResponseOutcome> ProcessLineAsync(string line, CancellationToken hostCancellationToken)
    {
        BridgeRequestEnvelope request;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!IsValidEnvelope(document.RootElement))
            {
                return Serialize(BridgeProtocol.Failure(
                    TryReadString(document.RootElement, "id"),
                    TryReadCorrelationId(document.RootElement),
                    "invalid_request",
                    "请求格式无效。",
                    BridgeErrorKind.Validation,
                    retryable: false));
            }

            request = JsonSerializer.Deserialize<BridgeRequestEnvelope>(
                document.RootElement.GetRawText(),
                BridgeProtocol.SerializerOptions)!;
        }
        catch (JsonException)
        {
            return Serialize(BridgeProtocol.Failure(
                string.Empty,
                "unknown",
                "parse_error",
                "无法解析请求。",
                BridgeErrorKind.Validation,
                retryable: false));
        }

        if (_completedRequests.TryGetValue(request.Id, out var cached)
            && DateTimeOffset.UtcNow - cached.CompletedAtUtc <= _options.CompletedRequestTtl)
        {
            if (string.Equals(cached.Method, request.Method, StringComparison.Ordinal))
            {
                return new ResponseOutcome(cached.SerializedResponse, ShouldShutdown: false);
            }

            return Serialize(BridgeProtocol.Failure(
                request.Id,
                request.Meta.CorrelationId,
                "invalid_request",
                "请求标识已被其他方法使用。",
                BridgeErrorKind.Validation,
                retryable: false));
        }
        _completedRequests.Remove(request.Id);

        if (!BridgeProtocol.IsSupportedApiVersion(request.Meta.ApiVersion))
        {
            return SerializeAndCache(request, BridgeProtocol.Failure(
                request.Id,
                request.Meta.CorrelationId,
                "unsupported_api_version",
                "当前 Bridge 不支持该 API 版本。",
                BridgeErrorKind.Unsupported,
                retryable: false));
        }

        if (!string.Equals(request.Method, BridgeProtocol.HelloMethod, StringComparison.Ordinal)
            && !_handshakeCompleted)
        {
            return SerializeAndCache(request, BridgeProtocol.Failure(
                request.Id,
                request.Meta.CorrelationId,
                "handshake_required",
                "请先完成 Bridge 握手。",
                BridgeErrorKind.Conflict,
                retryable: true));
        }

        if (BridgeProtocol.RequiresInitialization(request.Method) && !_initialized)
        {
            return SerializeAndCache(request, BridgeProtocol.Failure(
                request.Id,
                request.Meta.CorrelationId,
                "initialization_required",
                "请先初始化 WUJI Client。",
                BridgeErrorKind.Conflict,
                retryable: true));
        }

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken);
        requestCancellation.CancelAfter(_options.GetRequestTimeout(request.Method));

        try
        {
            return request.Method switch
            {
                BridgeProtocol.HelloMethod => HandleHello(request),
                BridgeProtocol.InitializeMethod => await HandleInitializeAsync(
                    request,
                    requestCancellation.Token).ConfigureAwait(false),
                BridgeProtocol.AgentGetStatusMethod => await HandleAgentGetStatusAsync(
                    request,
                    requestCancellation.Token).ConfigureAwait(false),
                BridgeProtocol.AgentStartMethod => await HandleAgentStartAsync(
                    request,
                    requestCancellation.Token).ConfigureAwait(false),
                BridgeProtocol.AgentPauseMethod => await HandleAgentPauseAsync(
                    request,
                    requestCancellation.Token).ConfigureAwait(false),
                BridgeProtocol.AgentResumeMethod => await HandleAgentResumeAsync(
                    request,
                    requestCancellation.Token).ConfigureAwait(false),
                BridgeProtocol.AgentStopMethod => await HandleAgentStopAsync(
                    request,
                    requestCancellation.Token).ConfigureAwait(false),
                BridgeProtocol.ActivityGetOverviewMethod => await HandleActivityGetOverviewAsync(
                    request,
                    requestCancellation.Token).ConfigureAwait(false),
                BridgeProtocol.ShutdownMethod => HandleShutdown(request),
                _ => SerializeAndCache(request, BridgeProtocol.Failure(
                    request.Id,
                    request.Meta.CorrelationId,
                    "method_not_found",
                    "请求的方法不可用。",
                    BridgeErrorKind.Unsupported,
                    retryable: false))
            };
        }
        catch (OperationCanceledException) when (hostCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return SerializeAndCache(request, BridgeProtocol.Failure(
                request.Id,
                request.Meta.CorrelationId,
                "request_timeout",
                BridgeProtocol.IsSideEffectMethod(request.Method)
                    ? "请求超时；命令可能已经执行，请先查询 Agent 状态。"
                    : "请求超时，请稍后重试。",
                BridgeErrorKind.Transient,
                retryable: !BridgeProtocol.IsSideEffectMethod(request.Method)));
        }
        catch
        {
            await _log.WriteLineAsync(
                $"Bridge request failed. method={request.Method} correlationId={request.Meta.CorrelationId}")
                .ConfigureAwait(false);
            return SerializeAndCache(request, BridgeProtocol.Failure(
                request.Id,
                request.Meta.CorrelationId,
                "internal_error",
                "Bridge 暂时无法完成请求。",
                BridgeErrorKind.Internal,
                retryable: true));
        }
    }

    private ResponseOutcome HandleHello(BridgeRequestEnvelope request)
    {
        _handshakeCompleted = true;
        return SerializeAndCache(request, BridgeProtocol.Success(
            request.Id,
            new BridgeHelloResult
            {
                ApiVersion = BridgeProtocol.ApiVersion,
                BridgeVersion = typeof(BridgeHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                Capabilities = BridgeProtocol.Capabilities
            }));
    }

    private async Task<ResponseOutcome> HandleInitializeAsync(
        BridgeRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await _client.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }

        return SerializeAndCache(request, BridgeProtocol.Success(
            request.Id,
            new ClientInitializeResult
            {
                ApiVersion = BridgeProtocol.ApiVersion,
                ChannelName = _client.Context.ChannelName,
                ProductDisplayName = _client.Context.ProductDisplayName,
                IsDefaultChannel = _client.Context.IsDefaultChannel,
                Capabilities = BridgeProtocol.Capabilities
            }));
    }

    private ResponseOutcome HandleShutdown(BridgeRequestEnvelope request)
    {
        var outcome = SerializeAndCache(request, BridgeProtocol.Success(
            request.Id,
            new BridgeShutdownResult
            {
                Accepted = true
            }));
        return outcome with { ShouldShutdown = true };
    }

    private async Task<ResponseOutcome> HandleAgentGetStatusAsync(
        BridgeRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        var status = await _client.Agent.Status.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return SerializeAndCache(request, BridgeProtocol.Success(
            request.Id,
            BridgeAgentMapper.ToStatus(status)));
    }

    private async Task<ResponseOutcome> HandleAgentStartAsync(
        BridgeRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        var process = await _client.Agent.Process.StartAgentAsync(cancellationToken).ConfigureAwait(false);
        var status = await _client.Agent.Status.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return SerializeAndCache(request, BridgeProtocol.Success(
            request.Id,
            BridgeAgentMapper.ToStartResult(process, status)));
    }

    private async Task<ResponseOutcome> HandleAgentPauseAsync(
        BridgeRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        var result = await _client.Agent.Control.RequestPauseAsync(cancellationToken).ConfigureAwait(false);
        return SerializeAndCache(request, BridgeProtocol.Success(
            request.Id,
            BridgeAgentMapper.ToCommandResult(result)));
    }

    private async Task<ResponseOutcome> HandleAgentResumeAsync(
        BridgeRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        var result = await _client.Agent.Control.RequestResumeAsync(cancellationToken).ConfigureAwait(false);
        return SerializeAndCache(request, BridgeProtocol.Success(
            request.Id,
            BridgeAgentMapper.ToCommandResult(result)));
    }

    private async Task<ResponseOutcome> HandleAgentStopAsync(
        BridgeRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        var result = await _client.Agent.Process.StopAgentAsync(cancellationToken).ConfigureAwait(false);
        return SerializeAndCache(request, BridgeProtocol.Success(
            request.Id,
            BridgeAgentMapper.ToStopResult(result)));
    }

    private async Task<ResponseOutcome> HandleActivityGetOverviewAsync(
        BridgeRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        var overview = _client.Activity.Overview;
        var summaryTask = overview.GetDashboardSummaryAsync(cancellationToken);
        var topAppsTask = overview.GetTopAppsTodayAsync(limit: 5, cancellationToken: cancellationToken);
        var recentSessionsTask = overview.GetRecentSessionsAsync(limit: 5, cancellationToken: cancellationToken);

        await Task.WhenAll(summaryTask, topAppsTask, recentSessionsTask).ConfigureAwait(false);

        return SerializeAndCache(request, BridgeProtocol.Success(
            request.Id,
            BridgeActivityMapper.ToOverview(
                await summaryTask.ConfigureAwait(false),
                await topAppsTask.ConfigureAwait(false),
                await recentSessionsTask.ConfigureAwait(false))));
    }

    private ResponseOutcome SerializeAndCache(
        BridgeRequestEnvelope request,
        BridgeResponseEnvelope response)
    {
        var outcome = Serialize(response);
        if (_completedRequests.Count >= 128)
        {
            var oldestKey = _completedRequests.Keys.First();
            _completedRequests.Remove(oldestKey);
        }

        _completedRequests[request.Id] = new CachedResponse(
            request.Method,
            outcome.SerializedResponse,
            DateTimeOffset.UtcNow);
        return outcome;
    }

    private static ResponseOutcome Serialize(BridgeResponseEnvelope response)
    {
        return new ResponseOutcome(
            JsonSerializer.Serialize(response, BridgeProtocol.SerializerOptions),
            ShouldShutdown: false);
    }

    private static bool IsValidEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (!RequestProperties.Contains(property.Name))
            {
                return false;
            }
        }

        if (!TryGetNonEmptyBoundedString(root, "jsonrpc", 8, out var jsonrpc)
            || !string.Equals(jsonrpc, BridgeProtocol.JsonRpcVersion, StringComparison.Ordinal)
            || !TryGetNonEmptyBoundedString(root, "id", 128, out _)
            || !TryGetNonEmptyBoundedString(root, "method", 128, out _)
            || !root.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || parameters.EnumerateObject().Any()
            || !root.TryGetProperty("meta", out var meta)
            || meta.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in meta.EnumerateObject())
        {
            if (!MetaProperties.Contains(property.Name))
            {
                return false;
            }
        }

        return TryGetNonEmptyBoundedString(meta, "apiVersion", 16, out _)
            && TryGetSafeIdentifier(meta, "correlationId", 128, out _)
            && TryGetSafeIdentifier(root, "id", 128, out _);
    }

    private static bool TryGetNonEmptyBoundedString(
        JsonElement element,
        string propertyName,
        int maxLength,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return value.Length is > 0 && value.Length <= maxLength;
    }

    private static bool TryGetSafeIdentifier(
        JsonElement element,
        string propertyName,
        int maxLength,
        out string value)
    {
        if (!TryGetNonEmptyBoundedString(element, propertyName, maxLength, out value))
        {
            return false;
        }

        return value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or ':');
    }

    private static string TryReadString(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
    }

    private static string TryReadCorrelationId(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && TryGetSafeIdentifier(meta, "correlationId", 128, out var correlationId))
        {
            return correlationId;
        }

        return "unknown";
    }

    private sealed record CachedResponse(
        string Method,
        string SerializedResponse,
        DateTimeOffset CompletedAtUtc);

    private sealed record ResponseOutcome(string SerializedResponse, bool ShouldShutdown);
}
