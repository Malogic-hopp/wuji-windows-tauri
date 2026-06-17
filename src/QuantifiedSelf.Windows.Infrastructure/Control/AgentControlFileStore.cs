using System.Text.Json;
using QuantifiedSelf.Windows.Core.Control;
using QuantifiedSelf.Windows.Core.Serialization;

namespace QuantifiedSelf.Windows.Infrastructure.Control;

public sealed class AgentControlFileStore
{
    public Task WriteAsync(
        string path,
        AgentControlCommand command,
        CancellationToken cancellationToken = default)
        => WriteInternalAsync(path, command, cancellationToken);

    public Task<AgentControlFileReadResult> ReadForAgentAsync(
        string path,
        CancellationToken cancellationToken = default)
        => ReadInternalAsync(path, mutateMalformedFile: true, cancellationToken);

    public Task<AgentControlFileReadResult> PeekAsync(
        string path,
        CancellationToken cancellationToken = default)
        => ReadInternalAsync(path, mutateMalformedFile: false, cancellationToken);

    private async Task WriteInternalAsync(
        string path,
        AgentControlCommand command,
        CancellationToken cancellationToken)
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

    private async Task<AgentControlFileReadResult> ReadInternalAsync(
        string path,
        bool mutateMalformedFile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new AgentControlFileReadResult();
        }

        var rawText = await File.ReadAllTextAsync(path, cancellationToken);

        try
        {
            var command = JsonSerializer.Deserialize<AgentControlCommand>(
                rawText,
                JsonSerializationOptions.Default);

            return new AgentControlFileReadResult
            {
                Command = command,
                RawText = rawText
            };
        }
        catch (JsonException ex)
        {
            if (mutateMalformedFile)
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
            }

            return new AgentControlFileReadResult
            {
                WasMalformed = true,
                ErrorMessage = ex.Message,
                RawText = rawText
            };
        }
    }
}
