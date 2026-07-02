using QuantifiedSelf.Windows.App.Models;
using QuantifiedSelf.Windows.Core.Control;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class AgentCommandAvailability
{
    public bool CanStart { get; init; }
    public bool CanStop { get; init; }
    public bool CanPause { get; init; }
    public bool CanResume { get; init; }
    public bool CanReloadConfigNow { get; init; }
    public bool CanSaveAndReloadAgentOptions { get; init; }
    public bool CanPruneData { get; init; }
    public bool CanClearHistory { get; init; }
    public bool IsMaintenance { get; init; }
    public string ReloadConfigStatusText { get; init; } = "";

    public static AgentCommandAvailability FromStatus(AgentStatusSnapshot status)
    {
        var state = status.ActualState;
        var isMaintenance = state == AgentActualState.Maintenance;
        var isRunningOrPaused = state is AgentActualState.Running or AgentActualState.Paused;

        return new AgentCommandAvailability
        {
            CanStart = (state is AgentActualState.NotRunning or AgentActualState.Stopped) && !isMaintenance,
            CanStop = (state is AgentActualState.Running or AgentActualState.Paused or AgentActualState.Stale) && !isMaintenance,
            CanPause = state == AgentActualState.Running && !isMaintenance,
            CanResume = state == AgentActualState.Paused && !isMaintenance,
            CanReloadConfigNow = isRunningOrPaused && !isMaintenance,
            CanSaveAndReloadAgentOptions = isRunningOrPaused && !isMaintenance,
            CanPruneData = isRunningOrPaused && !isMaintenance,
            CanClearHistory = isRunningOrPaused && !isMaintenance,
            IsMaintenance = isMaintenance,
            ReloadConfigStatusText = isRunningOrPaused && !isMaintenance
                ? "Agent is running. You can apply the saved configuration with Reload Config."
                : isMaintenance
                    ? "Agent is performing maintenance. Reload is temporarily unavailable."
                    : "Agent is not running. Saved configuration will take effect on next Agent start."
        };
    }
}
