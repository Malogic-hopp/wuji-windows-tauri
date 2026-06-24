using QuantifiedSelf.Windows.Agent.Events;
using QuantifiedSelf.Windows.Agent.Services;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Events;
using QuantifiedSelf.Windows.Core.Display;
using QuantifiedSelf.Windows.Core.Models;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Infrastructure.Database;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using QuantifiedSelf.Windows.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace QuantifiedSelf.Windows.Agent.State;

public sealed class AgentStateMachine
{
    private static readonly string[] LifecyclePayloadKeys = ["processId", "version", "actualState"];
    private static readonly string[] SessionStartedPayloadKeys = ["sessionId", "processName", "startedAtUtc", "processId", "actualState"];
    private static readonly string[] SessionClosedPayloadKeys = ["sessionId", "processName", "closeReason", "totalDurationSeconds", "activeDurationSeconds", "idleDurationSeconds", "unknownDurationSeconds"];
    private static readonly string[] CommandPayloadKeys = ["commandSource", "command", "desiredState", "requestedBy", "requestedAtUtc", "waitForCompletion", "timeoutMilliseconds", "actualState"];
    private static readonly string[] CommandFailurePayloadKeys = ["commandSource", "command", "desiredState", "requestedBy", "requestedAtUtc", "waitForCompletion", "timeoutMilliseconds", "actualState", "errorCode", "exceptionType", "shortMessage"];
    private static readonly string[] CommandInvalidJsonPayloadKeys = ["commandSource", "quarantined", "fileKind"];
    private static readonly string[] PrivacyFilteredPayloadKeys = ["ruleType", "processName", "privacyReason", "processId", "actualState"];
    private static readonly string[] CaptureFailedPayloadKeys = ["errorCode", "exceptionType", "shortMessage"];

    private readonly WindowsAgentPaths _paths;
    private readonly RuntimeStateStore _runtimeStateStore;
    private readonly AgentHealthStateStore _healthStateStore;
    private readonly AgentControlFileStore _controlFileStore;
    private readonly WindowsAgentOptionsStore _optionsStore;
    private readonly SqliteDatabaseInitializer _databaseInitializer;
    private readonly ForegroundSampleRepository _foregroundSampleRepository;
    private readonly SessionAggregator _sessionAggregator;
    private readonly ForegroundSamplePrivacyFilter _privacyFilter;
    private readonly ConfiguredForegroundSampleProvider _foregroundSampleProvider;
    private readonly AgentEventWriter? _eventWriter;
    private readonly AgentEventRateLimiter _eventRateLimiter = new();
    private readonly AgentOptionsValidator _optionsValidator;
    private readonly ILogger<AgentStateMachine> _logger;

    private WindowsAgentOptions _options = new();
    private DateTime _lastPersistedHeartbeatUtc = DateTime.MinValue;
    private DateTime _lastSampleAtUtc = DateTime.MinValue;
    private string? _lastProcessedRequestId;
    private long? _currentSessionId;
    private int _sampleCountSinceStart;
    private int _databaseWriteErrorCount;
    private int _captureErrorCount;

    public AgentStateMachine(
        WindowsAgentPaths paths,
        RuntimeStateStore runtimeStateStore,
        AgentHealthStateStore healthStateStore,
        AgentControlFileStore controlFileStore,
        WindowsAgentOptionsStore optionsStore,
        SqliteDatabaseInitializer databaseInitializer,
        ForegroundSampleRepository foregroundSampleRepository,
        SessionAggregator sessionAggregator,
        ForegroundSamplePrivacyFilter privacyFilter,
        ConfiguredForegroundSampleProvider foregroundSampleProvider,
        AgentEventWriter? eventWriter,
        ILogger<AgentStateMachine> logger)
        : this(
            paths,
            runtimeStateStore,
            healthStateStore,
            controlFileStore,
            optionsStore,
            databaseInitializer,
            foregroundSampleRepository,
            sessionAggregator,
            privacyFilter,
            foregroundSampleProvider,
            eventWriter,
            new AgentOptionsValidator(),
            logger)
    {
    }

    public AgentStateMachine(
        WindowsAgentPaths paths,
        RuntimeStateStore runtimeStateStore,
        AgentHealthStateStore healthStateStore,
        AgentControlFileStore controlFileStore,
        WindowsAgentOptionsStore optionsStore,
        SqliteDatabaseInitializer databaseInitializer,
        ForegroundSampleRepository foregroundSampleRepository,
        SessionAggregator sessionAggregator,
        ForegroundSamplePrivacyFilter privacyFilter,
        ConfiguredForegroundSampleProvider foregroundSampleProvider,
        AgentEventWriter? eventWriter,
        AgentOptionsValidator optionsValidator,
        ILogger<AgentStateMachine> logger)
    {
        _paths = paths;
        _runtimeStateStore = runtimeStateStore;
        _healthStateStore = healthStateStore;
        _controlFileStore = controlFileStore;
        _optionsStore = optionsStore;
        _databaseInitializer = databaseInitializer;
        _foregroundSampleRepository = foregroundSampleRepository;
        _sessionAggregator = sessionAggregator;
        _privacyFilter = privacyFilter;
        _foregroundSampleProvider = foregroundSampleProvider;
        _eventWriter = eventWriter;
        _optionsValidator = optionsValidator;
        _logger = logger;
    }

    public AgentStateMachine(
        WindowsAgentPaths paths,
        RuntimeStateStore runtimeStateStore,
        AgentHealthStateStore healthStateStore,
        AgentControlFileStore controlFileStore,
        WindowsAgentOptionsStore optionsStore,
        SqliteDatabaseInitializer databaseInitializer,
        ForegroundSampleRepository foregroundSampleRepository,
        SessionAggregator sessionAggregator,
        ForegroundSamplePrivacyFilter privacyFilter,
        ConfiguredForegroundSampleProvider foregroundSampleProvider,
        ILogger<AgentStateMachine> logger)
        : this(
            paths,
            runtimeStateStore,
            healthStateStore,
            controlFileStore,
            optionsStore,
            databaseInitializer,
            foregroundSampleRepository,
            sessionAggregator,
            privacyFilter,
            foregroundSampleProvider,
            null,
            new AgentOptionsValidator(),
            logger)
    {
    }

    public AgentActualState ActualState { get; private set; } = AgentActualState.Starting;

    public DateTime StartedAtUtc { get; private set; } = DateTime.UtcNow;

    public DateTime LastHeartbeatUtc { get; private set; } = DateTime.UtcNow;

    public DateTime? LastSampleUtc { get; private set; }

    public int ProcessId => Environment.ProcessId;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _paths.EnsureDirectories();
        await _databaseInitializer.InitializeAsync(cancellationToken);
        _options = await _optionsStore.ReadAsync(_paths.AgentOptionsPath, cancellationToken) ?? new WindowsAgentOptions();
        var startupValidation = _optionsValidator.Validate(_options);
        if (!startupValidation.IsValid)
        {
            _logger.LogWarning(
                "Agent options validation failed on startup, falling back to defaults: {Errors}",
                string.Join("; ", startupValidation.Issues.Select(issue => issue.SafeText)));
            _options = new WindowsAgentOptions();
        }
        else
        {
            _options = startupValidation.NormalizedOptions;
        }

        StartedAtUtc = DateTime.UtcNow;
        ActualState = AgentActualState.Starting;
        LastHeartbeatUtc = StartedAtUtc;
        LastSampleUtc = null;
        _lastSampleAtUtc = StartedAtUtc;
        _currentSessionId = null;
        _sampleCountSinceStart = 0;
        _databaseWriteErrorCount = 0;
        _captureErrorCount = 0;

        await PersistAsync("Agent starting", cancellationToken);
        var startupSessionResult = await CloseOpenSessionSafelyAsync("AgentStarted", "SessionWriteFailed", cancellationToken);
        if (startupSessionResult is not null)
        {
            await HandleSessionAggregationResultAsync(startupSessionResult, cancellationToken);
        }
        ActualState = AgentActualState.Running;
        await PersistAsync("Agent initialized", cancellationToken);
        await WriteLifecycleEventAsync(
            AgentEventType.AgentStarted,
            AgentEventLevel.Info,
            "Agent 已启动",
            cancellationToken,
            payload: new Dictionary<string, object?>
            {
                ["processId"] = ProcessId,
                ["version"] = CreateRuntimeSnapshot().Version,
                ["actualState"] = ActualState.ToString()
            },
            allowedPayloadKeys: LifecyclePayloadKeys);
    }

    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var commandRead = await _controlFileStore.ReadForAgentAsync(_paths.AgentControlPath, cancellationToken);
        if (commandRead.WasMalformed)
        {
            _logger.LogWarning(
                "控制文件格式错误：errorCode=MalformedControlFile，path={ControlPath}，message={ErrorMessage}",
                _paths.AgentControlPath,
                commandRead.ErrorMessage);
            await WriteCommandInvalidJsonEventAsync(cancellationToken);
            await PersistAsync(
                $"Malformed control file: {commandRead.ErrorMessage}",
                cancellationToken,
                "MalformedControlFile");
        }

        var command = commandRead.Command;
        if (command is not null && command.RequestId != _lastProcessedRequestId)
        {
            _lastProcessedRequestId = command.RequestId;

            try
            {
                await WriteCommandDetectedEventAsync(command, cancellationToken);
                await ProcessCommandAsync(command, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "处理控制命令失败：path={ControlPath}", _paths.AgentControlPath);
                await WriteCommandFailedEventAsync(
                    command,
                    "CommandProcessingFailed",
                    "命令处理失败",
                    cancellationToken,
                    ex);
            }
            finally
            {
                try
                {
                    File.Delete(_paths.AgentControlPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "删除控制文件失败：path={ControlPath}", _paths.AgentControlPath);
                }
            }
        }

        var now = DateTime.UtcNow;
        var heartbeatDue = now - _lastPersistedHeartbeatUtc >= TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatIntervalSeconds));
        var sampleDue = now - _lastSampleAtUtc >= TimeSpan.FromSeconds(Math.Max(1, _options.SamplingIntervalSeconds));

        if (ActualState == AgentActualState.Running && sampleDue)
        {
            ForegroundSample sample = null!;
            try
            {
                sample = NormalizeActivityState(_foregroundSampleProvider.Capture(_options), _options);
            }
            catch (Exception ex)
            {
                _captureErrorCount++;
                await WriteCaptureFailedEventAsync(ex, "ForegroundWindowUnavailable", "Foreground window capture failed", cancellationToken);
                await PersistAsync($"Foreground capture failed: {GetShortExceptionMessage(ex)}", cancellationToken, "ForegroundWindowUnavailable");
                LogCaptureFailed("ForegroundWindowUnavailable", ex);
                return true;
            }

            var privacyDecision = _privacyFilter.Apply(sample, _options);

            if (!privacyDecision.ShouldWriteSample)
            {
                if (privacyDecision.ShouldCloseOpenSession)
                {
                    var closedSessionResult = await CloseOpenSessionSafelyAsync("PrivacyExcluded", "SessionWriteFailed", cancellationToken);
                    if (closedSessionResult is not null)
                    {
                        await HandleSessionAggregationResultAsync(closedSessionResult, cancellationToken);
                    }
                }

                _lastSampleAtUtc = now;
                LogPrivacyFiltered(privacyDecision);
                await WritePrivacyFilteredEventAsync(sample, privacyDecision, cancellationToken);
                await PersistAsync(
                    $"Sample excluded: {GetPrivacyPersistMessage(privacyDecision)}",
                    cancellationToken);
                return true;
            }

            var filteredSample = privacyDecision.Sample ?? sample;
            try
            {
                await _foregroundSampleRepository.InsertAsync(filteredSample, cancellationToken);
            }
            catch (Exception ex)
            {
                _databaseWriteErrorCount++;
                await WriteCaptureFailedEventAsync(ex, "SampleWriteFailed", "Sample write failed", cancellationToken);
                await PersistAsync($"Sample write failed: {GetShortExceptionMessage(ex)}", cancellationToken, "SampleWriteFailed");
                LogCaptureFailed("SampleWriteFailed", ex);
                return true;
            }

            try
            {
                var sessionAggregationResult = await _sessionAggregator.HandleSampleAsync(filteredSample, _options.SamplingIntervalSeconds, cancellationToken);
                await HandleSessionAggregationResultAsync(sessionAggregationResult, cancellationToken);
            }
            catch (Exception ex)
            {
                _databaseWriteErrorCount++;
                await WriteCaptureFailedEventAsync(ex, "SessionAggregationFailed", "Session aggregation failed", cancellationToken);
                await PersistAsync($"Session aggregation failed: {GetShortExceptionMessage(ex)}", cancellationToken, "SessionAggregationFailed");
                LogCaptureFailed("SessionAggregationFailed", ex);
                return true;
            }

            LastSampleUtc = filteredSample.SampleTimeUtc;
            _lastSampleAtUtc = filteredSample.SampleTimeUtc;
            _sampleCountSinceStart++;
            await PersistAsync("Sample captured", cancellationToken);
            LogSampleCaptured(filteredSample);
        }

        if (ActualState == AgentActualState.Paused)
        {
            if (heartbeatDue)
            {
                await PersistAsync("Paused heartbeat", cancellationToken);
            }

            return true;
        }

        if (ActualState == AgentActualState.Stopped)
        {
            await PersistAsync("Agent stopped", cancellationToken);
            return false;
        }

        if (heartbeatDue)
        {
            await PersistAsync("Heartbeat", cancellationToken);
        }

        return true;
    }

    public async Task<AgentCommandResult> ProcessCommandAsync(
        AgentControlCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var result = new AgentCommandResult
        {
            RequestId = command.RequestId,
            Accepted = true,
            Completed = false,
            ActualState = ActualState
        };

        switch (command.Command)
        {
            case AgentCommandType.Pause:
                await WriteCommandAcceptedEventAsync(command, cancellationToken);
                await TransitionToPausedAsync("Collection paused", cancellationToken);
                result.Completed = true;
                result.ActualState = ActualState;
                result.Message = "Collection paused";
                break;

            case AgentCommandType.Resume:
                await WriteCommandAcceptedEventAsync(command, cancellationToken);
                await TransitionToRunningAsync("Collection resumed", cancellationToken);
                result.Completed = true;
                result.ActualState = ActualState;
                result.Message = "Collection resumed";
                break;

            case AgentCommandType.Stop:
                await WriteCommandAcceptedEventAsync(command, cancellationToken);
                await TransitionToStoppedAsync("Agent stopping", cancellationToken);
                result.Completed = true;
                result.ActualState = ActualState;
                result.Message = "Agent stopping";
                break;

            case AgentCommandType.GetStatus:
                await WriteCommandAcceptedEventAsync(command, cancellationToken);
                await PersistAsync("Status requested", cancellationToken);
                result.Completed = true;
                result.ActualState = ActualState;
                result.Message = "Status returned";
                break;

            case AgentCommandType.ReloadConfig:
                await WriteCommandAcceptedEventAsync(command, cancellationToken);

                WindowsAgentOptions? reloadedOptions = null;
                Exception? readException = null;
                try
                {
                    reloadedOptions = await _optionsStore.ReadAsync(_paths.AgentOptionsPath, cancellationToken);
                }
                catch (Exception ex)
                {
                    readException = ex;
                }

                if (readException is not null || reloadedOptions is null)
                {
                    const string readErrorCode = "ReloadConfigReadFailed";
                    var readSafeMessage = "Failed to read or parse agent options configuration.";
                    if (readException is not null)
                    {
                        var sanitizedReadMessage = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(readException, 160);
                        if (!string.IsNullOrWhiteSpace(sanitizedReadMessage))
                        {
                            readSafeMessage = $"{readSafeMessage} {sanitizedReadMessage}";
                        }
                    }

                    await WriteCommandFailedEventAsync(command, readErrorCode, readSafeMessage, cancellationToken, readException);
                    result.Accepted = true;
                    result.Completed = false;
                    result.ErrorCode = readErrorCode;
                    result.Message = readSafeMessage;
                    return result;
                }

                var validationResult = _optionsValidator.Validate(reloadedOptions);
                if (!validationResult.IsValid)
                {
                    const string validationErrorCode = "ReloadConfigValidationFailed";
                    var validationSafeMessage = "Reloaded agent options configuration is invalid.";
                    var issueMessage = string.Join("; ", validationResult.Issues.Select(issue => issue.SafeText));
                    if (!string.IsNullOrWhiteSpace(issueMessage))
                    {
                        validationSafeMessage = $"{validationSafeMessage} {issueMessage}";
                    }

                    await WriteCommandFailedEventAsync(command, validationErrorCode, validationSafeMessage, cancellationToken);
                    result.Accepted = true;
                    result.Completed = false;
                    result.ErrorCode = validationErrorCode;
                    result.Message = validationSafeMessage;
                    return result;
                }

                _options = validationResult.NormalizedOptions;
                await PersistAsync("Config reloaded", cancellationToken);
                await WriteLifecycleEventAsync(
                    AgentEventType.ConfigReloaded,
                    AgentEventLevel.Info,
                    "Config reloaded",
                    cancellationToken,
                    payload: new Dictionary<string, object?>
                    {
                        ["actualState"] = ActualState.ToString(),
                        ["processId"] = ProcessId
                    });
                result.Completed = true;
                result.ActualState = ActualState;
                result.Message = "Config reloaded";
                break;

            case AgentCommandType.UpdateAppMetadata:
            case AgentCommandType.UpdatePrivacyRules:
            case AgentCommandType.PruneData:
            case AgentCommandType.ClearHistory:
                await WriteCommandAcceptedEventAsync(command, cancellationToken);
                await PersistAsync($"{command.Command} accepted", cancellationToken);
                result.Completed = true;
                result.ActualState = ActualState;
                result.Message = $"{command.Command} accepted";
                break;

            default:
                result.Accepted = false;
                result.Message = $"Unsupported command: {command.Command}";
                result.ErrorCode = "UnsupportedCommand";
                await WriteCommandFailedEventAsync(command, result.ErrorCode, result.Message, cancellationToken);
                return result;
        }

        await WriteCommandCompletedEventAsync(command, cancellationToken);
        return result;
    }

    public RuntimeState CreateRuntimeSnapshot()
    {
        return new RuntimeState
        {
            ProcessId = ProcessId,
            StartedAtUtc = StartedAtUtc,
            LastHeartbeatUtc = LastHeartbeatUtc,
            LastSampleUtc = LastSampleUtc,
            State = ActualState,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            Version = typeof(AgentStateMachine).Assembly.GetName().Version?.ToString() ?? "0.1.0"
        };
    }

    public AgentHealthState CreateHealthSnapshot(string? message = null, string? errorCode = null)
    {
        return new AgentHealthState
        {
            ActualState = ActualState,
            IsHealthy = errorCode is null && ActualState is not AgentActualState.Error,
            LastHeartbeatUtc = LastHeartbeatUtc,
            CheckedAtUtc = DateTime.UtcNow,
            LastSampleUtc = LastSampleUtc,
            LastErrorUtc = errorCode is null ? null : DateTime.UtcNow,
            SampleCountSinceStart = _sampleCountSinceStart,
            DatabaseWriteErrorCount = _databaseWriteErrorCount,
            CaptureErrorCount = _captureErrorCount,
            CurrentSessionId = _currentSessionId,
            EventWriteErrorCount = _eventWriter?.EventWriteErrorCount ?? 0,
            JournalWriteErrorCount = _eventWriter?.JournalWriteErrorCount ?? 0,
            LastEventWriteError = _eventWriter?.LastEventWriteError,
            LastJournalWriteError = _eventWriter?.LastJournalWriteError,
            LastEventWriteErrorUtc = _eventWriter?.LastEventWriteErrorUtc,
            LastJournalWriteErrorUtc = _eventWriter?.LastJournalWriteErrorUtc,
            Message = message,
            ErrorCode = errorCode ?? (ActualState == AgentActualState.Error ? "AgentError" : null)
        };
    }

    private async Task TransitionToRunningAsync(string message, CancellationToken cancellationToken)
    {
        ActualState = AgentActualState.Resuming;
        await PersistAsync(message, cancellationToken);

        ActualState = AgentActualState.Running;
        _lastSampleAtUtc = DateTime.UtcNow;
        await PersistAsync(message, cancellationToken);
        await WriteLifecycleEventAsync(
            AgentEventType.AgentResumed,
            AgentEventLevel.Info,
            "Collection resumed",
            cancellationToken,
            payload: new Dictionary<string, object?>
            {
                ["actualState"] = ActualState.ToString(),
                ["processId"] = ProcessId
            });
    }

    private async Task TransitionToPausedAsync(string message, CancellationToken cancellationToken)
    {
        ActualState = AgentActualState.Pausing;
        await PersistAsync(message, cancellationToken);

        var sessionResult = await CloseOpenSessionSafelyAsync("Paused", "SessionWriteFailed", cancellationToken);
        if (sessionResult is not null)
        {
            await HandleSessionAggregationResultAsync(sessionResult, cancellationToken);
        }
        ActualState = AgentActualState.Paused;
        await PersistAsync(message, cancellationToken);
        await WriteLifecycleEventAsync(
            AgentEventType.AgentPaused,
            AgentEventLevel.Info,
            "Collection paused",
            cancellationToken,
            payload: new Dictionary<string, object?>
            {
                ["actualState"] = ActualState.ToString(),
                ["processId"] = ProcessId
            },
            allowedPayloadKeys: LifecyclePayloadKeys);
    }

    private async Task TransitionToStoppedAsync(string message, CancellationToken cancellationToken)
    {
        ActualState = AgentActualState.Stopping;
        await PersistAsync(message, cancellationToken);

        var sessionResult = await CloseOpenSessionSafelyAsync("Stopped", "SessionWriteFailed", cancellationToken);
        if (sessionResult is not null)
        {
            await HandleSessionAggregationResultAsync(sessionResult, cancellationToken);
        }
        ActualState = AgentActualState.Stopped;
        await PersistAsync(message, cancellationToken);
        await WriteLifecycleEventAsync(
            AgentEventType.AgentStopped,
            AgentEventLevel.Info,
            "Agent stopping",
            cancellationToken,
            payload: new Dictionary<string, object?>
            {
                ["actualState"] = ActualState.ToString(),
                ["processId"] = ProcessId
            },
            allowedPayloadKeys: LifecyclePayloadKeys);
    }

    private Task WriteLifecycleEventAsync(
        AgentEventType eventType,
        AgentEventLevel eventLevel,
        string message,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? payload = null,
        string[]? allowedPayloadKeys = null)
    {
        return WriteAgentEventAsync(
            eventType,
            eventLevel,
            message,
            cancellationToken,
            payload,
            source: nameof(AgentStateMachine),
            allowedPayloadKeys: allowedPayloadKeys ?? LifecyclePayloadKeys);
    }

    private Task WriteSessionClosedEventAsync(
        AppSession closedSession,
        string closeReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(closedSession);

        return WriteAgentEventAsync(
            AgentEventType.SessionClosed,
            AgentEventLevel.Info,
            "Session closed",
            cancellationToken,
            new Dictionary<string, object?>
            {
                ["sessionId"] = closedSession.Id,
                ["processName"] = closedSession.ProcessName,
                ["closeReason"] = closeReason,
                ["totalDurationSeconds"] = closedSession.TotalDurationSeconds,
                ["activeDurationSeconds"] = closedSession.ActiveDurationSeconds,
                ["idleDurationSeconds"] = closedSession.IdleDurationSeconds,
                ["unknownDurationSeconds"] = closedSession.UnknownDurationSeconds
            },
            source: nameof(SessionAggregator),
            sessionId: closedSession.Id,
            allowedPayloadKeys: SessionClosedPayloadKeys);
    }

    private Task WriteSessionStartedEventAsync(
        AppSession startedSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startedSession);

        return WriteAgentEventAsync(
            AgentEventType.SessionStarted,
            AgentEventLevel.Info,
            "Session started",
            cancellationToken,
            new Dictionary<string, object?>
            {
                ["sessionId"] = startedSession.Id,
                ["processName"] = startedSession.ProcessName,
                ["startedAtUtc"] = startedSession.StartedAtUtc.ToUniversalTime().ToString("O"),
                ["processId"] = ProcessId,
                ["actualState"] = ActualState.ToString()
            },
            source: nameof(SessionAggregator),
            sessionId: startedSession.Id,
            allowedPayloadKeys: SessionStartedPayloadKeys);
    }

    private async Task HandleSessionAggregationResultAsync(
        SessionAggregationResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.ClosedSession is not null)
        {
            await WriteSessionClosedEventAsync(
                result.ClosedSession,
                result.CloseReason ?? result.ClosedSession.CloseReason,
                cancellationToken);

            if (_currentSessionId == result.ClosedSession.Id)
            {
                _currentSessionId = null;
            }
        }

        if (result.StartedSession is not null)
        {
            await WriteSessionStartedEventAsync(result.StartedSession, cancellationToken);
            _currentSessionId = result.StartedSession.Id;
        }
    }

    private async Task<SessionAggregationResult?> CloseOpenSessionSafelyAsync(
        string closeReason,
        string errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _sessionAggregator.CloseOpenSessionAsync(closeReason, cancellationToken);
        }
        catch (Exception ex)
        {
            _databaseWriteErrorCount++;
            await WriteCaptureFailedEventAsync(ex, errorCode, "Session write failed", cancellationToken);
            await PersistAsync($"Session write failed: {GetShortExceptionMessage(ex)}", cancellationToken, errorCode);
            LogCaptureFailed(errorCode, ex);
            return null;
        }
    }

    private static string GetPrivacyRuleType(ForegroundSamplePrivacyDecision decision)
    {
        var reason = decision.Reason ?? string.Empty;
        if (reason.Contains("process privacy rule", StringComparison.OrdinalIgnoreCase))
        {
            return "Process";
        }

        if (reason.Contains("title privacy rule", StringComparison.OrdinalIgnoreCase))
        {
            return "Title";
        }

        return "Unknown";
    }

    private Task WritePrivacyFilteredEventAsync(
        ForegroundSample sample,
        ForegroundSamplePrivacyDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(decision);

        var processName = GetSafeProcessName(sample.ProcessName);
        var ruleType = GetPrivacyRuleType(decision);
        var payload = new Dictionary<string, object?>
        {
            ["ruleType"] = ruleType,
            ["processName"] = processName,
            ["privacyReason"] = GetPrivacyReason(decision),
            ["processId"] = ProcessId,
            ["actualState"] = ActualState.ToString()
        };

        return WriteAgentEventAsync(
            AgentEventType.PrivacyFiltered,
            AgentEventLevel.Info,
            "Sample filtered by privacy",
            cancellationToken,
            payload: payload,
            source: nameof(AgentStateMachine),
            allowedPayloadKeys: PrivacyFilteredPayloadKeys,
            rateLimitKey: $"PrivacyFiltered:{ruleType}:{processName}");
    }

    private Task WriteCaptureFailedEventAsync(
        Exception exception,
        string errorCode,
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var utcNow = DateTime.UtcNow;
        if (!_eventRateLimiter.ShouldAllow($"CaptureFailed:{errorCode}", utcNow))
        {
            return Task.CompletedTask;
        }

        var payload = new Dictionary<string, object?>
        {
            ["errorCode"] = errorCode,
            ["exceptionType"] = exception.GetType().Name,
            ["shortMessage"] = DiagnosticMessageSanitizer.CreateSafeExceptionMessage(exception, 160)
        };

        return WriteAgentEventAsync(
            AgentEventType.CaptureFailed,
            AgentEventLevel.Error,
            message,
            cancellationToken,
            payload: payload,
            source: nameof(AgentStateMachine),
            errorCode: errorCode,
            allowedPayloadKeys: CaptureFailedPayloadKeys);
    }

    private async Task WriteAgentEventAsync(
        AgentEventType eventType,
        AgentEventLevel eventLevel,
        string message,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? payload = null,
        string? source = null,
        string? requestId = null,
        string? errorCode = null,
        string? processName = null,
        long? sessionId = null,
        string[]? allowedPayloadKeys = null,
        string? rateLimitKey = null)
    {
        if (_eventWriter is null)
        {
            return;
        }

        if (rateLimitKey is not null && !_eventRateLimiter.ShouldAllow(rateLimitKey, DateTime.UtcNow))
        {
            return;
        }

        var agentEvent = new AgentEvent
        {
            EventTimeUtc = DateTime.UtcNow,
            EventType = eventType,
            EventLevel = eventLevel,
            Message = message,
            Source = source,
            RequestId = requestId,
            ErrorCode = errorCode,
            ProcessName = processName ?? typeof(AgentStateMachine).Assembly.GetName().Name,
            SessionId = sessionId,
            PayloadJson = AgentEventPayloadSanitizer.CreatePayloadJson(payload, allowedPayloadKeys ?? Array.Empty<string>())
        };

        await _eventWriter.WriteAsync(agentEvent, _options, cancellationToken);
    }

    private Task WriteCommandDetectedEventAsync(
        AgentControlCommand command,
        CancellationToken cancellationToken)
    {
        return WriteCommandEventAsync(
            AgentEventType.CommandDetected,
            AgentEventLevel.Info,
            "Command detected",
            command,
            cancellationToken);
    }

    private Task WriteCommandAcceptedEventAsync(
        AgentControlCommand command,
        CancellationToken cancellationToken)
    {
        return WriteCommandEventAsync(
            AgentEventType.CommandAccepted,
            AgentEventLevel.Info,
            "Command accepted",
            command,
            cancellationToken);
    }

    private Task WriteCommandCompletedEventAsync(
        AgentControlCommand command,
        CancellationToken cancellationToken)
    {
        return WriteCommandEventAsync(
            AgentEventType.CommandCompleted,
            AgentEventLevel.Info,
            "Command completed",
            command,
            cancellationToken);
    }

    private Task WriteCommandFailedEventAsync(
        AgentControlCommand command,
        string? errorCode,
        string? message,
        CancellationToken cancellationToken,
        Exception? exception = null)
    {
        var eventLevel = exception is null ? AgentEventLevel.Warning : AgentEventLevel.Error;
        var eventMessage = string.IsNullOrWhiteSpace(message) ? "Command failed" : message;
        var payload = CreateCommandPayload(command, includeCommandSource: true);
        payload["errorCode"] = errorCode;
        if (exception is not null)
        {
            payload["exceptionType"] = exception.GetType().Name;
            payload["shortMessage"] = GetShortExceptionMessage(exception);
        }

        return WriteAgentEventAsync(
            AgentEventType.CommandFailed,
            eventLevel,
            eventMessage,
            cancellationToken,
            payload: payload,
            source: nameof(AgentStateMachine),
            requestId: string.IsNullOrWhiteSpace(command.RequestId) ? null : command.RequestId,
            errorCode: errorCode,
            allowedPayloadKeys: CommandFailurePayloadKeys);
    }

    private Task WriteCommandInvalidJsonEventAsync(CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["commandSource"] = "FileFallback",
            ["quarantined"] = true,
            ["fileKind"] = "agent_control"
        };

        return WriteAgentEventAsync(
            AgentEventType.CommandInvalidJson,
            AgentEventLevel.Warning,
            "Control command JSON invalid",
            cancellationToken,
            payload: payload,
            source: nameof(AgentStateMachine),
            requestId: null,
            errorCode: "CommandInvalidJson",
            allowedPayloadKeys: CommandInvalidJsonPayloadKeys);
    }

    private Task WriteCommandEventAsync(
        AgentEventType eventType,
        AgentEventLevel eventLevel,
        string message,
        AgentControlCommand command,
        CancellationToken cancellationToken)
    {
        var payload = CreateCommandPayload(command, includeCommandSource: true);
        return WriteAgentEventAsync(
            eventType,
            eventLevel,
            message,
            cancellationToken,
            payload: payload,
            source: nameof(AgentStateMachine),
            requestId: string.IsNullOrWhiteSpace(command.RequestId) ? null : command.RequestId,
            allowedPayloadKeys: CommandPayloadKeys);
    }

    private Dictionary<string, object?> CreateCommandPayload(
        AgentControlCommand command,
        bool includeCommandSource)
    {
        var payload = new Dictionary<string, object?>
        {
            ["command"] = command.Command.ToString(),
            ["desiredState"] = command.DesiredState?.ToString(),
            ["requestedBy"] = command.RequestedBy,
            ["requestedAtUtc"] = command.RequestedAtUtc.ToUniversalTime().ToString("O"),
            ["waitForCompletion"] = command.WaitForCompletion,
            ["timeoutMilliseconds"] = command.TimeoutMilliseconds,
            ["actualState"] = ActualState.ToString()
        };

        if (includeCommandSource)
        {
            payload["commandSource"] = "FileFallback";
        }

        return payload;
    }

    private static string GetShortExceptionMessage(Exception exception)
    {
        return DiagnosticMessageSanitizer.CreateSafeExceptionMessage(exception, 160);
    }

    private static string GetPrivacyReason(ForegroundSamplePrivacyDecision decision)
    {
        var reason = decision.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "Excluded by privacy rule";
        }

        if (reason.Contains("process privacy rule", StringComparison.OrdinalIgnoreCase))
        {
            return "Excluded by process privacy rule";
        }

        if (reason.Contains("title privacy rule", StringComparison.OrdinalIgnoreCase))
        {
            return "Excluded by title privacy rule";
        }

        return "Excluded by privacy rule";
    }

    private static string GetPrivacyPersistMessage(ForegroundSamplePrivacyDecision decision)
    {
        if (decision.Reason?.Contains("process privacy rule", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "process privacy rule";
        }

        if (decision.Reason?.Contains("title privacy rule", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "title privacy rule";
        }

        return "privacy rule";
    }

    private async Task PersistAsync(string message, CancellationToken cancellationToken, string? errorCode = null)
    {
        LastHeartbeatUtc = DateTime.UtcNow;
        _lastPersistedHeartbeatUtc = LastHeartbeatUtc;

        var runtimeState = CreateRuntimeSnapshot();
        var healthState = CreateHealthSnapshot(DiagnosticMessageSanitizer.CreateSafeText(message, 240), errorCode);

        await _runtimeStateStore.WriteAsync(_paths.RuntimeStatePath, runtimeState, cancellationToken);
        await _healthStateStore.WriteAsync(_paths.HealthStatePath, healthState, cancellationToken);

        LogPersistedState(message, errorCode);
    }

    private void LogPersistedState(string message, string? errorCode)
    {
        var terminalMessage = GetTerminalStateMessage(message);
        if (terminalMessage is null)
        {
            return;
        }

        if (errorCode is null)
        {
            _logger.LogInformation("{Message}：状态={State}", terminalMessage, ActualState);
            return;
        }

        _logger.LogWarning("{Message}：状态={State}，errorCode={ErrorCode}", terminalMessage, ActualState, errorCode);
    }

    private string? GetTerminalStateMessage(string message)
    {
        if (message.StartsWith("Sample excluded:", StringComparison.Ordinal)
            || message.StartsWith("Foreground capture failed:", StringComparison.Ordinal)
            || message.StartsWith("Sample write failed:", StringComparison.Ordinal)
            || message.StartsWith("Session aggregation failed:", StringComparison.Ordinal)
            || message.StartsWith("Session write failed:", StringComparison.Ordinal)
            || string.Equals(message, "Sample captured", StringComparison.Ordinal))
        {
            return null;
        }

        return message switch
        {
            "Agent starting" => "Agent 正在启动",
            "Agent initialized" => "Agent 已启动",
            "Heartbeat" or "Paused heartbeat" => "心跳已更新",
            "Collection paused" when ActualState == AgentActualState.Paused => "已暂停采集：当前窗口不再写入样本，心跳仍继续",
            "Collection paused" => "正在暂停采集",
            "Collection resumed" when ActualState == AgentActualState.Running => "已恢复采集：继续写入前台样本",
            "Collection resumed" => "正在恢复采集",
            "Agent stopping" when ActualState == AgentActualState.Stopped => "已停止：open session 已关闭",
            "Agent stopping" => "正在停止：正在关闭 open session",
            "Agent stopped" => "已停止：open session 已关闭",
            "Status requested" => "状态查询已处理",
            "Config reloaded" => "配置已重新加载",
            _ when message.EndsWith(" accepted", StringComparison.Ordinal) => "命令已接受",
            _ when message.StartsWith("Malformed control file:", StringComparison.Ordinal) => "控制文件格式错误",
            _ => "状态已更新"
        };
    }

    private void LogSampleCaptured(ForegroundSample sample)
    {
        var processName = GetSafeProcessName(sample.ProcessName);
        var displayName = ProductDisplayNameResolver.Resolve(processName);

        _logger.LogInformation(
            "采样成功：状态={State}，前台={DisplayName}，idle={IdleSeconds}秒，sampleId={SampleId}，已写入数据库",
            ActualState,
            displayName,
            sample.IdleSeconds,
            sample.Id);
    }

    private void LogPrivacyFiltered(ForegroundSamplePrivacyDecision decision)
    {
        var reason = decision.Reason ?? string.Empty;
        if (reason.Contains("title", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("已跳过采样：命中标题隐私规则");
            return;
        }

        if (reason.Contains("process", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("已跳过采样：命中进程隐私规则");
            return;
        }

        _logger.LogInformation("已跳过采样：命中隐私规则");
    }

    private void LogCaptureFailed(string errorCode, Exception exception)
    {
        _logger.LogWarning(
            "采样相关步骤失败：errorCode={ErrorCode}，message={ErrorMessage}",
            errorCode,
            GetShortExceptionMessage(exception));
    }

    private static string GetSafeProcessName(string processName)
    {
        return string.IsNullOrWhiteSpace(processName) ? "Unknown" : processName.Trim();
    }

    private static ForegroundSample NormalizeActivityState(ForegroundSample sample, WindowsAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(options);

        if (string.Equals(sample.ActivityState, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return sample;
        }

        var thresholdSeconds = Math.Max(0, options.IdleThresholdSeconds);
        var normalizedState = sample.IdleSeconds >= thresholdSeconds ? "Idle" : "Active";
        if (string.Equals(sample.ActivityState, normalizedState, StringComparison.OrdinalIgnoreCase))
        {
            return sample;
        }

        return new ForegroundSample
        {
            Id = sample.Id,
            SampleTimeUtc = sample.SampleTimeUtc,
            ProcessName = sample.ProcessName,
            WindowTitle = sample.WindowTitle,
            ExecutablePath = sample.ExecutablePath,
            IdleSeconds = sample.IdleSeconds,
            ActivityState = normalizedState
        };
    }
}
