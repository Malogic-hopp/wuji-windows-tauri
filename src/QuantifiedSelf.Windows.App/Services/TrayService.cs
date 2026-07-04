using System.Windows.Threading;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class TrayService : ITrayStateSink, IDisposable
{
    private readonly ITrayIconAdapter _adapter;
    private readonly Dispatcher _dispatcher;
    private readonly Action _showMainWindow;
    private readonly Action _exitApp;
    private readonly Action? _startAgent;
    private readonly Action? _pauseAgent;
    private readonly Action? _resumeAgent;
    private readonly Action? _stopAgent;
    private bool _disposed;

    public TrayService(
        ITrayIconAdapter adapter,
        Dispatcher dispatcher,
        Action showMainWindow,
        Action exitApp,
        TrayMenuState? initialState = null,
        Action? startAgent = null,
        Action? pauseAgent = null,
        Action? resumeAgent = null,
        Action? stopAgent = null)
    {
        _adapter = adapter;
        _dispatcher = dispatcher;
        _showMainWindow = showMainWindow;
        _exitApp = exitApp;
        _startAgent = startAgent;
        _pauseAgent = pauseAgent;
        _resumeAgent = resumeAgent;
        _stopAgent = stopAgent;

        _adapter.DoubleClick += OnShowRequested;
        _adapter.ShowMainWindowRequested += OnShowRequested;
        _adapter.ExitAppRequested += OnExitRequested;
        _adapter.StartRequested += OnStartRequested;
        _adapter.PauseRequested += OnPauseRequested;
        _adapter.ResumeRequested += OnResumeRequested;
        _adapter.StopRequested += OnStopRequested;

        var state = initialState ?? new TrayMenuState();
        _adapter.UpdateMenuState(state);
        _adapter.Visible = true;
    }

    void ITrayStateSink.UpdateState(TrayMenuState state)
    {
        if (_disposed) return;
        TryPostToDispatcher(() => _adapter.UpdateMenuState(state));
    }

    public void UpdateMenuState(TrayMenuState state) => ((ITrayStateSink)this).UpdateState(state);

    private void OnShowRequested(object? sender, EventArgs e) => TryPostToDispatcher(_showMainWindow);
    private void OnExitRequested(object? sender, EventArgs e) => TryPostToDispatcher(_exitApp);
    private void OnStartRequested(object? sender, EventArgs e) { if (_startAgent is not null) TryPostToDispatcher(_startAgent); }
    private void OnPauseRequested(object? sender, EventArgs e) { if (_pauseAgent is not null) TryPostToDispatcher(_pauseAgent); }
    private void OnResumeRequested(object? sender, EventArgs e) { if (_resumeAgent is not null) TryPostToDispatcher(_resumeAgent); }
    private void OnStopRequested(object? sender, EventArgs e) { if (_stopAgent is not null) TryPostToDispatcher(_stopAgent); }

    private void TryPostToDispatcher(Action action)
    {
        try
        {
            if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
            if (_dispatcher.CheckAccess())
                action();
            else
                _dispatcher.Invoke(action);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _adapter.DoubleClick -= OnShowRequested;
        _adapter.ShowMainWindowRequested -= OnShowRequested;
        _adapter.ExitAppRequested -= OnExitRequested;
        _adapter.StartRequested -= OnStartRequested;
        _adapter.PauseRequested -= OnPauseRequested;
        _adapter.ResumeRequested -= OnResumeRequested;
        _adapter.StopRequested -= OnStopRequested;
        _adapter.Visible = false;
        _adapter.Dispose();
    }
}
