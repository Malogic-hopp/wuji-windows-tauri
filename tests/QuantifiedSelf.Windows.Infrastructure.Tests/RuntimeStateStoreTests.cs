using System.IO;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Infrastructure.RuntimeState;
using QuantifiedSelf.Windows.Tests.TestHelpers;
using AgentRuntimeState = QuantifiedSelf.Windows.Core.Runtime.RuntimeState;

namespace QuantifiedSelf.Windows.Infrastructure.Tests;

[Trait("Category", "Integration")]
public sealed class RuntimeStateStoreTests
{
    [Fact]
    public async Task WriteAndReadAsync_RoundTripsExistingRuntimeFormat()
    {
        using var workspace = new TempWorkspace("wuji-infrastructure-tests");
        var path = Path.Combine(workspace.Root, "runtime", "runtime_state.json");
        var store = new RuntimeStateStore();
        var expected = new AgentRuntimeState
        {
            ProcessId = 42,
            State = AgentActualState.Running,
            Version = "0.1.0",
            MachineName = "test-machine",
            UserName = "test-user"
        };

        await store.WriteAsync(path, expected);
        var actual = await store.ReadAsync(path);

        Assert.NotNull(actual);
        Assert.Equal(expected.ProcessId, actual!.ProcessId);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.MachineName, actual.MachineName);
        Assert.Equal(expected.UserName, actual.UserName);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task ReadAsync_MissingFileReturnsNullWithoutCreatingDirectory()
    {
        using var workspace = new TempWorkspace("wuji-infrastructure-missing-tests");
        var runtimeDirectory = Path.Combine(workspace.Root, "not-created");
        var path = Path.Combine(runtimeDirectory, "runtime_state.json");

        var result = await new RuntimeStateStore().ReadAsync(path);

        Assert.Null(result);
        Assert.False(Directory.Exists(runtimeDirectory));
    }
}
