namespace QuantifiedSelf.Windows.App.Services;

public sealed class RefreshHealthSnapshot
{
    // ── Status refresh health ──
    public DateTime? LastStatusRefreshSuccessUtc { get; set; }
    public DateTime? LastStatusRefreshErrorUtc { get; set; }
    public string? LastStatusRefreshError { get; set; }
    public int SkippedStatusRefreshCount { get; set; }
    public bool IsStatusRefreshing { get; set; }

    // ── Page refresh health ──
    public DateTime? LastPageRefreshSuccessUtc { get; set; }
    public DateTime? LastPageRefreshErrorUtc { get; set; }
    public string? LastPageRefreshError { get; set; }
    public int SkippedPageRefreshCount { get; set; }
    public bool IsPageRefreshing { get; set; }
    public string? LastPageRefreshPage { get; set; }

    // ── Compat (summary / old callers) ──
    public DateTime? LastRefreshSuccessUtc
    {
        get => LastStatusRefreshSuccessUtc ?? LastPageRefreshSuccessUtc;
        set => LastStatusRefreshSuccessUtc = value;
    }
    public DateTime? LastRefreshErrorUtc
    {
        get => LastStatusRefreshErrorUtc ?? LastPageRefreshErrorUtc;
        set => LastStatusRefreshErrorUtc = value;
    }
    public string? LastRefreshError
    {
        get => LastStatusRefreshError ?? LastPageRefreshError;
        set => LastStatusRefreshError = value;
    }
    public int SkippedRefreshCount
    {
        get => SkippedStatusRefreshCount + SkippedPageRefreshCount;
        set => SkippedStatusRefreshCount = value;
    }
    public bool IsRefreshing
    {
        get => IsStatusRefreshing || IsPageRefreshing;
        set => IsStatusRefreshing = value;
    }
    public string StatusText { get; set; } = "Ready";

    // ── Status refresh ──
    public void RecordStatusSuccess()
    {
        LastStatusRefreshSuccessUtc = DateTime.UtcNow;
        LastStatusRefreshErrorUtc = null;
        LastStatusRefreshError = null;
        IsStatusRefreshing = false;
        StatusText = "Refresh succeeded.";
    }

    public void RecordStatusError(string safeError)
    {
        LastStatusRefreshErrorUtc = DateTime.UtcNow;
        LastStatusRefreshError = safeError;
        IsStatusRefreshing = false;
        StatusText = "Refresh failed.";
    }

    public void RecordStatusSkipped()
    {
        SkippedStatusRefreshCount++;
    }

    // ── Page refresh ──
    public void RecordPageSuccess(string currentPage)
    {
        LastPageRefreshSuccessUtc = DateTime.UtcNow;
        LastPageRefreshErrorUtc = null;
        LastPageRefreshError = null;
        IsPageRefreshing = false;
        LastPageRefreshPage = currentPage;
    }

    public void RecordPageError(string safeError)
    {
        LastPageRefreshErrorUtc = DateTime.UtcNow;
        LastPageRefreshError = safeError;
        IsPageRefreshing = false;
    }

    public void RecordPageSkipped()
    {
        SkippedPageRefreshCount++;
    }

    // ── Legacy compat ──
    public void RecordSuccess() => RecordStatusSuccess();
    public void RecordError(string safeError) => RecordStatusError(safeError);
    public void RecordSkipped() => RecordStatusSkipped();
}
