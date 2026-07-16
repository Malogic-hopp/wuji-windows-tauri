using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuantifiedSelf.Windows.Agent.State;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Ipc;

namespace QuantifiedSelf.Windows.Agent.Services;

public sealed class AgentCommandServerHostedService : BackgroundService
{
    private readonly AgentStateMachine _stateMachine;
    private readonly WindowsAgentPaths _paths;
    private readonly NamedPipeAgentCommandServer _server;
    private readonly ProcessedRequestCache _requestCache;
    private readonly ILogger<AgentCommandServerHostedService> _logger;

    public AgentCommandServerHostedService(
        AgentStateMachine stateMachine,
        WindowsAgentPaths paths,
        NamedPipeAgentCommandServer server,
        ProcessedRequestCache requestCache,
        ILogger<AgentCommandServerHostedService>? logger = null)
    {
        _stateMachine = stateMachine;
        _paths = paths;
        _server = server;
        _requestCache = requestCache;
        _logger = logger ?? NullLogger<AgentCommandServerHostedService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
            ?? Environment.UserName;

        AgentPipeName pipeName;
        try
        {
            pipeName = new AgentPipeName(userSid, _paths.ChannelName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to create pipe name: {Type}", ex.GetType().Name);
            return;
        }

        _logger.LogInformation("IPC server starting on pipe: {DisplayName}", pipeName.DisplayPipeName);

        try
        {
            await _server.StartAsync(pipeName.FullPipeName, HandleIpcRequestAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning("IPC server stopped unexpectedly: {Type}", ex.GetType().Name);
            // IPC unavailable, but sampling continues — BackgroundService won't fault
        }
    }

    private Task<AgentIpcResponse> HandleIpcRequestAsync(
        AgentIpcRequest request,
        CancellationToken cancellationToken)
    {
        return AgentIpcCommandDispatcher.DispatchAsync(request, _stateMachine, _requestCache);
    }
}

internal static class AgentIpcCommandDispatcher
{
    public static async Task<AgentIpcResponse> DispatchAsync(
        AgentIpcRequest request,
        AgentStateMachine stateMachine,
        ProcessedRequestCache? requestCache = null)
    {
        if (request.ProtocolVersion != 1)
        {
            return new AgentIpcResponse
            {
                RequestId = request.RequestId,
                Accepted = false,
                Completed = false,
                ErrorCode = "UnsupportedProtocolVersion",
                Message = "Unsupported protocol version."
            };
        }

        var startedAt = DateTime.UtcNow;

        switch (request.Command)
        {
            case "Ping":
                return new AgentIpcResponse
                {
                    ProtocolVersion = 1,
                    RequestId = request.RequestId,
                    Accepted = true,
                    Completed = true,
                    Message = "Pong",
                    ActualState = stateMachine.ActualState,
                    StartedAtUtc = startedAt,
                    CompletedAtUtc = DateTime.UtcNow
                };

            case "GetStatus":
                var snapshot = stateMachine.CreateRuntimeSnapshot();
                var health = stateMachine.CreateHealthSnapshot();
                return new AgentIpcResponse
                {
                    ProtocolVersion = 1,
                    RequestId = request.RequestId,
                    Accepted = true,
                    Completed = true,
                    ActualState = stateMachine.ActualState,
                    StartedAtUtc = startedAt,
                    CompletedAtUtc = DateTime.UtcNow,
                    Status = new AgentIpcStatus
                    {
                        ActualState = stateMachine.ActualState,
                        DesiredState = null,
                        ProcessId = stateMachine.ProcessId,
                        StartedAtUtc = snapshot.StartedAtUtc,
                        LastHeartbeatUtc = stateMachine.LastHeartbeatUtc,
                        LastSampleUtc = stateMachine.LastSampleUtc,
                        CurrentSessionId = health.CurrentSessionId,
                        Version = snapshot.Version,
                        IsHealthy = health.IsHealthy
                    }
                };

            case "Pause":
            case "Resume":
            case "Stop":
            case "ReloadConfig":
            case "PruneData":
            case "ClearHistory":
                return await DispatchCommandAsync(request, stateMachine, requestCache, startedAt);

            default:
                return new AgentIpcResponse
                {
                    RequestId = request.RequestId,
                    Accepted = false,
                    Completed = false,
                    ErrorCode = "UnsupportedIpcCommand",
                    Message = "Unsupported IPC command."
                };
        }
    }

    private static async Task<AgentIpcResponse> DispatchCommandAsync(
        AgentIpcRequest request,
        AgentStateMachine stateMachine,
        ProcessedRequestCache? requestCache,
        DateTime startedAt)
    {
        // Dedup: prevent duplicate execution of side-effect commands
        if (requestCache is not null && !string.IsNullOrWhiteSpace(request.RequestId))
        {
            if (requestCache.TryMarkProcessed(request.RequestId))
            {
                return new AgentIpcResponse
                {
                    ProtocolVersion = 1,
                    RequestId = request.RequestId,
                    Accepted = true,
                    Completed = true,
                    ActualState = stateMachine.ActualState,
                    ErrorCode = "DuplicateRequest",
                    Message = "Duplicate request ignored.",
                    StartedAtUtc = startedAt,
                    CompletedAtUtc = DateTime.UtcNow
                };
            }
        }

        var commandType = request.Command switch
        {
            "Pause" => AgentCommandType.Pause,
            "Resume" => AgentCommandType.Resume,
            "Stop" => AgentCommandType.Stop,
            "ReloadConfig" => AgentCommandType.ReloadConfig,
            "PruneData" => AgentCommandType.PruneData,
            "ClearHistory" => AgentCommandType.ClearHistory,
            _ => throw new InvalidOperationException($"Unexpected command: {request.Command}")
        };

        var desiredState = commandType switch
        {
            AgentCommandType.Pause => AgentDesiredState.Paused,
            AgentCommandType.Resume => AgentDesiredState.Running,
            AgentCommandType.Stop => AgentDesiredState.Stopped,
            _ => (AgentDesiredState?)null
        };

        var command = new AgentControlCommand
        {
            Command = commandType,
            DesiredState = desiredState,
            RequestId = request.RequestId,
            RequestedBy = string.IsNullOrWhiteSpace(request.RequestedBy)
                ? "QuantifiedSelf.Windows.App"
                : request.RequestedBy,
            RequestedAtUtc = request.RequestedAtUtc,
            TimeoutMilliseconds = request.TimeoutMilliseconds,
            Reason = $"IPC requested {request.Command}"
        };

        var result = await stateMachine.ProcessCommandAsync(command, CancellationToken.None);

        return new AgentIpcResponse
        {
            ProtocolVersion = 1,
            RequestId = result.RequestId ?? request.RequestId,
            Accepted = result.Accepted,
            Completed = result.Completed,
            ActualState = result.ActualState,
            Message = result.Message,
            ErrorCode = result.ErrorCode,
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTime.UtcNow
        };
    }
}
