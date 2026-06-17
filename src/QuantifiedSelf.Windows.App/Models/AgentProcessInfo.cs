namespace QuantifiedSelf.Windows.App.Models;

public sealed class AgentProcessInfo
{
    public int? ProcessId { get; init; }

    public bool IsRunning { get; init; }

    public DateTime? StartedAtUtc { get; init; }

    public string? Version { get; init; }

    public string? MachineName { get; init; }

    public string? UserName { get; init; }
}
