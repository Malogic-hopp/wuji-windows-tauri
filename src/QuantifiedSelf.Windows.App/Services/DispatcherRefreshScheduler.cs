using System.Windows.Threading;
using QuantifiedSelf.Windows.Core.Runtime;

namespace QuantifiedSelf.Windows.App.Services;

/// <summary>
/// DispatcherTimer-backed refresh scheduler for WPF runtime use.
/// </summary>
public sealed class DispatcherRefreshScheduler : IRefreshScheduler
{
    private DispatcherTimer? _timer;
    private CancellationTokenSource? _cts;
    private Func<CancellationToken, Task>? _callback;
    private TimeSpan _interval = TimeSpan.FromSeconds(15);

    public void Start(TimeSpan interval, Func<CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Stop();

        _interval = interval;
        _callback = callback;
        _cts = new CancellationTokenSource();
        _timer = new DispatcherTimer { Interval = _interval };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Stop()
    {
        if (_timer is not null)
        {
            _timer.Tick -= OnTick;
            _timer.Stop();
            _timer = null;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _callback = null;
    }

    public void UpdateInterval(TimeSpan newInterval)
    {
        _interval = newInterval;
        if (_timer is not null)
        {
            _timer.Interval = newInterval;
        }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_callback is null || _cts is null)
        {
            return;
        }

        try
        {
            await _callback(_cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch
        {
            // ViewModel refresh methods already guard and sanitize their own exceptions.
        }
    }
}
