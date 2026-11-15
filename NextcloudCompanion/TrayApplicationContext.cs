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
    private readonly IProcessController _processes = new WindowsProcessController();
    private readonly StatusMonitor _monitor;
    private readonly RecoveryCoordinator _coordinator;
    private readonly ClientRecoveryService _recovery;
    private readonly ToolStripMenuItem _itemRestart;
    private readonly ToolStripMenuItem _itemAutoRestart;
    private readonly Notifier _notifier;
    private bool _scanning;
    private DecisionKind _lastDecisionKind;
    private string _lastDecisionReason = "";
    private RecoveryStatus _recoveryStatus = RecoveryStatus.None;
    private CancellationTokenSource? _recoveryCts;
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
    private AppPasswordsWindow? _appPasswordsWindow;
    private Icon? _currentIcon;
    private Health _lastHealth = Health.Unknown;
    private string _lastSummary = "";

    public TrayApplicationContext()
    {
        _monitor = new StatusMonitor(new StatusMonitorOptions { Processes = _processes });
        _coordinator = new RecoveryCoordinator(SystemClock.Instance, new SettingsRecoveryStore(), enabled: () => Settings.AutoRestartNextcloud);
        _recovery = new ClientRecoveryService(_processes, _monitor, SystemClock.Instance);
        _coordinator.Diagnostic += ErrorLog.Recovery;
        _notifier = new Notifier(new WindowsToastSink(ShowBalloon), SystemClock.Instance);
        _notifier.Shown += ErrorLog.Recovery;

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

        _itemRestart = new ToolStripMenuItem("Restart Nextcloud to retry syncing…", null, OnRestartNextcloud);
        _itemAutoRestart = new ToolStripMenuItem("Automatically restart Nextcloud when syncing appears stuck", null, OnToggleAutoRestart)
        {
            CheckOnClick = true,
            Checked = Settings.AutoRestartNextcloud
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
            new ToolStripMenuItem("App passwords…", null, (_, _) => ShowAppPasswords()),
            new ToolStripMenuItem("Open Nextcloud", null, OnOpenNextcloud),
            _itemRestart,
            new ToolStripMenuItem("Open log folder", null, OnOpenLogs),
            new ToolStripMenuItem("Copy latest error", null, OnCopyError),
            new ToolStripSeparator(),
            _itemNotify,
            _itemStartup,
            _itemAutoRestart,
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
            UpdateRecoveryItems();
        };

        _trayIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
            Text = AppInfo.Name
        };
        _trayIcon.MouseClick += OnTrayClick;

        ProcessScan(_monitor.Scan(), notify: false);

        // Docking, undocking, a changed scale factor or an RDP session with another
        // resolution: redraw the tray icon and menu fonts for the new DPI.
        _restartTimer.Tick += (_, _) => OnRestartTimer();
        _ = _ui.Handle; // marshals the event back onto the UI thread
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

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

    private async void OnPollTick(object? sender, EventArgs e)
    {
        if (_scanning)
            return;             // the previous scan has not finished: never overlap
        _scanning = true;
        try
        {
            var status = await Task.Run(() => _monitor.Scan());      // reads logs: not on the UI thread
            if (!IsDisposedUi())
                ProcessScan(status, notify: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Scan failed", ex);
        }
        finally
        {
            _scanning = false;
        }
    }

    private bool IsDisposedUi() => _ui.IsDisposed;

    /// <summary>
    /// A finished scan: put the recovery state on it, let the coordinator judge the evidence (it marks stalled folders),
    /// show it, and start an automatic restart if one is due.
    /// </summary>
    private void ProcessScan(ClientStatus status, bool notify)
    {
        if (_recoveryStatus.Phase is RecoveryPhase.Resumed or RecoveryPhase.Failed
            && _recoveryStatus.Since is DateTime since && DateTime.Now - since > TimeSpan.FromMinutes(5))
            _recoveryStatus = RecoveryStatus.None;

        status.Recovery = _recoveryStatus;
        var decision = _coordinator.Observe(status);
        status.RecoveryNote = Settings.AutoRestartNextcloud ? _coordinator.Note : null;
        if (decision.Kind != _lastDecisionKind || (decision.Kind != DecisionKind.None && decision.Reason != _lastDecisionReason))
        {
            _lastDecisionKind = decision.Kind;
            _lastDecisionReason = decision.Reason;
            if (decision.Kind != DecisionKind.None)
                ErrorLog.Recovery($"Decision: {decision.Kind}. {decision.Reason}");
        }
        ApplyStatus(status, notify);
        NotifyRecoveryDecision(decision, notify);

        if (notify && decision.Kind == DecisionKind.Due && !_recovery.Busy && !_recoveryStatus.Active)
            StartAutomaticRestart(decision);
    }

    /// <summary>
    /// Tells the user, once, when automatic recovery has recognised a stall but may not act, or cannot work at all.
    /// (Starting a restart and its outcome are announced where they happen, in <see cref="RunRecoveryAsync"/>.)
    /// </summary>
    private void NotifyRecoveryDecision(RecoveryDecision decision, bool notify)
    {
        if (!notify || !Settings.AutoRestartNextcloud)
            return;
        if (decision.Kind == DecisionKind.Suppressed && decision.IncidentId is not null)
        {
            _notifier.Show("recovery-suppressed|" + decision.IncidentId + "|" + decision.Reason,
                "Nextcloud looks stuck - not restarted automatically", decision.Reason,
                NotificationLevel.Warning, TimeSpan.FromHours(12));
        }
        else if (decision.Kind == DecisionKind.Disabled)
        {
            _notifier.Show("recovery-disabled|" + decision.Reason,
                "Automatic restart cannot work with this Nextcloud", decision.Reason,
                NotificationLevel.Warning, TimeSpan.FromHours(12));
        }
    }

    /// <summary>Fallback when Windows toasts cannot be used: the classic tray balloon.</summary>
    private void ShowBalloon(string title, string text, NotificationLevel level)
        => _trayIcon.ShowBalloonTip(10000, title, TruncateTip(text, 240),
            level switch { NotificationLevel.Error => ToolTipIcon.Error, NotificationLevel.Warning => ToolTipIcon.Warning, _ => ToolTipIcon.Info });

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
            return;
        // Raised on a system-events thread. Waiting right after a wake-up is normal: the coordinator starts over.
        if (_ui.IsHandleCreated && !_ui.IsDisposed)
            _ui.BeginInvoke(_coordinator.NotifyResume);
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

        _itemStatus.Text = TruncateTip(status.Summary, 110);
        NotifyStalls(status, notify);

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
            _notifier.Show("problem|" + status.Summary,
                status.Overall == Health.Disconnected ? "Nextcloud server unreachable" : "Nextcloud sync error",
                status.Summary, NotificationLevel.Error, TimeSpan.FromMinutes(15));
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
            _window = new StatusWindow { RestartRequested = OnRestartNextcloud };
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

    private void ShowAppPasswords()
    {
        try
        {
            if (_appPasswordsWindow is null || _appPasswordsWindow.IsDisposed)
                _appPasswordsWindow = new AppPasswordsWindow(() => _monitor.Snapshot.Folders);
            _appPasswordsWindow.ShowNear(Cursor.Position);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Show app passwords failed", ex);
            Dialogs.Message(
                "Could not open the app passwords window.\n\n" + ex.Message,
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
                _meetingWindow = new MeetingWindow(() => _monitor.Snapshot.Folders, ShowAppPasswords);
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

    // ---- restarting Nextcloud -----------------------------------------------------------------------------------

    /// <summary>Menu and flyout enable the restart only for exactly one identifiable client, and say why not otherwise.</summary>
    private void UpdateRecoveryItems()
    {
        var snapshot = _monitor.Snapshot;
        bool busy = _recovery.Busy || _recoveryStatus.Active;
        if (busy)
        {
            _itemRestart.Enabled = false;
            _itemRestart.ToolTipText = "A restart is already in progress.";
        }
        else if (snapshot.AmbiguousProcess)
        {
            _itemRestart.Enabled = false;
            _itemRestart.ToolTipText = "More than one Nextcloud process is running; Companion will not guess which to restart.";
        }
        else if (snapshot.Process is null)
        {
            _itemRestart.Enabled = false;
            _itemRestart.ToolTipText = "Nextcloud is not running.";
        }
        else
        {
            _itemRestart.Enabled = true;
            _itemRestart.ToolTipText = "Closes Nextcloud and starts it again. This affects all of its accounts.";
        }

        _itemAutoRestart.Checked = Settings.AutoRestartNextcloud;
        var decision = _coordinator.LastDecision;
        _itemAutoRestart.ToolTipText = Settings.AutoRestartNextcloud && decision.Kind is DecisionKind.Disabled or DecisionKind.Suppressed
            ? (decision.Kind == DecisionKind.Disabled ? "Not possible with this Nextcloud: " : "Not restarting now: ") + decision.Reason
            : Settings.AutoRestartNextcloud && _coordinator.ForecastNote is { } note
                ? "On. Companion is " + note + " unless Nextcloud starts syncing first."
                : "Off by default. Restarts Nextcloud at most once per stall, one hour apart and twice per day.";
    }

    private void OnRestartNextcloud()
    {
        if (_recovery.Busy || _recoveryStatus.Active)
            return;
        var answer = Dialogs.Message(
            "Nextcloud will be closed and started again in the background.\n\n"
            + "This affects every account and folder of that Nextcloud, not only the one that seems stuck. "
            + "Syncing starts again by itself afterwards, and Companion then checks whether it did.\n\n"
            + "Restart Nextcloud now?",
            AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
            _ = RunRecoveryAsync(automatic: false, force: null);
    }

    private void OnRestartNextcloud(object? sender, EventArgs e) => OnRestartNextcloud();

    private void StartAutomaticRestart(RecoveryDecision decision)
    {
        // The attempt is recorded first. If the history cannot be saved, the limits could not survive a restart of
        // Companion, so nothing is restarted.
        if (!_coordinator.BeginAttempt(decision))
        {
            _notifier.Show("history-unsaved", "Nextcloud was not restarted",
                "Companion could not save its restart history, so it did not restart Nextcloud automatically.",
                NotificationLevel.Warning, TimeSpan.FromHours(1));
            return;
        }
        _notifier.Show("auto-restart", "Restarting Nextcloud automatically",
            decision.Reason + " Companion is restarting it now.", NotificationLevel.Warning, TimeSpan.FromSeconds(30));
        _ = RunRecoveryAsync(automatic: true, force: null);
    }

    private async Task RunRecoveryAsync(bool automatic, ProcessIdentity? force)
    {
        _recoveryCts?.Cancel();
        _recoveryCts = new CancellationTokenSource();
        var token = _recoveryCts.Token;
        var progress = new Progress<RecoveryStatus>(OnRecoveryProgress);     // created here: reports on the UI thread
        RecoveryResult result;
        try
        {
            result = await Task.Run(() => force is null
                ? _recovery.RestartAsync(automatic, progress, token)
                : _recovery.ForceRestartAsync(force, progress, token));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Restarting Nextcloud failed", ex);
            result = new RecoveryResult(RecoveryResultKind.Refused, "Restarting Nextcloud failed: " + ex.Message);
            OnRecoveryProgress(new RecoveryStatus { Phase = RecoveryPhase.Failed, Message = result.Message, Automatic = automatic, Since = DateTime.Now });
        }

        if (_ui.IsDisposed)
            return;
        ErrorLog.Recovery($"Nextcloud restart ({(automatic ? "automatic" : "manual")}): {result.Kind}. {result.Message}");

        if (result.Kind is RecoveryResultKind.Refused or RecoveryResultKind.Busy)
            OnRecoveryProgress(RecoveryStatus.None);     // nothing happened: do not leave a failure on screen

        if (result.Kind == RecoveryResultKind.QuitFailed && !automatic && force is null)
        {
            // Force-closing is a separate, explicit choice that is never taken automatically.
            var target = _monitor.Snapshot.Process;
            var answer = Dialogs.Message(
                result.Message + "\n\nForce Nextcloud to close now? Anything it has not finished writing may be lost.",
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes && target is not null)
            {
                await RunRecoveryAsync(automatic: false, force: target);
                return;
            }
        }
        else if (automatic || result.Succeeded)
        {
            string title = result.Succeeded
                ? (automatic ? "Nextcloud restarted automatically" : "Nextcloud restarted")
                : (automatic ? "Automatic restart of Nextcloud did not succeed" : "Nextcloud restart");
            _notifier.Show("restart-result", title, result.Message,
                result.Succeeded ? NotificationLevel.Info : NotificationLevel.Warning, TimeSpan.FromSeconds(30));
        }
        else
        {
            Dialogs.Message(result.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        _ = Task.Run(() => _monitor.Scan()).ContinueWith(t => _ui.BeginInvoke(() => ProcessScan(t.Result, notify: false)),
            TaskScheduler.Default);
    }

    private void OnRecoveryProgress(RecoveryStatus status)
    {
        _recoveryStatus = status;
        var snapshot = _monitor.Snapshot;
        snapshot.Recovery = status;
        ApplyStatus(snapshot, notify: false);
    }

    private void OnToggleAutoRestart(object? sender, EventArgs e)
    {
        bool enable = _itemAutoRestart.Checked;
        bool previous = Settings.AutoRestartNextcloud;
        if (enable)
        {
            var answer = Dialogs.Message(
                "When a folder has been \"waiting to start syncing\" for about 10 minutes and nothing else is syncing, "
                + "Companion will close Nextcloud and start it again.\n\n"
                + "That affects all of its accounts. It happens at most once per stall, at least an hour apart, and at most "
                + "twice in 24 hours. It does not happen while an account is unreachable, needs you to sign in, or has a paused folder, "
                + "and never if Nextcloud's log does not show enough to tell waiting from idle.\n\n"
                + "Turn this on?",
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                _itemAutoRestart.Checked = previous;
                return;
            }
        }

        Settings.AutoRestartNextcloud = enable;
        if (!Settings.Save())
        {
            // The choice would be lost at the next start: say so, and keep what is really saved.
            Settings.AutoRestartNextcloud = previous;
            _itemAutoRestart.Checked = previous;
            Dialogs.Message("The setting could not be saved, so it was not changed.\n\n" + Settings.LastSaveError,
                AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// At most one notice every ninety minutes about a suspected stall, and none when automatic restart is on (it
    /// announces what it does itself). The suspicion may come and go from one scan to the next; the notice must not.
    /// </summary>
    private void NotifyStalls(ClientStatus status, bool notify)
    {
        if (!notify || !Settings.NotifyOnError || Settings.AutoRestartNextcloud || !status.Folders.Any(f => f.Stalled))
            return;
        _notifier.Show("stall", "Nextcloud may be stuck waiting to sync",
            "A folder has been waiting to start syncing for a long time. You can restart Nextcloud from the tray menu.",
            NotificationLevel.Warning, TimeSpan.FromMinutes(90));
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
        var dir = _monitor.Snapshot.LogFile is { } monitored
            ? Path.GetDirectoryName(monitored)!
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nextcloud", "logs");
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
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _recoveryCts?.Cancel();
            _recoveryCts?.Dispose();
            _restartTimer.Dispose();
            _ui.Dispose();
            _pollTimer.Dispose();
            _window?.Dispose();
            _searchWindow?.Dispose();
            _meetingWindow?.Dispose();
            _appPasswordsWindow?.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _menu.Dispose();
            _currentIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
