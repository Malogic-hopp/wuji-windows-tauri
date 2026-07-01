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
    private readonly ILogger<AgentCommandServerHostedService> _logger;

    public AgentCommandServerHostedService(
        AgentStateMachine stateMachine,
        WindowsAgentPaths paths,
        NamedPipeAgentCommandServer server,
        ILogger<AgentCommandServerHostedService>? logger = null)
    {
        _stateMachine = stateMachine;
        _paths = paths;
        _server = server;
        _logger = logger ?? NullLogger<AgentCommandServerHostedService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
            ?? Environment.UserName;

        AgentPipeName pipeName;
        try
        {
            pipeName = new AgentPipeName(userSid);
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
        return AgentIpcCommandDispatcher.DispatchAsync(request, _stateMachine);
    }
}

internal static class AgentIpcCommandDispatcher
{
    public static Task<AgentIpcResponse> DispatchAsync(
        AgentIpcRequest request,
        AgentStateMachine stateMachine)
    {
        if (request.ProtocolVersion != 1)
        {
            return Task.FromResult(new AgentIpcResponse
            {
                RequestId = request.RequestId,
                Accepted = false,
                Completed = false,
                ErrorCode = "UnsupportedProtocolVersion",
                Message = "Unsupported protocol version."
            });
        }

        var startedAt = DateTime.UtcNow;

        switch (request.Command)
        {
            case "Ping":
                return Task.FromResult(new AgentIpcResponse
                {
                    ProtocolVersion = 1,
                    RequestId = request.RequestId,
                    Accepted = true,
                    Completed = true,
                    Message = "Pong",
                    ActualState = stateMachine.ActualState,
                    StartedAtUtc = startedAt,
                    CompletedAtUtc = DateTime.UtcNow
                });

            case "GetStatus":
                var snapshot = stateMachine.CreateRuntimeSnapshot();
                var health = stateMachine.CreateHealthSnapshot();
                return Task.FromResult(new AgentIpcResponse
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
                        DesiredState = null, // AgentStateMachine does not track a persistent DesiredState
                        ProcessId = stateMachine.ProcessId,
                        StartedAtUtc = snapshot.StartedAtUtc,
                        LastHeartbeatUtc = stateMachine.LastHeartbeatUtc,
                        LastSampleUtc = stateMachine.LastSampleUtc,
                        CurrentSessionId = health.CurrentSessionId,
                        Version = snapshot.Version,
                        IsHealthy = health.IsHealthy
                    }
                });

            default:
                return Task.FromResult(new AgentIpcResponse
                {
                    RequestId = request.RequestId,
                    Accepted = false,
                    Completed = false,
                    ErrorCode = "UnsupportedIpcCommand",
                    Message = "Unsupported IPC command."
                });
        }
    }
}
