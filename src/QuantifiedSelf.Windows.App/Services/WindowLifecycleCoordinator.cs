namespace QuantifiedSelf.Windows.App.Services;

/// <summary>
/// Testable helper that encapsulates CloseToTray / MinimizeToTray logic.
/// Kept minimal — the actual WPF events remain in App.xaml.cs wiring.
/// </summary>
public sealed class WindowLifecycleCoordinator
{
    private bool _isExitRequested;

    public bool IsExitRequested => _isExitRequested;

    public bool ShouldCancelClose(bool closeToTray)
    {
        return closeToTray && !_isExitRequested;
    }

    public bool ShouldHideOnMinimize(bool minimizeToTray)
    {
        return minimizeToTray && !_isExitRequested;
    }

    public void RequestExit()
    {
        _isExitRequested = true;
    }
}
