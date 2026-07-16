using System.Text;
using System.Text.Json;

namespace QuantifiedSelf.Windows.Client.Bridge.Tests;

[Trait("Category", "Integration")]
public sealed class BridgeProgramIntegrationTests
{
    [Fact]
    public async Task RealClientComposition_CompletesHelloAndShutdownOnDevelopmentChannel()
    {
        var inputText = string.Join(
            '\n',
            Request("hello-real", BridgeProtocol.HelloMethod),
            Request("shutdown-real", BridgeProtocol.ShutdownMethod)) + "\n";
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(inputText));
        await using var output = new MemoryStream();
        using var error = new StringWriter();

        var exitCode = await BridgeProgram.RunAsync(
            ["--channel", "dev"],
            input,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        var responses = Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, responses.Length);
        Assert.Equal("1.0", JsonDocument.Parse(responses[0])
            .RootElement
            .GetProperty("result")
            .GetProperty("apiVersion")
            .GetString());
        Assert.True(JsonDocument.Parse(responses[1])
            .RootElement
            .GetProperty("result")
            .GetProperty("accepted")
            .GetBoolean());
    }

    private static string Request(string id, string method)
    {
        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = new { },
            meta = new
            {
                apiVersion = "1.0",
                correlationId = $"corr-{id}"
            }
        });
    }
}
