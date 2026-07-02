using QuantifiedSelf.Windows.App.Models;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class RefreshResult
{
    public long RefreshSequence { get; init; }
    public DateTime StartedAtUtc { get; init; }
    public DateTime CompletedAtUtc { get; init; }
    public AgentStatusSnapshot Status { get; init; } = null!;
    public AgentProcessInfo? ProcessInfo { get; init; }
    public RefreshHealthSnapshot Health { get; init; } = null!;
    public string CurrentPage { get; init; } = string.Empty;
    public string StatusSource { get; init; } = "Unknown";
    public bool PageDataRefreshed { get; init; }
    public bool PageRefreshSkipped { get; init; }
}
