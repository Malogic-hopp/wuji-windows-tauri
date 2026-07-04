namespace QuantifiedSelf.Windows.Core.Options;

public sealed class AppSettings
{
    public bool AutoStartAgentWhenAppStarts { get; set; }

    public bool StartAppOnWindowsLogin { get; set; }

    public bool MinimizeToTray { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    public int RefreshIntervalSeconds { get; set; } = 15;

    public string Theme { get; set; } = "Light";

    public string LastSelectedPage { get; set; } = "Dashboard";
}
