using QuantifiedSelf.Windows.Core.Options;

namespace QuantifiedSelf.Windows.ApplicationLayer.Abstractions.Settings;

public interface IAppSettingsStore
{
    Task<AppSettings?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IAgentOptionsStore
{
    Task<WindowsAgentOptions?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(WindowsAgentOptions options, CancellationToken cancellationToken = default);

    Task WriteWithBackupAsync(WindowsAgentOptions options, CancellationToken cancellationToken = default);

    Task RestoreBackupAsync(CancellationToken cancellationToken = default);
}
