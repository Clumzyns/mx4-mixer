using System.Drawing;
using System.IO;
using System.Windows;
using Mixer.ViewModels;
using Forms = System.Windows.Forms;

namespace Mixer.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _notify;
    private readonly MainWindow _window;
    private readonly MainViewModel _viewModel;
    private readonly Icon _icon;
    private readonly MemoryStream? _iconStream;
    private readonly Action _exit;
    private bool _balloonShown;

    public TrayService(MainWindow window, MainViewModel viewModel, Action exit)
    {
        _window = window;
        _viewModel = viewModel;
        _exit = exit;
        (_icon, _iconStream) = LoadIcon();

        var open = new Forms.ToolStripMenuItem("Open Mixer");
        open.Font = new Font(open.Font, System.Drawing.FontStyle.Bold);
        open.Click += (_, _) => _window.ShowFromTray();

        var startWithWindows = new Forms.ToolStripMenuItem("Start with Windows");
        startWithWindows.Click += (_, _) => _viewModel.StartWithWindows = !_viewModel.StartWithWindows;

        var startMinimized = new Forms.ToolStripMenuItem("Start minimized");
        startMinimized.Click += (_, _) => _viewModel.StartMinimized = !_viewModel.StartMinimized;

        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => _exit();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(startWithWindows);
        menu.Items.Add(startMinimized);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);
        menu.Opening += (_, _) =>
        {
            startWithWindows.Checked = _viewModel.StartWithWindows;
            startMinimized.Checked = _viewModel.StartMinimized;
        };

        _notify = new Forms.NotifyIcon
        {
            Text = AppInfo.Name,
            Icon = _icon,
            Visible = true,
            ContextMenuStrip = menu
        };
        _notify.DoubleClick += (_, _) => _window.ShowFromTray();
        _window.HiddenToTray += (_, _) => NotifyHidden();
    }

    public void NotifyHidden()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        try
        {
            _notify.ShowBalloonTip(2000, AppInfo.Name, "Mixer is still running in the tray.", Forms.ToolTipIcon.None);
        }
        catch
        {
            // Balloons can fail in a remote session. The icon is still there.
        }
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Dispose();
        _icon.Dispose();
        _iconStream?.Dispose();
    }

    private static (Icon Icon, MemoryStream? Stream) LoadIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/mixer.ico"))?.Stream;
            if (resource != null)
            {
                using (resource)
                {
                    var copy = new MemoryStream();
                    resource.CopyTo(copy);
                    copy.Position = 0;
                    return (new Icon(copy), copy);
                }
            }
        }
        catch
        {
            // Fall back to the shell icon.
        }
        return ((Icon)SystemIcons.Application.Clone(), null);
    }
}
