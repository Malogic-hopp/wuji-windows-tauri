using QuantifiedSelf.Windows.Client;

namespace QuantifiedSelf.Windows.Client.Bridge;

internal static class BridgeProgram
{
    public static async Task<int> RunAsync(
        string[] args,
        Stream input,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        BridgeLaunchOptions launchOptions;
        try
        {
            launchOptions = BridgeLaunchOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }

        try
        {
            var client = WujiClientFactory.Create(new WujiClientOptions
            {
                ChannelName = launchOptions.ChannelName
            });
            var host = new BridgeHost(client, new BridgeHostOptions(), error);
            await host.RunAsync(input, output, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch
        {
            await error.WriteLineAsync("Bridge terminated unexpectedly.").ConfigureAwait(false);
            return 1;
        }
    }
}
