using QuantifiedSelf.Windows.Core.Ipc;
using QuantifiedSelf.Windows.Core.Runtime;

namespace QuantifiedSelf.Windows.Core.Tests;

[Trait("Category", "Fast")]
public sealed class CoreContractTests
{
    [Theory]
    [InlineData(null, "prod")]
    [InlineData("stable", "prod")]
    [InlineData("preview", "dev")]
    [InlineData("--DEV", "dev")]
    [InlineData(" custom/channel ", "customchannel")]
    public void RuntimeChannel_NormalizeReturnsStableChannelName(string? value, string expected)
    {
        Assert.Equal(expected, RuntimeChannel.Normalize(value));
    }

    [Fact]
    public void AgentPipeName_SeparatesChannelsWithoutExposingIdentity()
    {
        const string identity = "S-1-5-21-stage-seven";

        var production = new AgentPipeName(identity);
        var development = new AgentPipeName(identity, RuntimeChannel.DevelopmentName);

        Assert.NotEqual(production.FullPipeName, development.FullPipeName);
        Assert.Contains(".dev.", development.FullPipeName, StringComparison.Ordinal);
        Assert.DoesNotContain(identity, production.FullPipeName, StringComparison.Ordinal);
        Assert.Equal(64, production.SidHash.Length);
    }
}
