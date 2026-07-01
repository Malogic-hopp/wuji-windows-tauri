using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using QuantifiedSelf.Windows.Core.Ipc;

namespace QuantifiedSelf.Windows.Infrastructure.Ipc;

public sealed class NamedPipeAgentCommandServer : IAgentIpcServer
{
    private readonly ILogger<NamedPipeAgentCommandServer> _logger;

    public NamedPipeAgentCommandServer(ILogger<NamedPipeAgentCommandServer>? logger = null)
    {
        _logger = logger ?? NullLogger<NamedPipeAgentCommandServer>.Instance;
    }

    public async Task StartAsync(
        string pipeName,
        Func<AgentIpcRequest, CancellationToken, Task<AgentIpcResponse>> handler,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;

            try
            {
                server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);
                await HandleConnectionAsync(server, handler, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (server is not null) await server.DisposeAsync();
                break;
            }
            catch (Exception ex)
            {
                var safeMessage = ex is IpcProtocolException
                    ? ex.Message
                    : ex.GetType().Name;
                _logger.LogWarning("IPC connection error: {Message}", safeMessage);

                // Delay before retry to avoid hot loop on persistent failures
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                if (server is not null) await server.DisposeAsync();
            }
        }
    }

    private static async Task HandleConnectionAsync(
        NamedPipeServerStream server,
        Func<AgentIpcRequest, CancellationToken, Task<AgentIpcResponse>> handler,
        CancellationToken cancellationToken)
    {
        AgentIpcResponse response;

        try
        {
            var request = await NamedPipeProtocol.ReadMessageAsync<AgentIpcRequest>(server, cancellationToken);
            response = await handler(request, cancellationToken);
        }
        catch (IpcProtocolException ex)
        {
            response = new AgentIpcResponse
            {
                Accepted = false,
                Completed = false,
                ErrorCode = ex.ErrorCode,
                Message = ex.Message
            };
        }
        catch (Exception)
        {
            response = new AgentIpcResponse
            {
                Accepted = false,
                Completed = false,
                ErrorCode = "IpcProtocolError",
                Message = "IPC protocol error."
            };
        }

        try
        {
            await NamedPipeProtocol.WriteMessageAsync(server, response, cancellationToken);
        }
        catch
        {
            // Client may have disconnected; nothing we can do
        }
    }
}
