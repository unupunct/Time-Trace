using System.Windows.Forms;
using TimeTrace.Core.Tracking;

namespace TimeTrace.App.Services;

/// <summary>System tray icon with the context menu required by the spec. Uses the standard Windows notification area.</summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pauseItem, _resumeItem;
    private readonly Action _open, _stats, _timeline, _exit;

    public TrayService(Action open, Action stats, Action timeline, Action exit)
    {
        _open = open; _stats = stats; _timeline = timeline; _exit = exit;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open TimeTrace", null, (_, _) => _open());
        menu.Items.Add(new ToolStripSeparator());
        _pauseItem = new ToolStripMenuItem("Pause Tracking", null, async (_, _) => await AppServices.Instance.PauseTrackingAsync());
        _resumeItem = new ToolStripMenuItem("Resume Tracking", null, async (_, _) => await AppServices.Instance.StartTrackingAsync());
        menu.Items.Add(_pauseItem); menu.Items.Add(_resumeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("View Today's Statistics", null, (_, _) => _stats());
        menu.Items.Add("Open Timeline", null, (_, _) => _timeline());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _exit());
        menu.Opening += (_, _) => RefreshMenu();

        System.Drawing.Icon icon;
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/TimeTrace.ico");
            using var s = System.Windows.Application.GetResourceStream(uri)!.Stream;
            icon = new System.Drawing.Icon(s);
        }
        catch (Exception) { icon = System.Drawing.SystemIcons.Application; }

        _icon = new NotifyIcon { Icon = icon, Text = "TimeTrace", Visible = true, ContextMenuStrip = menu };
        _icon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) _open(); };
    }

    private void RefreshMenu()
    {
        var st = AppServices.Instance.Engine.State;
        _pauseItem.Enabled = st == TrackingState.Running;
        _resumeItem.Enabled = AppServices.Instance.Settings.Current.TrackingEnabled && st != TrackingState.Running;
        _resumeItem.Text = st == TrackingState.Stopped ? "Start Tracking" : "Resume Tracking";
    }

    public void UpdateStatus(string text) => _icon.Text = Truncate("TimeTrace – " + text, 63);

    public void ShowBalloon(string title, string text, bool isError = false)
    {
        try { _icon.ShowBalloonTip(4000, title, text, isError ? ToolTipIcon.Error : ToolTipIcon.Info); }
        catch (Exception) { /* notifications are best effort */ }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}
