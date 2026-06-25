namespace QuantifiedSelf.Windows.Core.Maintenance;

public sealed class ClearHistoryResult
{
    public int ForegroundSamplesDeleted { get; init; }
    public int SessionsDeleted { get; init; }
    public int AgentEventsDeleted { get; init; }
    public int JsonlFilesDeleted { get; init; }
    public int JsonlDeleteErrorCount { get; init; }
    public bool Success { get; init; }
    public bool SqliteCleared { get; init; }
    public string? ErrorCode { get; init; }
    public string? SafeMessage { get; init; }

    public static ClearHistoryResult Ok(
        int foregroundSamplesDeleted,
        int sessionsDeleted,
        int agentEventsDeleted,
        int jsonlFilesDeleted,
        int jsonlDeleteErrorCount = 0)
    {
        return new ClearHistoryResult
        {
            ForegroundSamplesDeleted = foregroundSamplesDeleted,
            SessionsDeleted = sessionsDeleted,
            AgentEventsDeleted = agentEventsDeleted,
            JsonlFilesDeleted = jsonlFilesDeleted,
            JsonlDeleteErrorCount = jsonlDeleteErrorCount,
            Success = true,
            SqliteCleared = true,
            ErrorCode = jsonlDeleteErrorCount > 0 ? "JsonlDeletePartial" : null,
            SafeMessage = jsonlDeleteErrorCount > 0
                ? $"{jsonlDeleteErrorCount} JSONL file(s) could not be deleted."
                : null
        };
    }

    public static ClearHistoryResult Failed(string errorCode, string safeMessage)
    {
        return new ClearHistoryResult
        {
            Success = false,
            ErrorCode = errorCode,
            SafeMessage = safeMessage
        };
    }
}
