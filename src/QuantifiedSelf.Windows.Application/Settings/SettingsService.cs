using QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Settings;
using QuantifiedSelf.Windows.Core.Options;

namespace QuantifiedSelf.Windows.ApplicationLayer.Settings;

public sealed class SettingsService : ISettingsService
{
    private readonly IAppSettingsStore _appSettingsStore;
    private readonly IAgentOptionsStore _agentOptionsStore;

    public SettingsService(
        IAppSettingsStore appSettingsStore,
        IAgentOptionsStore agentOptionsStore)
    {
        _appSettingsStore = appSettingsStore ?? throw new ArgumentNullException(nameof(appSettingsStore));
        _agentOptionsStore = agentOptionsStore ?? throw new ArgumentNullException(nameof(agentOptionsStore));
    }

    public async Task<AppSettings> ReadAppSettingsAsync(CancellationToken cancellationToken = default)
    {
        return await _appSettingsStore.ReadAsync(cancellationToken) ?? new AppSettings();
    }

    public Task SaveAppSettingsAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return _appSettingsStore.WriteAsync(settings, cancellationToken);
    }

    public async Task<WindowsAgentOptions> ReadAgentOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        return await _agentOptionsStore.ReadAsync(cancellationToken) ?? new WindowsAgentOptions();
    }

    public Task SaveAgentOptionsAsync(
        WindowsAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _agentOptionsStore.WriteAsync(options, cancellationToken);
    }

    public Task SaveAgentOptionsWithBackupAsync(
        WindowsAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _agentOptionsStore.WriteWithBackupAsync(options, cancellationToken);
    }

    public Task RestoreAgentOptionsBackupAsync(CancellationToken cancellationToken = default)
    {
        return _agentOptionsStore.RestoreBackupAsync(cancellationToken);
    }
}
