namespace QuantifiedSelf.Windows.App.ViewModels;

public sealed class GeneralSettingsViewModel(SettingsViewModel owner)
{
    public string RefreshIntervalSecondsText { get => owner.RefreshIntervalSecondsText; set => owner.RefreshIntervalSecondsText = value; }
    public bool AutoStartAgentWhenAppStarts { get => owner.AutoStartAgentWhenAppStarts; set => owner.AutoStartAgentWhenAppStarts = value; }
    public bool StartAppOnWindowsLogin { get => owner.StartAppOnWindowsLogin; set => owner.StartAppOnWindowsLogin = value; }
}

public sealed class RecordingSettingsViewModel(SettingsViewModel owner)
{
    public string SamplingIntervalSecondsText { get => owner.SamplingIntervalSecondsText; set => owner.SamplingIntervalSecondsText = value; }
    public string IdleThresholdSecondsText { get => owner.IdleThresholdSecondsText; set => owner.IdleThresholdSecondsText = value; }
    public string RetentionDaysText { get => owner.RetentionDaysText; set => owner.RetentionDaysText = value; }
    public bool MaskWindowTitles { get => owner.MaskWindowTitles; set => owner.MaskWindowTitles = value; }
}

public sealed class NotificationSettingsViewModel
{
    public string AvailabilityText => "当前版本仅使用系统托盘状态，不发送云端或营销通知。";
}

public sealed class AppearanceSettingsViewModel(SettingsViewModel owner)
{
    public IReadOnlyList<ThemeChoice> ThemeOptions => owner.ThemeOptions;
    public string SelectedTheme { get => owner.SelectedTheme; set => owner.SelectedTheme = value; }
}

public sealed class AdvancedSettingsViewModel(SettingsViewModel owner)
{
    public string HeartbeatIntervalSecondsText { get => owner.HeartbeatIntervalSecondsText; set => owner.HeartbeatIntervalSecondsText = value; }
    public string StaleThresholdSecondsText { get => owner.StaleThresholdSecondsText; set => owner.StaleThresholdSecondsText = value; }
    public bool EnableJsonlJournal { get => owner.EnableJsonlJournal; set => owner.EnableJsonlJournal = value; }
    public bool EnableAgentEventJournal { get => owner.EnableAgentEventJournal; set => owner.EnableAgentEventJournal = value; }
}
