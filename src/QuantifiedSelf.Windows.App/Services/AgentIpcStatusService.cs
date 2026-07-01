using QuantifiedSelf.Windows.Core.Ipc;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class AgentIpcStatusService
{
    public DateTime? LastIpcSuccessUtc { get; private set; }
    public string? LastIpcError { get; private set; }
    public DateTime? LastFallbackUsedUtc { get; private set; }
    public string LastCommandSource { get; private set; } = "Unavailable";
    public string? FullPipeName { get; private set; }
    public string? DisplayPipeName { get; private set; }

    public void Initialize(AgentPipeName pipeName)
    {
        FullPipeName = pipeName.FullPipeName;
        DisplayPipeName = pipeName.DisplayPipeName;
        LastCommandSource = "Unavailable";
    }

    public void RecordIpcSuccess()
    {
        LastIpcSuccessUtc = DateTime.UtcNow;
        LastIpcError = null;
        LastCommandSource = "NamedPipe";
    }

    public void RecordIpcFallback(string? safeError)
    {
        LastFallbackUsedUtc = DateTime.UtcNow;
        LastIpcError = safeError;
        LastCommandSource = "FileFallback";
    }

    /// <summary>
    /// Returns a safe display text for UI. Never exposes full pipe name, SID, or raw exceptions.
    /// </summary>
    public string GetDisplayStatusText()
    {
        return LastCommandSource switch
        {
            "NamedPipe" => "IPC connected.",
            "FileFallback" => "IPC unavailable; using file fallback.",
            _ => "IPC status unknown."
        };
    }
}
