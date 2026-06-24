using System.Text.Json;
using QuantifiedSelf.Windows.Core.Options;
using QuantifiedSelf.Windows.Core.Serialization;

namespace QuantifiedSelf.Windows.Infrastructure.Settings;

public sealed class WindowsAgentOptionsStore
{
    public async Task WriteAsync(
        string path,
        WindowsAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, options, JsonSerializationOptions.Default, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public async Task WriteWithBackupAsync(
        string path,
        WindowsAgentOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, options, JsonSerializationOptions.Default, cancellationToken);
        }

        try
        {
            var backupPath = path + ".bak";
            if (File.Exists(path))
            {
                File.Copy(path, backupPath, overwrite: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best effort cleanup.
                }
            }

            throw;
        }
    }

    public async Task RestoreBackupAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var backupPath = path + ".bak";
        if (!File.Exists(backupPath))
        {
            throw new InvalidOperationException("No backup file is available to restore.");
        }

        WindowsAgentOptions? backupOptions;
        await using (var stream = File.OpenRead(backupPath))
        {
            backupOptions = await JsonSerializer.DeserializeAsync<WindowsAgentOptions>(
                stream,
                JsonSerializationOptions.Default,
                cancellationToken);
        }

        if (backupOptions is null)
        {
            throw new InvalidOperationException("Backup file is empty or invalid.");
        }

        await WriteWithBackupAsync(path, backupOptions, cancellationToken);
    }

    public async Task<WindowsAgentOptions?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WindowsAgentOptions>(
            stream,
            JsonSerializationOptions.Default,
            cancellationToken);
    }
}
