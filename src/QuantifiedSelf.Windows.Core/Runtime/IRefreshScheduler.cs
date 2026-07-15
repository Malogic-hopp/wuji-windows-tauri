namespace QuantifiedSelf.Windows.Core.Runtime;

/// <summary>
/// Abstraction over timer-driven refresh scheduling.
/// Production: DispatcherTimer with configurable interval.
/// Test: Manual trigger without real time delays.
/// </summary>
public interface IRefreshScheduler
{
    /// <summary>
    /// Starts scheduling refresh callbacks at the specified interval.
    /// </summary>
    void Start(TimeSpan interval, Func<CancellationToken, Task> callback);

    /// <summary>
    /// Stops scheduling. No further callbacks will fire.
    /// </summary>
    void Stop();

    /// <summary>
    /// Updates the interval without restarting.
    /// </summary>
    void UpdateInterval(TimeSpan newInterval);
}
