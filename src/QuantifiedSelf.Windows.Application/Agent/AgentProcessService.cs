using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Agent;
using QuantifiedSelf.Windows.ApplicationLayer.Models;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Ipc;

namespace QuantifiedSelf.Windows.ApplicationLayer.Agent;

public sealed class AgentProcessService : IAgentProcessService
{
    private readonly IAgentProcessController _processController;
    private readonly IAgentControlFallback _controlFallback;
    private readonly IAgentRuntimeStateReader _runtimeStateReader;
    private readonly IAgentTransport? _transport;

    internal int StopPollMaxAttempts { get; set; } = 30;
    internal int StopPollDelayMilliseconds { get; set; } = 500;

    public AgentProcessService(
        IAgentProcessController processController,
        IAgentControlFallback controlFallback,
        IAgentRuntimeStateReader runtimeStateReader,
        IAgentTransport? transport = null)
    {
        _processController = processController ?? throw new ArgumentNullException(nameof(processController));
        _controlFallback = controlFallback ?? throw new ArgumentNullException(nameof(controlFallback));
        _runtimeStateReader = runtimeStateReader ?? throw new ArgumentNullException(nameof(runtimeStateReader));
        _transport = transport;
    }

    public Task<AgentProcessInfo> StartAgentAsync(CancellationToken cancellationToken = default) =>
        _processController.StartAgentAsync(cancellationToken);

    public async Task<bool> StopAgentGracefullyAsync(CancellationToken cancellationToken = default)
    {
        var requestId = $"ipc-stop-{Guid.NewGuid():N}";
        var transportDelivered = false;

        if (_transport is not null)
        {
            try
            {
                await _transport.SendAsync(new AgentIpcRequest
                {
                    Command = "Stop",
                    RequestId = requestId,
                    RequestedBy = "QuantifiedSelf.Windows.App",
                    WaitForCompletion = false,
                    TimeoutMilliseconds = 2000
                }, cancellationToken);
                transportDelivered = true;
            }
            catch (TimeoutException)
            {
                for (var i = 0; i < 6; i++)
                {
                    if (!await IsAgentProcessRunningAsync(cancellationToken))
                    {
                        return true;
                    }

                    var runtimeState = await _runtimeStateReader.ReadRuntimeStateAsync(cancellationToken);
                    if (runtimeState?.State is AgentActualState.Stopped or AgentActualState.Stopping)
                    {
                        transportDelivered = true;
                        break;
                    }

                    await Task.Delay(500, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                if (!await IsAgentProcessRunningAsync(cancellationToken))
                {
                    return true;
                }

                var state = await _runtimeStateReader.ReadRuntimeStateAsync(cancellationToken);
                var processInfo = await _processController.GetAgentProcessInfoAsync(cancellationToken);
                if (state?.State == AgentActualState.Stopped
                    && state.ProcessId > 0
                    && (processInfo is null || processInfo.ProcessId != state.ProcessId))
                {
                    return true;
                }
            }
        }

        if (!transportDelivered)
        {
            await _controlFallback.WriteControlCommandAsync(new AgentControlCommand
            {
                Command = AgentCommandType.Stop,
                DesiredState = AgentDesiredState.Stopped,
                RequestId = requestId,
                RequestedBy = "QuantifiedSelf.Windows.App",
                Reason = "User requested stop"
            }, cancellationToken);
        }

        for (var attempt = 0; attempt < StopPollMaxAttempts; attempt++)
        {
            if (!await IsAgentProcessRunningAsync(cancellationToken))
            {
                return true;
            }

            await Task.Delay(StopPollDelayMilliseconds, cancellationToken);
        }

        return false;
    }

    public Task KillAgentAsFallbackAsync(CancellationToken cancellationToken = default) =>
        _processController.KillAgentAsFallbackAsync(cancellationToken);

    public Task<bool> IsAgentProcessRunningAsync(CancellationToken cancellationToken = default) =>
        _processController.IsAgentProcessRunningAsync(cancellationToken);

    public Task<AgentProcessInfo?> GetAgentProcessInfoAsync(CancellationToken cancellationToken = default) =>
        _processController.GetAgentProcessInfoAsync(cancellationToken);
}
