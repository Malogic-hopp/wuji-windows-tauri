using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Agent;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Core.Runtime;
using QuantifiedSelf.Windows.Infrastructure.Control;
using QuantifiedSelf.Windows.Infrastructure.Settings;
using CoreRuntimeState = QuantifiedSelf.Windows.Core.Runtime.RuntimeState;

namespace QuantifiedSelf.Windows.Infrastructure.RuntimeState;

public sealed class FileAgentStateAdapter :
    IAgentRuntimeStateReader,
    IAgentHealthStateReader,
    IAgentControlFallback,
    IAgentOptionsReader
{
    private readonly WindowsAgentPaths _paths;
    private readonly RuntimeStateStore _runtimeStateStore;
    private readonly AgentHealthStateStore _healthStateStore;
    private readonly AgentControlFileStore _controlFileStore;
    private readonly WindowsAgentOptionsStore _optionsStore;

    public FileAgentStateAdapter(
        WindowsAgentPaths paths,
        RuntimeStateStore? runtimeStateStore = null,
        AgentHealthStateStore? healthStateStore = null,
        AgentControlFileStore? controlFileStore = null,
        WindowsAgentOptionsStore? optionsStore = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _runtimeStateStore = runtimeStateStore ?? new RuntimeStateStore();
        _healthStateStore = healthStateStore ?? new AgentHealthStateStore();
        _controlFileStore = controlFileStore ?? new AgentControlFileStore();
        _optionsStore = optionsStore ?? new WindowsAgentOptionsStore();
    }

    public Task<CoreRuntimeState?> ReadRuntimeStateAsync(CancellationToken cancellationToken = default) =>
        _runtimeStateStore.ReadAsync(_paths.RuntimeStatePath, cancellationToken);

    public Task<AgentHealthState?> ReadHealthStateAsync(CancellationToken cancellationToken = default) =>
        _healthStateStore.ReadAsync(_paths.HealthStatePath, cancellationToken);

    public Task WriteControlCommandAsync(
        AgentControlCommand command,
        CancellationToken cancellationToken = default) =>
        _controlFileStore.WriteAsync(_paths.AgentControlPath, command, cancellationToken);

    public Task<AgentControlFileReadResult> ReadCurrentCommandAsync(
        CancellationToken cancellationToken = default) =>
        _controlFileStore.PeekAsync(_paths.AgentControlPath, cancellationToken);

    public async Task<WindowsAgentOptions> ReadAgentOptionsAsync(CancellationToken cancellationToken = default) =>
        await _optionsStore.ReadAsync(_paths.AgentOptionsPath, cancellationToken) ?? new WindowsAgentOptions();
}
