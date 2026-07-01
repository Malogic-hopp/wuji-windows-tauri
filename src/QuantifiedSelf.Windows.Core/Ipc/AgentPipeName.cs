using System.Security.Cryptography;
using System.Text;

namespace QuantifiedSelf.Windows.Core.Ipc;

public sealed class AgentPipeName
{
    private const string PipePrefix = "QuantifiedSelf.Windows.Agent";

    public string FullPipeName { get; }
    public string DisplayPipeName { get; }
    public string SidHash { get; }

    public AgentPipeName(string userSid)
    {
        if (string.IsNullOrWhiteSpace(userSid))
        {
            throw new ArgumentException("User SID must not be empty.", nameof(userSid));
        }

        SidHash = ComputeSha256Hex(userSid);
        FullPipeName = $"{PipePrefix}.{SidHash}";
        DisplayPipeName = $"{PipePrefix}.{SidHash[..Math.Min(12, SidHash.Length)]}";
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
