using QuantifiedSelf.Windows.Agent.Services;
using QuantifiedSelf.Windows.Core.Control;
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
    private readonly WindowsAgentPaths _paths;
    private readonly RuntimeStateStore _runtimeStateStore;
    private readonly AgentHealthStateStore _healthStateStore;
    private readonly AgentControlFileStore _controlFileStore;
    private readonly WindowsAgentOptionsStore _optionsStore;
    private readonly SqliteDatabaseInitializer _databaseInitializer;
    private readonly ForegroundSampleRepository _foregroundSampleRepository;
    private readonly SessionAggregator _sessionAggregator;
    private readonly ForegroundSamplePrivacyFilter _privacyFilter;
    private readonly IForegroundSampleProvider _foregroundSampleProvider;
    private readonly ILogger<AgentStateMachine> _logger;

    private WindowsAgentOptions _options = new();
    private DateTime _lastPersistedHeartbeatUtc = DateTime.MinValue;
    private DateTime _lastSampleAtUtc = DateTime.MinValue;
    private string? _lastProcessedRequestId;
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
        IForegroundSampleProvider foregroundSampleProvider,
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
        _logger = logger;
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

        StartedAtUtc = DateTime.UtcNow;
        ActualState = AgentActualState.Starting;
        LastHeartbeatUtc = StartedAtUtc;
        LastSampleUtc = null;
        _lastSampleAtUtc = StartedAtUtc;
        _sampleCountSinceStart = 0;
        _databaseWriteErrorCount = 0;
        _captureErrorCount = 0;

        await PersistAsync("Agent starting", cancellationToken);
        await _sessionAggregator.CloseOpenSessionAsync("AgentStarted", cancellationToken);
        ActualState = AgentActualState.Running;
        await PersistAsync("Agent initialized", cancellationToken);
    }

    public async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        var commandRead = await _controlFileStore.ReadForAgentAsync(_paths.AgentControlPath, cancellationToken);
        if (commandRead.WasMalformed)
        {
            _logger.LogWarning(
                "Malformed control file {ControlPath}: {Error}",
                _paths.AgentControlPath,
                commandRead.ErrorMessage);
            await PersistAsync(
                $"Malformed control file: {commandRead.ErrorMessage}",
                cancellationToken,
                "MalformedControlFile");
        }

        var command = commandRead.Command;
        if (command is not null && command.RequestId != _lastProcessedRequestId)
        {
            await ProcessCommandAsync(command, cancellationToken);
            _lastProcessedRequestId = command.RequestId;

            try
            {
                File.Delete(_paths.AgentControlPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete control file {ControlPath}", _paths.AgentControlPath);
            }
        }

        var now = DateTime.UtcNow;
        var heartbeatDue = now - _lastPersistedHeartbeatUtc >= TimeSpan.FromSeconds(Math.Max(1, _options.HeartbeatIntervalSeconds));
        var sampleDue = now - _lastSampleAtUtc >= TimeSpan.FromSeconds(Math.Max(1, _options.SamplingIntervalSeconds));

        if (ActualState == AgentActualState.Running && sampleDue)
        {
            try
            {
                var sample = _foregroundSampleProvider.Capture();
                var privacyDecision = _privacyFilter.Apply(sample, _options);

                if (!privacyDecision.ShouldWriteSample)
                {
                    if (privacyDecision.ShouldCloseOpenSession)
                    {
                        await _sessionAggregator.CloseOpenSessionAsync("PrivacyExcluded", cancellationToken);
                    }

                    _lastSampleAtUtc = now;
                    await PersistAsync(
                        $"Sample excluded: {privacyDecision.Reason ?? sample.ProcessName}",
                        cancellationToken);
                    return true;
                }

                var filteredSample = privacyDecision.Sample ?? sample;
                await _foregroundSampleRepository.InsertAsync(filteredSample, cancellationToken);
                await _sessionAggregator.HandleSampleAsync(filteredSample, _options.SamplingIntervalSeconds, cancellationToken);

                LastSampleUtc = filteredSample.SampleTimeUtc;
                _lastSampleAtUtc = filteredSample.SampleTimeUtc;
                _sampleCountSinceStart++;
                await PersistAsync("Sample captured", cancellationToken);
                _logger.LogInformation("Sample tick at {Time}", filteredSample.SampleTimeUtc);
            }
            catch (Exception ex)
            {
                _captureErrorCount++;
                _databaseWriteErrorCount++;
                await PersistAsync($"Sample capture failed: {ex.Message}", cancellationToken, "SampleCaptureFailed");
                _logger.LogWarning(ex, "Sample capture failed");
            }
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
                await TransitionToPausedAsync("Collection paused", cancellationToken);
                result.Completed = true;
                result.ActualState = ActualState;
                result.Message = "Collection paused";
                return result;

            case AgentCommandType.Resume:
                await TransitionToRunningAsync("Collection resumed", cancellationToken);
                result.Completed = true;
                result.ActualState = ActualState;
                result.Message = "Collection resumed";
                return result;

            case AgentCommandType.Stop:
                await TransitionToStoppedAsync("Agent stopping", cancellationToken);
                result.Completed = true;
                result.ActualState = ActualState;
                result.Message = "Agent stopping";
                return result;

            case AgentCommandType.GetStatus:
                await PersistAsync("Status requested", cancellationToken);
                result.Completed = true;
                result.ActualState = ActualState;
                result.Message = "Status returned";
                return result;

            case AgentCommandType.ReloadConfig:
                _options = await _optionsStore.ReadAsync(_paths.AgentOptionsPath, cancellationToken) ?? new WindowsAgentOptions();
                await PersistAsync("Config reloaded", cancellationToken);
                result.Completed = true;
                result.ActualState = ActualState;
                result.Message = "Config reloaded";
                return result;

            case AgentCommandType.UpdateAppMetadata:
            case AgentCommandType.UpdatePrivacyRules:
            case AgentCommandType.PruneData:
            case AgentCommandType.ClearHistory:
                await PersistAsync($"{command.Command} accepted", cancellationToken);
                result.Completed = true;
                result.ActualState = ActualState;
                result.Message = $"{command.Command} accepted";
                return result;

            default:
                result.Accepted = false;
                result.Message = $"Unsupported command: {command.Command}";
                result.ErrorCode = "UnsupportedCommand";
                return result;
        }
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
    }

    private async Task TransitionToPausedAsync(string message, CancellationToken cancellationToken)
    {
        ActualState = AgentActualState.Pausing;
        await PersistAsync(message, cancellationToken);

        await _sessionAggregator.CloseOpenSessionAsync("Paused", cancellationToken);
        ActualState = AgentActualState.Paused;
        await PersistAsync(message, cancellationToken);
    }

    private async Task TransitionToStoppedAsync(string message, CancellationToken cancellationToken)
    {
        ActualState = AgentActualState.Stopping;
        await PersistAsync(message, cancellationToken);

        await _sessionAggregator.CloseOpenSessionAsync("Stopped", cancellationToken);
        ActualState = AgentActualState.Stopped;
        await PersistAsync(message, cancellationToken);
    }

    private async Task PersistAsync(string message, CancellationToken cancellationToken, string? errorCode = null)
    {
        LastHeartbeatUtc = DateTime.UtcNow;
        _lastPersistedHeartbeatUtc = LastHeartbeatUtc;

        var runtimeState = CreateRuntimeSnapshot();
        var healthState = CreateHealthSnapshot(message, errorCode);

        await _runtimeStateStore.WriteAsync(_paths.RuntimeStatePath, runtimeState, cancellationToken);
        await _healthStateStore.WriteAsync(_paths.HealthStatePath, healthState, cancellationToken);

        _logger.LogInformation(
            "Persisted state {State} at {Heartbeat} ({Message})",
            ActualState,
            LastHeartbeatUtc,
            message);
    }
}
