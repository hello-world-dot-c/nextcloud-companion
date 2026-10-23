using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace NextcloudCompanion;

public sealed class TrayApplicationContext : ApplicationContext
{
    private static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripLabel _itemStatus;
    private readonly ToolStripMenuItem _itemNotify;
    private readonly ToolStripMenuItem _itemStartup;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly StatusMonitor _monitor = new();

    private StatusWindow? _window;
    private SearchWindow? _searchWindow;
    private MeetingWindow? _meetingWindow;
    private Icon? _currentIcon;
    private Health _lastHealth = Health.Unknown;
    private string _lastSummary = "";

    public TrayApplicationContext()
    {
        _itemStatus = new ToolStripLabel("Checking Nextcloud…")
        {
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
            ForeColor = SystemColors.GrayText,
            Enabled = false
        };

        _itemNotify = new ToolStripMenuItem("Notify when a new error appears", null, OnToggleNotify)
        {
            CheckOnClick = true,
            Checked = Settings.NotifyOnError
        };

        bool startupOn = StartupShortcut.Exists();
        Settings.StartWithWindows = startupOn;
        _itemStartup = new ToolStripMenuItem("Start with Windows", null, OnToggleStartup)
        {
            CheckOnClick = true,
            Checked = startupOn
        };

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(
        [
            new ToolStripLabel($"{AppInfo.Name} v{AppVersion}")
            {
                Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
                ForeColor = SystemColors.GrayText,
                Enabled = false
            },
            _itemStatus,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Show status", null, (_, _) => ShowFlyout()),
            new ToolStripMenuItem("Search files…", null, (_, _) => ShowSearch()),
            new ToolStripMenuItem("New meeting…", null, (_, _) => ShowMeeting()),
            new ToolStripMenuItem("Open Nextcloud", null, OnOpenNextcloud),
            new ToolStripMenuItem("Open log folder", null, OnOpenLogs),
            new ToolStripMenuItem("Copy latest error", null, OnCopyError),
            new ToolStripSeparator(),
            _itemNotify,
            _itemStartup,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, OnExit)
        ]);
        _menu.Opening += (_, _) =>
        {
            _itemStatus.Text = TruncateTip(_monitor.Snapshot.Summary, 80);
            _itemNotify.Checked = Settings.NotifyOnError;
            _itemStartup.Checked = StartupShortcut.Exists();
        };

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
            Text = AppInfo.Name
        };
        _trayIcon.MouseClick += OnTrayClick;

        ApplyStatus(_monitor.Scan(), notify: false);

        _pollTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        try
        {
            ApplyStatus(_monitor.Scan(), notify: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Scan failed", ex);
        }
    }

    private void ApplyStatus(ClientStatus status, bool notify)
    {
        int iconDpi = _window is { IsDisposed: false, IsHandleCreated: true }
            ? _window.DeviceDpi
            : Dpi.ForPoint(Screen.PrimaryScreen?.WorkingArea.Location ?? Cursor.Position);
        Icon newIcon = IconHelper.Create(status.Overall, iconDpi);
        Icon? old = _currentIcon;
        _trayIcon.Icon = newIcon;
        _trayIcon.Text = TruncateTip("Nextcloud: " + status.Summary, 127);
        _currentIcon = newIcon;
        old?.Dispose();

        _itemStatus.Text = TruncateTip(status.Summary, 80);

        if (_window is { IsDisposed: false, Visible: true })
        {
            try { WindowFor(Cursor.Position).ShowStatus(status, Cursor.Position); }
            catch (Exception ex) { ErrorLog.Write("Refresh flyout failed", ex); }
        }

        bool problem = status.Overall is Health.Error or Health.Disconnected;
        bool becameProblem = problem
                             && (_lastHealth != status.Overall || !string.Equals(_lastSummary, status.Summary, StringComparison.Ordinal));

        if (notify && Settings.NotifyOnError && becameProblem)
        {
            _trayIcon.ShowBalloonTip(
                8000,
                status.Overall == Health.Disconnected ? "Nextcloud server unreachable" : "Nextcloud sync error",
                TruncateTip(status.Summary, 240),
                ToolTipIcon.Error);
        }

        _lastHealth = status.Overall;
        _lastSummary = status.Summary;
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        ShowFlyout();
    }

    /// <summary>
    /// A status window created while the notebook was the primary display keeps that
    /// DPI for its lifetime. After docking, throw it away and create one on the current monitor.
    /// </summary>
    private StatusWindow WindowFor(Point near)
    {
        int dpi = Dpi.ForPoint(near);
        if (_window is { IsDisposed: false, IsHandleCreated: true } && Math.Abs(_window.DeviceDpi - dpi) > 1)
        {
            _window.HideInsteadOfClose = false;
            _window.Dispose();
            _window = null;
        }
        if (_window is null || _window.IsDisposed)
            _window = new StatusWindow();
        return _window;
    }

    private void ShowFlyout()
    {
        try
        {
            var near = Cursor.Position;
            var window = WindowFor(near);
            if (window.Visible)
            {
                window.Hide();
                return;
            }

            window.ShowStatus(_monitor.Snapshot, near);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Show status failed", ex);
            MessageBox.Show(
                "Could not open the status window.\n\n" + ex.Message,
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ShowSearch()
    {
        try
        {
            if (_searchWindow is null || _searchWindow.IsDisposed)
            {
                _searchWindow = new SearchWindow(() => _monitor.Snapshot.Folders);
            }
            _searchWindow.ShowNear(Cursor.Position);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Show search failed", ex);
            MessageBox.Show(
                "Could not open search.\n\n" + ex.Message,
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ShowMeeting()
    {
        try
        {
            if (_meetingWindow is null || _meetingWindow.IsDisposed)
                _meetingWindow = new MeetingWindow(() => _monitor.Snapshot.Folders);
            _meetingWindow.ShowNear(Cursor.Position);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Show meeting failed", ex);
            MessageBox.Show(
                "Could not open the meeting window.\n\n" + ex.Message,
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OnOpenNextcloud(object? s, EventArgs e)
    {
        const string exe = @"C:\Program Files\Nextcloud\nextcloud.exe";
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = File.Exists(exe) ? exe : "nextcloud",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not start the Nextcloud client.\n\n" + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnOpenLogs(object? s, EventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nextcloud", "logs");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    private void OnCopyError(object? s, EventArgs e)
    {
        var err = _monitor.Snapshot.Folders.FirstOrDefault(f => f.Health is Health.Error or Health.Disconnected);
        string text = err is null
            ? _monitor.Snapshot.Summary
            : $"{err.DisplayName} ({err.Account})\r\n{err.ErrorPath}\r\n{err.ErrorMessage}\r\n{err.LastError}";
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard can be locked by another app.
        }
    }

    private void OnToggleNotify(object? s, EventArgs e)
    {
        Settings.NotifyOnError = _itemNotify.Checked;
        Settings.Save();
    }

    private void OnToggleStartup(object? s, EventArgs e)
    {
        try
        {
            if (_itemStartup.Checked)
                StartupShortcut.Create(Application.ExecutablePath);
            else
                StartupShortcut.Remove();
            Settings.StartWithWindows = _itemStartup.Checked;
            Settings.Save();
        }
        catch (Exception ex)
        {
            _itemStartup.Checked = StartupShortcut.Exists();
            MessageBox.Show("Could not update the startup shortcut.\n\n" + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnExit(object? s, EventArgs e)
    {
        _trayIcon.Visible = false;
        Application.Exit();
    }

    private static string TruncateTip(string text, int max)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pollTimer.Stop();
            _pollTimer.Dispose();
            _window?.Dispose();
            _searchWindow?.Dispose();
            _meetingWindow?.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _menu.Dispose();
            _currentIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
