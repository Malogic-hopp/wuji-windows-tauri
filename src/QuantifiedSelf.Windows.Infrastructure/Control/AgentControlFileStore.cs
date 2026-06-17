using System.Text.Json;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Serialization;

namespace QuantifiedSelf.Windows.Infrastructure.Control;

public sealed class AgentControlFileStore
{
    public async Task WriteAsync(
        string path,
        AgentControlCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(command);

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
                command,
                JsonSerializationOptions.Default,
                cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public async Task<AgentControlFileReadResult> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new AgentControlFileReadResult();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var command = await JsonSerializer.DeserializeAsync<AgentControlCommand>(
                stream,
                JsonSerializationOptions.Default,
                cancellationToken);

            return new AgentControlFileReadResult
            {
                Command = command
            };
        }
        catch (JsonException ex)
        {
            var badPath = path + ".bad";
            try
            {
                File.Move(path, badPath, overwrite: true);
            }
            catch
            {
                // The malformed file should never block the agent from continuing.
            }

            return new AgentControlFileReadResult
            {
                WasMalformed = true,
                ErrorMessage = ex.Message
            };
        }
    }
}
