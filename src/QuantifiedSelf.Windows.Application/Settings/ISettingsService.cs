using QuantifiedSelf.Windows.Core.Options;

namespace QuantifiedSelf.Windows.ApplicationLayer.Settings;

public interface ISettingsService
{
    Task<ClientSettingsSnapshot> GetClientSettingsAsync(CancellationToken cancellationToken = default);

    ClientSettingsSnapshot GetDefaultClientSettings();

    Task<ClientSettingsUpdateResult> UpdateClientSettingsAsync(
        ClientSettingsSnapshot settings,
        CancellationToken cancellationToken = default);

    Task<AppSettings> ReadAppSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveAppSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default);

    Task<WindowsAgentOptions> ReadAgentOptionsAsync(CancellationToken cancellationToken = default);

    Task SaveAgentOptionsAsync(WindowsAgentOptions options, CancellationToken cancellationToken = default);

    Task SaveAgentOptionsWithBackupAsync(
        WindowsAgentOptions options,
        CancellationToken cancellationToken = default);

    Task RestoreAgentOptionsBackupAsync(CancellationToken cancellationToken = default);
}
