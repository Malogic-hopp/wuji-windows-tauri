using QuantifiedSelf.Windows.Client.Bridge;

using var shutdown = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

Console.CancelKeyPress += cancelHandler;
try
{
    return await BridgeProgram.RunAsync(
        args,
        Console.OpenStandardInput(),
        Console.OpenStandardOutput(),
        Console.Error,
        shutdown.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
