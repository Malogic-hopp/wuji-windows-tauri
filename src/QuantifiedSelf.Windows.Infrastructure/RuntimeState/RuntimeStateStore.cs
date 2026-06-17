using System.Text.Json;
using CoreRuntimeState = QuantifiedSelf.Windows.Core.Runtime.RuntimeState;
using QuantifiedSelf.Windows.Core.Serialization;

namespace QuantifiedSelf.Windows.Infrastructure.RuntimeState;

public sealed class RuntimeStateStore
{
    public async Task WriteAsync(
        string path,
        CoreRuntimeState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(state);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";

        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                state,
                JsonSerializationOptions.Default,
                cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public async Task<CoreRuntimeState?> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);

        return await JsonSerializer.DeserializeAsync<CoreRuntimeState>(
            stream,
            JsonSerializationOptions.Default,
            cancellationToken);
    }
}
