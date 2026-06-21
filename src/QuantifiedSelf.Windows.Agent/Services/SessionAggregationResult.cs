using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Agent.Services;

public sealed class SessionAggregationResult
{
    public AppSession? StartedSession { get; init; }

    public AppSession? ClosedSession { get; init; }

    public string? CloseReason { get; init; }
}
