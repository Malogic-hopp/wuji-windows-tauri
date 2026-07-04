using System.Drawing;
using System.Windows.Forms;

namespace QuantifiedSelf.Windows.App.Services;

public sealed class NotifyIconTrayIconAdapter : ITrayIconAdapter
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Dictionary<string, ToolStripMenuItem> _items = new();
    private bool _disposed;

    public NotifyIconTrayIconAdapter()
    {
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "WUJI",
            Visible = false
        };

        _menu = new ContextMenuStrip();
        AddItem("start", "Start Agent").Enabled = false;
        AddItem("pause", "Pause Agent").Enabled = false;
        AddItem("resume", "Resume Agent").Enabled = false;
        AddItem("stop", "Stop Agent").Enabled = false;
        _menu.Items.Add(new ToolStripSeparator());
        AddItem("show", "Show Main Window").Click += (_, _) => ShowMainWindowRequested?.Invoke(this, EventArgs.Empty);
        AddItem("exit", "Exit App").Click += (_, _) => ExitAppRequested?.Invoke(this, EventArgs.Empty);

        // Wire command clicks via stored references
        if (_items.TryGetValue("start", out var startItem)) startItem.Click += (_, _) => StartRequested?.Invoke(this, EventArgs.Empty);
        if (_items.TryGetValue("pause", out var pauseItem)) pauseItem.Click += (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty);
        if (_items.TryGetValue("resume", out var resumeItem)) resumeItem.Click += (_, _) => ResumeRequested?.Invoke(this, EventArgs.Empty);
        if (_items.TryGetValue("stop", out var stopItem)) stopItem.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);

        _notifyIcon.ContextMenuStrip = _menu;
        _notifyIcon.DoubleClick += (_, _) => DoubleClick?.Invoke(this, EventArgs.Empty);
    }

    private ToolStripMenuItem AddItem(string key, string text)
    {
        var item = new ToolStripMenuItem(text);
        _items[key] = item;
        _menu.Items.Add(item);
        return item;
    }

    public bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    public string TooltipText
    {
        get => _notifyIcon.Text;
        set => _notifyIcon.Text = value.Length <= 63 ? value : value[..62] + "…";
    }

    public event EventHandler? DoubleClick;
    public event EventHandler? ShowMainWindowRequested;
    public event EventHandler? ExitAppRequested;
    public event EventHandler? StartRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? ResumeRequested;
    public event EventHandler? StopRequested;

    public void UpdateMenuState(TrayMenuState state)
    {
        TooltipText = state.TooltipText;
        SetMenuItemEnabled("start", state.CanStart);
        SetMenuItemEnabled("pause", state.CanPause);
        SetMenuItemEnabled("resume", state.CanResume);
        SetMenuItemEnabled("stop", state.CanStop);
        SetMenuItemEnabled("show", state.CanShowMainWindow);
        SetMenuItemEnabled("exit", state.CanExitApp);
    }

    public void SetMenuItemEnabled(string key, bool enabled)
    {
        if (_items.TryGetValue(key, out var item))
            item.Enabled = enabled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
