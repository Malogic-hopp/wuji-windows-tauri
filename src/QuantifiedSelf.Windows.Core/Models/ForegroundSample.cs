namespace QuantifiedSelf.Windows.Core.Models;

public sealed class ForegroundSample
{
    public long Id { get; set; }

    public DateTime SampleTimeUtc { get; set; }

    public string ProcessName { get; set; } = string.Empty;

    public string? WindowTitle { get; set; }

    public string? ExecutablePath { get; set; }

    public int IdleSeconds { get; set; }

    public string ActivityState { get; set; } = "Active";
}
