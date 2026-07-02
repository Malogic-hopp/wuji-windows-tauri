namespace QuantifiedSelf.Windows.App.Services;

public sealed class RefreshOptions
{
    /// <summary>
    /// Status polling interval for future phased polling (not used in 9.1).
    /// </summary>
    public TimeSpan StatusPollingInterval { get; set; } = TimeSpan.FromSeconds(2);
}
