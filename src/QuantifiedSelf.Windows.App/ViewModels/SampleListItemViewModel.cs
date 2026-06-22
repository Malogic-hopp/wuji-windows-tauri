using QuantifiedSelf.Windows.Core.Models;

namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed class SampleListItemViewModel
{
    public SampleListItemViewModel(ForegroundSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        SampleId = sample.Id;
        SampleTimeUtc = sample.SampleTimeUtc;
        LocalTime = sample.SampleTimeUtc.ToLocalTime();
        ProcessName = sample.ProcessName;
        DisplayName = string.IsNullOrWhiteSpace(sample.DisplayName)
            ? string.IsNullOrWhiteSpace(sample.ProcessName) ? "Unknown" : sample.ProcessName
            : sample.DisplayName;
        WindowTitleText = string.IsNullOrWhiteSpace(sample.WindowTitle) ? "[Hidden]" : sample.WindowTitle;
        IdleSeconds = sample.IdleSeconds;
        ActivityState = string.IsNullOrWhiteSpace(sample.ActivityState) ? "Unknown" : sample.ActivityState;
    }

    public long SampleId { get; }

    public DateTime SampleTimeUtc { get; }

    public DateTime LocalTime { get; }

    public string ProcessName { get; }

    public string DisplayName { get; }

    public string WindowTitleText { get; }

    public int IdleSeconds { get; }

    public string ActivityState { get; }
}
