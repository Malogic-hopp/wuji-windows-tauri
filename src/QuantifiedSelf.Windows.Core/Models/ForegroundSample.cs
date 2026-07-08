namespace QuantifiedSelf.Windows.Core.Models;

public sealed class ForegroundSample
{
    public long Id { get; set; }

    public DateTime SampleTimeUtc { get; set; }

    public string ProcessName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? WindowTitle { get; set; }

    public string? ExecutablePath { get; set; }

    public int IdleSeconds { get; set; }

    public string ActivityState { get; set; } = "Active";

    /// <summary>
    /// Transient context classification set by analysis services (Development, Communication, etc.).
    /// Not persisted to SQLite.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Context { get; set; } = string.Empty;
}
