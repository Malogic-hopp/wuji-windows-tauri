namespace QuantifiedSelf.Windows.Core.Maintenance;

public sealed class PruneDataResult
{
    public int ForegroundSamplesDeleted { get; init; }
    public int SessionsDeleted { get; init; }
    public int AgentEventsDeleted { get; init; }
    public int JsonlFilesDeleted { get; init; }
    public int JsonlDeleteErrorCount { get; init; }
    public DateTime CutoffUtc { get; init; }
    public DateOnly CutoffLocalDate { get; init; }
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? SafeMessage { get; init; }
    public string? SafeDetail { get; init; }

    public static PruneDataResult Ok(
        int foregroundSamplesDeleted,
        int sessionsDeleted,
        int agentEventsDeleted,
        int jsonlFilesDeleted,
        DateTime cutoffUtc,
        DateOnly cutoffLocalDate,
        int jsonlDeleteErrorCount = 0)
    {
        return new PruneDataResult
        {
            ForegroundSamplesDeleted = foregroundSamplesDeleted,
            SessionsDeleted = sessionsDeleted,
            AgentEventsDeleted = agentEventsDeleted,
            JsonlFilesDeleted = jsonlFilesDeleted,
            CutoffUtc = cutoffUtc,
            CutoffLocalDate = cutoffLocalDate,
            JsonlDeleteErrorCount = jsonlDeleteErrorCount,
            Success = jsonlDeleteErrorCount == 0,
            ErrorCode = jsonlDeleteErrorCount > 0 ? "JsonlDeletePartial" : null,
            SafeMessage = jsonlDeleteErrorCount > 0
                ? $"{jsonlDeleteErrorCount} JSONL file(s) could not be deleted."
                : null
        };
    }

    public static PruneDataResult Failed(
        string errorCode,
        string safeMessage,
        DateTime cutoffUtc = default,
        DateOnly cutoffLocalDate = default,
        string? safeDetail = null)
    {
        return new PruneDataResult
        {
            Success = false,
            ErrorCode = errorCode,
            SafeMessage = safeMessage,
            SafeDetail = safeDetail,
            CutoffUtc = cutoffUtc,
            CutoffLocalDate = cutoffLocalDate
        };
    }
}
