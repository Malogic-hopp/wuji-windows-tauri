namespace QuantifiedSelf.Windows.Tests.TestHelpers;

/// <summary>
/// Test-only scheduler that captures the refresh callback and allows
/// manual triggering without real time delays.
/// </summary>
public sealed class FakeRefreshScheduler : QuantifiedSelf.Windows.Core.Runtime.IRefreshScheduler
{
    private Func<CancellationToken, Task>? _callback;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }
    public TimeSpan CurrentInterval { get; private set; }

    /// <summary>
    /// Number of times Start has been called.
    /// </summary>
    public int StartCount { get; private set; }

    /// <summary>
    /// Number of times Stop has been called.
    /// </summary>
    public int StopCount { get; private set; }

    public void Start(TimeSpan interval, Func<CancellationToken, Task> callback)
    {
        _callback = callback;
        _cts = new CancellationTokenSource();
        CurrentInterval = interval;
        IsRunning = true;
        StartCount++;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        IsRunning = false;
        StopCount++;
    }

    public void UpdateInterval(TimeSpan newInterval)
    {
        CurrentInterval = newInterval;
    }

    /// <summary>
    /// Manually trigger one refresh tick. Returns the task so tests can await completion.
    /// </summary>
    public async Task TriggerAsync()
    {
        if (_callback is null || _cts is null)
            throw new InvalidOperationException("Scheduler is not started.");

        await _callback(_cts.Token);
    }

    /// <summary>
    /// Trigger and discard the result (fire-and-forget for tests that don't need to await).
    /// </summary>
    public void Trigger()
    {
        if (_callback is null || _cts is null)
            throw new InvalidOperationException("Scheduler is not started.");

        _ = _callback(_cts.Token);
    }
}
