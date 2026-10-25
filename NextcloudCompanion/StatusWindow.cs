using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NextcloudCompanion;

/// <summary>
/// Compact flyout near the tray. Closing the X hides it; it is never disposed while the tray runs.
/// </summary>
internal sealed class StatusWindow : Form, IDpiTracked
{
    private int _trackedDpi;

    public int TrackedDpi => _trackedDpi;

    private readonly Panel _body;
    private bool _allowDeactivateClose;
    private ClientStatus? _shown;
    private FlowLayoutPanel? _stack;
    private int _fitInner, _fitPad;
    private Rectangle _fitWorkArea;
    private bool _refitQueued;

    /// <summary>
    /// When true (tray mode), the X button hides the flyout so the process stays alive.
    /// </summary>
    public bool HideInsteadOfClose { get; set; } = true;

    public StatusWindow()
    {
        Text = AppInfo.Name;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        Icon = AppIcon.Get(32);
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        MaximizeBox = false;
        MinimizeBox = false;

        int pad = Math.Max(8, Font.Height * 2 / 3);
        _body = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(pad)
        };
        Controls.Add(_body);
    }

    public void ShowStatus(ClientStatus status, Point nearTray)
    {
        _shown = status;
        EnsureHandleOn(nearTray);
        int dpi = Dpi.Of(this);
        ApplyFont(dpi);
        Rebuild(status, nearTray, dpi);
        PositionNear(nearTray);
        _allowDeactivateClose = false;
        if (!Visible)
            Show();
        Activate();

        var t = new System.Windows.Forms.Timer { Interval = 350 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            _allowDeactivateClose = true;
        };
        t.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _trackedDpi = Dpi.OfWindow(this);
        AppIcon.ApplyTo(this, _trackedDpi);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // An ApplicationContext has no main form. If this flyout actually closes,
        // WinForms treats it as the last window and exits the whole tray app.
        if (HideInsteadOfClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (HideInsteadOfClose && _allowDeactivateClose && Visible)
            Hide();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _trackedDpi = e.DeviceDpiNew > 0 ? e.DeviceDpiNew : Dpi.OfWindow(this);
        AppIcon.ApplyTo(this, _trackedDpi);
        if (_shown is null)
            return;
        // WinForms rescales the fonts of existing children after this handler returns, so
        // controls built here would be scaled twice. Rebuild once it has finished.
        var anchor = new Point(Left + Math.Max(1, Width / 2), Top + Math.Max(1, Height / 2));
        BeginInvoke(() =>
        {
            if (IsDisposed || _shown is null)
                return;
            int dpi = Dpi.Of(this);
            ApplyFont(dpi);
            Rebuild(_shown, anchor, dpi);
            PositionNear(anchor);
        });
    }

    /// <summary>
    /// Sizes set before the HWND exists are interpreted in the process DPI (the monitor
    /// that was primary at startup) and then scaled. Create the window on the target
    /// monitor first so layout pixels match the screen it is shown on.
    /// </summary>
    private void EnsureHandleOn(Point near)
    {
        if (IsHandleCreated)
            return;
        var wa = Screen.FromPoint(near).WorkingArea;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(wa.Left + Math.Max(0, wa.Width / 2), wa.Top + Math.Max(0, wa.Height / 2));
        _ = Handle;
    }

    private void ApplyFont(int dpi)
    {
        if (dpi <= 0) dpi = 96;
        float px = 9f * dpi / 72f;
        if (Font.Unit == GraphicsUnit.Pixel && Math.Abs(Font.Size - px) < 0.5f && Font.Name == "Segoe UI")
            return;
        Font = new Font("Segoe UI", px, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private void PositionNear(Point nearTray)
    {
        var screen = Screen.FromPoint(nearTray).WorkingArea;
        int x = screen.Right - Width - Dpi.Px(this, 8);
        int y = screen.Bottom - Height - Dpi.Px(this, 8);
        Location = new Point(Math.Max(screen.Left, x), Math.Max(screen.Top, y));
    }

    private void Rebuild(ClientStatus status, Point near, int dpi)
    {
        var wa = Screen.FromPoint(near).WorkingArea;
        if (dpi <= 0) dpi = 96;
        int pad = Math.Max(8, Font.Height * 2 / 3);
        int margin = Dpi.Px(dpi, 12);
        int innerWidth = Math.Min(
            Math.Max(Font.Height * 22, Dpi.Px(dpi, 420)),
            Math.Max(Font.Height * 18, wa.Width - margin * 2 - pad * 2));
        _body.Padding = new Padding(pad);
        _body.AutoScroll = false;

        _body.SuspendLayout();
        _body.Controls.Clear();

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = innerWidth,
            Margin = Padding.Empty
        };

        stack.Controls.Add(Header(status, innerWidth));

        if (status.Folders.Count == 0)
        {
            stack.Controls.Add(Muted("No Nextcloud folders found in nextcloud.cfg.", innerWidth));
        }
        else
        {
            foreach (var folder in status.Folders)
                stack.Controls.Add(FolderCard(folder, innerWidth));
        }

        stack.Controls.Add(Muted($"Updated {status.CheckedAt:HH:mm:ss}", innerWidth));

        _body.Controls.Add(stack);
        _body.ResumeLayout(true);
        stack.PerformLayout();

        _stack = stack;
        _fitInner = innerWidth;
        _fitPad = pad;
        _fitWorkArea = wa;
        FitToContent(dpi);
    }

    /// <summary>Sizes the window to the laid-out cards (re-run once child handles exist, see QueueRefit).</summary>
    private void FitToContent(int dpi)
    {
        if (_stack is null)
            return;
        var wa = _fitWorkArea;
        int pad = _fitPad;
        _stack.PerformLayout();
        int chrome = IsHandleCreated ? Math.Max(0, Height - ClientSize.Height) : Dpi.Px(dpi, 32);
        int maxClient = Math.Max(Font.Height * 10, wa.Height * 3 / 4 - chrome);
        int contentHeight = Math.Max(_stack.PreferredSize.Height, Font.Height * 5);
        int clientH = Math.Clamp(contentHeight + pad * 2, Font.Height * 9, maxClient);
        ClientSize = new Size(_fitInner + pad * 2, clientH);
        MinimumSize = new Size(Math.Min(_fitInner + pad * 2, wa.Width), Math.Min(Font.Height * 8, wa.Height));
        _body.AutoScroll = contentHeight + pad * 2 > clientH;
    }

    /// <summary>
    /// WinForms rescales a child's own font when its handle is created on a monitor whose DPI differs
    /// from the process's initial DPI, which happens after Rebuild measured everything. Bold lines
    /// therefore put their font back in HandleCreated, and the window is fitted again afterwards.
    /// </summary>
    private void QueueRefit()
    {
        if (_refitQueued || !IsHandleCreated || IsDisposed)
            return;
        _refitQueued = true;
        BeginInvoke(() =>
        {
            _refitQueued = false;
            if (IsDisposed || _stack is null)
                return;
            FitToContent(Dpi.Of(this));
            PositionNear(new Point(Left + Math.Max(1, Width / 2), Top + Math.Max(1, Height / 2)));
        });
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        QueueRefit();
    }

    private Control Header(ClientStatus status, int width)
    {
        int gap = Math.Max(6, Font.Height / 2);
        int badge = Math.Max(10, Font.Height);
        int textWidth = Math.Max(1, width - badge - gap * 2);

        var card = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = width,
            Margin = new Padding(0, 0, 0, gap)
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, badge + 4));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        card.Controls.Add(new StatusBadge { Fill = IconHelper.BadgeColor(status.Overall) }, 0, 0);
        var text = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Width = textWidth,
            Margin = Padding.Empty
        };
        text.Controls.Add(Line(OverallTitle(status), textWidth, bold: true));
        text.Controls.Add(Line(status.Summary, textWidth, color: SystemColors.GrayText));
        card.Controls.Add(text, 1, 0);
        return card;
    }

    private static string OverallTitle(ClientStatus status) => status.Overall switch
    {
        Health.Ok           => "Nextcloud is in sync",
        Health.Error        => "Nextcloud has a sync error",
        Health.Warning      => "Nextcloud has a warning",
        Health.Disconnected => "A Nextcloud server is unreachable",
        Health.Offline      => "Nextcloud client is not running",
        _                   => "Nextcloud status"
    };

    private Control FolderCard(FolderStatus folder, int width)
    {
        int gap = Math.Max(6, Font.Height / 2);
        int badge = Math.Max(10, Font.Height);
        int textWidth = Math.Max(1, width - badge - gap * 3);

        var card = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = width,
            Padding = new Padding(gap),
            Margin = new Padding(0, 0, 0, gap),
            BackColor = Mix(SystemColors.Window, IconHelper.BadgeColor(folder.Health), 0.08f)
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, badge + 4));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        card.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        card.Controls.Add(new StatusBadge { Fill = IconHelper.BadgeColor(folder.Health) }, 0, 0);

        var text = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Width = textWidth,
            Margin = Padding.Empty
        };

        text.Controls.Add(Line(folder.DisplayName, textWidth, bold: true));
        text.Controls.Add(Line(
            folder.Account + (folder.Paused ? " · paused" : ""),
            textWidth,
            color: SystemColors.GrayText));

        string detail = folder.Health switch
        {
            Health.Ok => folder.LastSync is DateTime t ? "Last sync " + FolderStatus.FormatTime(t) : "OK",
            Health.Error or Health.Warning or Health.Disconnected => folder.Headline,
            Health.Offline => "Client not running",
            _ => "No recent result in the log"
        };
        Color detailColor = folder.Health is Health.Error or Health.Disconnected
            ? IconHelper.BadgeColor(Health.Error)
            : SystemColors.GrayText;
        text.Controls.Add(Line(detail, textWidth, color: detailColor));

        bool hasError = folder.Health is Health.Error or Health.Warning && !string.IsNullOrEmpty(folder.ErrorMessage);
        if (hasError && !string.IsNullOrWhiteSpace(folder.ErrorPath))
            text.Controls.Add(Line(folder.ErrorPath!, textWidth, color: SystemColors.GrayText));

        card.Controls.Add(text, 1, 0);
        return card;
    }

    private Label Line(string text, int width, bool bold = false, Color? color = null)
    {
        // Regular lines inherit the form font (already final). Only bold ones need a font of their own.
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(Math.Max(1, width), 0),
            ForeColor = color ?? ForeColor,
            Margin = new Padding(0, 0, 0, 1)
        };
        if (bold)
        {
            label.Font = new Font(Font, FontStyle.Bold);
            label.HandleCreated += (_, _) =>
            {
                label.Font = new Font(Font, FontStyle.Bold);
                QueueRefit();
            };
        }
        return label;
    }

    private Label Muted(string text, int width) => new()
    {
        Text = text,
        ForeColor = SystemColors.GrayText,
        AutoSize = true,
        MaximumSize = new Size(Math.Max(1, width), 0),
        Margin = new Padding(Font.Height / 4, Font.Height / 4, 0, 0)
    };

    private static Color Mix(Color a, Color b, float amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (int)(a.R * (1 - amount) + b.R * amount),
            (int)(a.G * (1 - amount) + b.G * amount),
            (int)(a.B * (1 - amount) + b.B * amount));
    }
}

/// <summary>Filled circle sized from the current font so it tracks DPI.</summary>
internal sealed class StatusBadge : Control
{
    public Color Fill { get; set; } = Color.Gray;

    public StatusBadge()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint
            | ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        TabStop = false;
        Margin = new Padding(0, 4, 4, 0);
    }

    protected override Size DefaultSize => new(12, 12);

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        SyncSize();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        SyncSize();
    }

    private void SyncSize()
    {
        int d = Math.Max(10, Font.Height);
        Size = new Size(d, d);
        MinimumSize = Size;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int inset = 1;
        using var b = new SolidBrush(Fill);
        e.Graphics.FillEllipse(b, inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
    }
}
