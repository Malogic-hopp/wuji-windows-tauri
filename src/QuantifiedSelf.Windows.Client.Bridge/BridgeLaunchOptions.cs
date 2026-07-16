namespace QuantifiedSelf.Windows.Client.Bridge;

internal sealed record BridgeLaunchOptions(string ChannelName)
{
    private const string DevelopmentChannel = "dev";

    public static BridgeLaunchOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var channelName = DevelopmentChannel;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!string.Equals(argument, "--channel", StringComparison.Ordinal))
            {
                throw new ArgumentException("Unsupported Bridge launch argument.");
            }

            if (index == args.Length - 1)
            {
                throw new ArgumentException("Bridge channel value is required.");
            }

            channelName = args[++index].Trim().ToLowerInvariant();
        }

        if (!string.Equals(channelName, DevelopmentChannel, StringComparison.Ordinal))
        {
            throw new ArgumentException("This Bridge build only allows the development channel.");
        }

        return new BridgeLaunchOptions(channelName);
    }
}
