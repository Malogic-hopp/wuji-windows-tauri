using System.Security.Cryptography;
using System.Text;

using QuantifiedSelf.Windows.Core.Runtime;

namespace QuantifiedSelf.Windows.Core.Ipc;

public sealed class AgentPipeName
{
    private const string PipePrefix = "QuantifiedSelf.Windows.Agent";

    public string FullPipeName { get; }
    public string DisplayPipeName { get; }
    public string SidHash { get; }

    public AgentPipeName(string userSid, string? channelName = null)
    {
        if (string.IsNullOrWhiteSpace(userSid))
        {
            throw new ArgumentException("User SID must not be empty.", nameof(userSid));
        }

        var channel = RuntimeChannel.Parse(channelName);
        SidHash = ComputeSha256Hex(userSid);
        var channelPart = channel.PipeQualifier is null
            ? string.Empty
            : $".{channel.PipeQualifier}";
        FullPipeName = $"{PipePrefix}{channelPart}.{SidHash}";
        DisplayPipeName = $"{PipePrefix}{channelPart}.{SidHash[..Math.Min(12, SidHash.Length)]}";
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
