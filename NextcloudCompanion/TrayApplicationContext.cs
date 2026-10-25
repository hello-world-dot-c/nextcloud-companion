using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

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
    private readonly Control _ui = new();

    // The DPI of the primary monitor when this process started. WinForms keeps deriving internals (menu item
    // spacing, image sizes, paddings) from the DPI a process started with and does not fully recover when the
    // display changes later (docking, undocking, RDP from a device with other scaling). The reliable cure is a
    // fresh process, so a lasting change of the primary monitor's DPI restarts the app.
    private readonly int _startDpi = Dpi.Primary();
    private readonly System.Windows.Forms.Timer _restartTimer = new() { Interval = 2500 };
    private bool _restarting;
    private readonly List<ToolStripItem> _boldLabels = [];

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
            ForeColor = SystemColors.GrayText,
            Enabled = false
        };
        _boldLabels.Add(_itemStatus);

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
            // Title line: name and version; a click opens the project page.
            new ToolStripMenuItem($"{AppInfo.Name} v{AppVersion}", null, OnOpenRepository)
            {
                ToolTipText = "Click to show GitHub page"
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
        _boldLabels.Add(_menu.Items[0]);
        _menu.Opening += (_, _) =>
        {
            // Bold title lines are derived from the strip's own font each time: SystemFonts and fonts cached
            // earlier belong to the DPI the process started with.
            foreach (var label in _boldLabels)
            {
                var old = label.Font;
                label.Font = new Font(_menu.Font, FontStyle.Bold);
                if (!ReferenceEquals(old, _menu.Font))
                    old.Dispose();
            }
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

        // Docking, undocking, a changed scale factor or an RDP session with another
        // resolution: redraw the tray icon and menu fonts for the new DPI.
        _restartTimer.Tick += (_, _) => OnRestartTimer();
        _ = _ui.Handle; // marshals the event back onto the UI thread
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _pollTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        _pollTimer.Tick += OnPollTick;
        _pollTimer.Start();
    }

    // Some scale-factor changes (notably RDP) only raise this one. It fires often, so only act on a real DPI change.
    private int _iconDpi;

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_iconDpi == Dpi.Primary())
            return;
        OnDisplaySettingsChanged(sender, e);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Raised on a system-events thread.
        void Refresh()
        {
            try
            {
                ApplyStatus(_monitor.Snapshot, notify: false);
                _restartTimer.Stop();       // debounce: the display settles over a few seconds
                _restartTimer.Start();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Display change handling failed", ex);
            }
        }

        if (_ui.IsHandleCreated && !_ui.IsDisposed)
            _ui.BeginInvoke(Refresh);
    }

    private void OnRestartTimer()
    {
        _restartTimer.Stop();
        if (!_restarting && Dpi.Primary() != _startDpi)
            RestartSelf();
    }

    /// <summary>Starts a new instance and exits; the new one waits for the single-instance mutex (--restarted).</summary>
    private void RestartSelf()
    {
        _restarting = true;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restarted") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Restart after a display change failed", ex);
            _restarting = false;
            return;
        }
        OnExit(null, EventArgs.Empty);
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
        // The notification area lives on the primary monitor's taskbar.
        int iconDpi = Dpi.ForPoint(Screen.PrimaryScreen?.WorkingArea.Location ?? Cursor.Position);
        _iconDpi = iconDpi;
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
        if (_window is { IsDisposed: false, IsHandleCreated: true } && Math.Abs(Dpi.Of(_window) - dpi) > 1)
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
            Dialogs.Message(
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
            Dialogs.Message(
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
            Dialogs.Message(
                "Could not open the meeting window.\n\n" + ex.Message,
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OnOpenRepository(object? s, EventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = AppInfo.RepositoryUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Could not open the GitHub page", ex);
            Dialogs.Message("Could not open the GitHub page.\n\n" + AppInfo.RepositoryUrl + "\n\n" + ex.Message,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            Dialogs.Message("Could not start the Nextcloud client.\n\n" + ex.Message,
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
            Dialogs.Message("Could not update the startup shortcut.\n\n" + ex.Message,
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
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _restartTimer.Dispose();
            _ui.Dispose();
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
