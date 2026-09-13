using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace DropScan.Desktop;

public sealed class TrayService : IDisposable
{
    readonly Forms.NotifyIcon icon;
    readonly Forms.ContextMenuStrip menu;
    public TrayService(Action restore, Action exit)
    {
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("เปิด DropScan", null, (_, _) => restore());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("หยุดแชร์และออก", null, (_, _) => exit());
        icon = new Forms.NotifyIcon { Icon = Drawing.SystemIcons.Application, Text = "DropScan — พร้อมใช้งาน", ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => restore();
    }
    public void Notify(string title, string message) => icon.ShowBalloonTip(4000, title, message, Forms.ToolTipIcon.Info);
    public void SetStatus(string status) => icon.Text = ("DropScan — " + status) is var text && text.Length > 63 ? text[..63] : text;
    public void Dispose() { icon.Visible = false; icon.Dispose(); menu.Dispose(); }
}
