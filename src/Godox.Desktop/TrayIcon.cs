using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Godox.Desktop;

public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Drawing.Icon drawing;
    public TrayIcon(MainWindow window)
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Godox.Desktop;component/Assets/godox.ico")).Stream;
        using var resource = new Drawing.Icon(stream);
        drawing = (Drawing.Icon)resource.Clone();
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть Godox Desktop", null, (_, _) => window.Dispatcher.InvokeAsync(window.RestoreFromTray));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => window.Dispatcher.InvokeAsync(window.ExitApplication));
        icon = new Forms.NotifyIcon { Icon = drawing, Text = "Godox Desktop", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) window.Dispatcher.InvokeAsync(window.RestoreFromTray); };
    }
    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose(); menu.Dispose(); drawing.Dispose();
    }
}
