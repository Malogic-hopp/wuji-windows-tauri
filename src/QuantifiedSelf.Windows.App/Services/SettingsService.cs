using System.IO;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Paths;
using QuantifiedSelf.Windows.Infrastructure.Settings;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class SettingsService
{
    private readonly WindowsAgentPaths _paths;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly WindowsAgentOptionsStore _agentOptionsStore;

    public SettingsService(
        WindowsAgentPaths paths,
        AppSettingsStore appSettingsStore,
        WindowsAgentOptionsStore agentOptionsStore)
    {
        _paths = paths;
        _appSettingsStore = appSettingsStore;
        _agentOptionsStore = agentOptionsStore;
    }

    public string AppSettingsPath => Path.Combine(_paths.ConfigDir, "app-settings.json");

    public async Task<AppSettings> ReadAppSettingsAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _appSettingsStore.ReadAsync(AppSettingsPath, cancellationToken);
        return settings ?? new AppSettings();
    }

    public Task SaveAppSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return _appSettingsStore.WriteAsync(AppSettingsPath, settings, cancellationToken);
    }

    public async Task<WindowsAgentOptions> ReadAgentOptionsAsync(CancellationToken cancellationToken = default)
    {
        var options = await _agentOptionsStore.ReadAsync(_paths.AgentOptionsPath, cancellationToken);
        return options ?? new WindowsAgentOptions();
    }

    public Task SaveAgentOptionsAsync(WindowsAgentOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _agentOptionsStore.WriteAsync(_paths.AgentOptionsPath, options, cancellationToken);
    }

    public Task SaveAgentOptionsWithBackupAsync(WindowsAgentOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _agentOptionsStore.WriteWithBackupAsync(_paths.AgentOptionsPath, options, cancellationToken);
    }

    public Task RestoreAgentOptionsBackupAsync(CancellationToken cancellationToken = default)
    {
        return _agentOptionsStore.RestoreBackupAsync(_paths.AgentOptionsPath, cancellationToken);
    }
}
