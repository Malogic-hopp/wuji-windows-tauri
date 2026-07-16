using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.Agent.Services;

public sealed class ForegroundSamplePrivacyDecision
{
    public bool ShouldWriteSample { get; init; }

    public bool ShouldCloseOpenSession { get; init; }

    public string? Reason { get; init; }

    public ForegroundSample? Sample { get; init; }
}
