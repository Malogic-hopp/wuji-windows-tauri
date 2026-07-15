using QuantifiedSelf.Windows.ApplicationLayer.Models;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class TrayMenuState
{
    public string TooltipText { get; init; } = "WUJI";
    public string SafeStatusText { get; init; } = "NotRunning";
    public string IpcStatusText { get; init; } = "unavailable";

    public bool CanShowMainWindow { get; init; } = true;
    public bool CanExitApp { get; init; } = true;
    public bool CanStart { get; init; }
    public bool CanPause { get; init; }
    public bool CanResume { get; init; }
    public bool CanStop { get; init; }
    public bool IsMaintenance { get; init; }

    public static TrayMenuState From(
        AgentStatusSnapshot status,
        AgentCommandAvailability availability,
        string? ipcStatusSource = null)
    {
        var ipcStatus = ipcStatusSource switch
        {
            "NamedPipe" => "NamedPipe",
            "FileFallback" => "FileFallback",
            _ => "unavailable"
        };

        var tooltip = $"WUJI\nAgent: {status.ActualState}\nIPC: {ipcStatus}";

        return new TrayMenuState
        {
            TooltipText = Truncate(tooltip, 63),
            SafeStatusText = status.ActualState.ToString(),
            IpcStatusText = ipcStatus,
            CanShowMainWindow = true,
            CanExitApp = true,
            CanStart = availability.CanStart,
            CanPause = availability.CanPause,
            CanResume = availability.CanResume,
            CanStop = availability.CanStop,
            IsMaintenance = availability.IsMaintenance
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "WUJI";
        if (value.Length <= maxLength) return value;
        return value[..(maxLength - 1)] + "…";
    }
}
