namespace QuantifiedSelf.Windows.App.Services;

public interface ITrayIconAdapter : IDisposable
{
    bool Visible { get; set; }
    string TooltipText { get; set; }
    event EventHandler? DoubleClick;
    event EventHandler? ShowMainWindowRequested;
    event EventHandler? ExitAppRequested;
    event EventHandler? StartRequested;
    event EventHandler? PauseRequested;
    event EventHandler? ResumeRequested;
    event EventHandler? StopRequested;
    void UpdateMenuState(TrayMenuState state);
    void SetMenuItemEnabled(string key, bool enabled);
}
