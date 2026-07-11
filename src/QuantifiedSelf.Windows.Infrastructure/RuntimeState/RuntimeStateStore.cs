using System.IO;
using System.Text.Json;
using CoreRuntimeState = QuantifiedSelf.Windows.Core.Runtime.RuntimeState;
using QuantifiedSelf.Windows.Core.Serialization;

namespace QuantifiedSelf.Windows.Infrastructure.RuntimeState;

public class RuntimeStateStore
{
    public virtual async Task WriteAsync(
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

        await MoveWithRetryAsync(tempPath, path);
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

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        return await JsonSerializer.DeserializeAsync<CoreRuntimeState>(
            stream,
            JsonSerializationOptions.Default,
            cancellationToken);
    }

    private static async Task MoveWithRetryAsync(string tempPath, string targetPath)
    {
        const int maxRetries = 3;
        const int delayMs = 50;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                // Use Delete + Move instead of Move(overwrite: true).
                // MoveFileEx with MOVEFILE_REPLACE_EXISTING can fail even when
                // open read handles grant FileShare.Delete. File.Delete + Move
                // handles this correctly because DeleteFileW removes the
                // directory entry while the open handle keeps the old file data
                // alive, allowing a new file to be created at the same path.
                File.Delete(targetPath);
                File.Move(tempPath, targetPath);
                return;
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                await Task.Delay(delayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < maxRetries - 1)
            {
                await Task.Delay(delayMs);
            }
        }

        // Last attempt — let it throw if it still fails
        File.Delete(targetPath);
        File.Move(tempPath, targetPath);
    }
}
