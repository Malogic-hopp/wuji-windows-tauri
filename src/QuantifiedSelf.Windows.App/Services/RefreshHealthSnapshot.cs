namespace QuantifiedSelf.Windows.App.Services;

public sealed class RefreshHealthSnapshot
{
    public DateTime? LastRefreshSuccessUtc { get; set; }
    public DateTime? LastRefreshErrorUtc { get; set; }
    public string? LastRefreshError { get; set; }
    public int SkippedRefreshCount { get; set; }
    public bool IsRefreshing { get; set; }
    public string StatusText { get; set; } = "Ready";

    public void RecordSuccess()
    {
        LastRefreshSuccessUtc = DateTime.UtcNow;
        LastRefreshErrorUtc = null;
        LastRefreshError = null;
        IsRefreshing = false;
        StatusText = "Refresh succeeded.";
    }

    public void RecordError(string safeError)
    {
        LastRefreshErrorUtc = DateTime.UtcNow;
        LastRefreshError = safeError;
        IsRefreshing = false;
        StatusText = "Refresh failed.";
    }

    public void RecordSkipped()
    {
        SkippedRefreshCount++;
    }
}
