using System.IO;
using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Settings;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Settings;

namespace QuantifiedSelf.Windows.Client.Settings;

public sealed class WindowsSettingsStoreAdapter : IAppSettingsStore, IAgentOptionsStore
{
    private readonly WindowsAgentPaths _paths;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly WindowsAgentOptionsStore _agentOptionsStore;

    public WindowsSettingsStoreAdapter(
        WindowsAgentPaths paths,
        AppSettingsStore? appSettingsStore = null,
        WindowsAgentOptionsStore? agentOptionsStore = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _appSettingsStore = appSettingsStore ?? new AppSettingsStore();
        _agentOptionsStore = agentOptionsStore ?? new WindowsAgentOptionsStore();
    }

    internal string AppSettingsPath => Path.Combine(_paths.ConfigDir, "app-settings.json");

    internal string AgentOptionsPath => _paths.AgentOptionsPath;

    Task<AppSettings?> IAppSettingsStore.ReadAsync(CancellationToken cancellationToken) =>
        _appSettingsStore.ReadAsync(AppSettingsPath, cancellationToken);

    Task IAppSettingsStore.WriteAsync(AppSettings settings, CancellationToken cancellationToken) =>
        _appSettingsStore.WriteAsync(AppSettingsPath, settings, cancellationToken);

    Task<WindowsAgentOptions?> IAgentOptionsStore.ReadAsync(CancellationToken cancellationToken) =>
        _agentOptionsStore.ReadAsync(AgentOptionsPath, cancellationToken);

    Task IAgentOptionsStore.WriteAsync(
        WindowsAgentOptions options,
        CancellationToken cancellationToken) =>
        _agentOptionsStore.WriteAsync(AgentOptionsPath, options, cancellationToken);

    Task IAgentOptionsStore.WriteWithBackupAsync(
        WindowsAgentOptions options,
        CancellationToken cancellationToken) =>
        _agentOptionsStore.WriteWithBackupAsync(AgentOptionsPath, options, cancellationToken);

    Task IAgentOptionsStore.RestoreBackupAsync(CancellationToken cancellationToken) =>
        _agentOptionsStore.RestoreBackupAsync(AgentOptionsPath, cancellationToken);
}
