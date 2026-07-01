using System.IO.Pipes;
using QuantifiedSelf.Windows.Core.Ipc;

namespace QuantifiedSelf.Windows.Infrastructure.Ipc;

public sealed class NamedPipeAgentControlClient : IAgentIpcClient
{
    private readonly AgentPipeName _pipeName;
    private readonly AgentIpcClientOptions _options;

    public NamedPipeAgentControlClient(AgentPipeName pipeName, AgentIpcClientOptions? options = null)
    {
        _pipeName = pipeName;
        _options = options ?? new AgentIpcClientOptions();
    }

    public async Task<AgentIpcResponse> SendAsync(AgentIpcRequest request, CancellationToken cancellationToken = default)
    {
        using var client = new NamedPipeClientStream(
            ".",
            _pipeName.FullPipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        // Phase 1: connect with dedicated connect timeout
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(_options.ConnectTimeoutMilliseconds);

        try
        {
            await client.ConnectAsync(_options.ConnectTimeoutMilliseconds, connectCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("IPC connect timed out.");
        }

        // Phase 2: write request and read response with request timeout
        var requestTimeoutMs = request.TimeoutMilliseconds > 0
            ? request.TimeoutMilliseconds
            : _options.RequestTimeoutMilliseconds;

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(requestTimeoutMs);

        try
        {
            await NamedPipeProtocol.WriteMessageAsync(client, request, requestCts.Token);
            return await NamedPipeProtocol.ReadMessageAsync<AgentIpcResponse>(client, requestCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("IPC request timed out.");
        }
    }
}
