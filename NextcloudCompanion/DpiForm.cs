using System.Drawing;
using System.Windows.Forms;

namespace NextcloudCompanion;

/// <summary>
/// Base for fixed-layout windows that must follow the monitor DPI while they are open
/// (docking, undocking, RDP from a device with another scale factor).
///
/// The window is built once in 96-DPI "design pixels" with a 12 px font. Whenever the
/// window DPI is known or changes, every control is re-scaled from those design values
/// (never incrementally, so repeated changes do not accumulate rounding errors).
/// WinForms' own per-control scaling during WM_DPICHANGED is not relied on: it scales
/// fonts but not margins, padding, fixed sizes or table-layout columns, and it runs
/// after <see cref="OnDpiChanged"/>, so the result is re-applied once it has finished.
///
/// WinForms also rescales a child's own font and size when that child's handle is created on
/// a monitor whose DPI differs from the process's initial DPI. Child handles only exist after
/// the form's, so the values are put back in each child's HandleCreated, in Load and in Shown.
/// Children that merely inherit the form font are left alone: the ambient font is already final.
///
/// Re-applying is cheap when nothing changed: a value is only assigned if it differs (assigning
/// a size to a live control resizes its window even when the size is the same), control sizes are
/// only rewritten when the DPI actually changed, and the full layout pass is skipped when no value
/// changed. Windows can also stay invisible until they have settled (see <see cref="HideUntilSettled"/>),
/// so the user sees the finished window instead of the controls being laid out.
/// </summary>
internal abstract class DpiForm : Form, IDpiTracked
{
    public const int DesignDpi = 96;
    private const float DesignFontPx = 12f; // 9 pt at 96 DPI

    private readonly Dictionary<Control, Design> _design = [];
    private int _appliedDpi = DesignDpi;
    private bool _captured;
    private int _trackedDpi;
    private bool _changed;        // did the current ApplyDpi pass assign anything?
    private int _sizeDpi;         // DPI the control sizes were last written for
    private bool _sizesStale;
    private bool _revealPending;
    private bool _sizing;                 // true while this class resizes the window itself
    private bool _inDpiMessage;           // true while WM_DPICHANGED is being processed (Windows resizes the window in it)
    private SizeF _logicalSize;           // outer window size in 96-dpi units (follows the user's resizing)
    private SizeF _logicalMin;            // MinimumSize in 96-dpi units (WinForms may already have scaled it by itself)
    private readonly HashSet<Control> _maxSizeFree = [];

    public int TrackedDpi => _trackedDpi;

    protected DpiForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", DesignFontPx, FontStyle.Regular, GraphicsUnit.Pixel);
        Icon = AppIcon.Get(32);       // the app logo instead of the generic WinForms icon
    }

    /// <summary>
    /// The form never writes this control's MaximumSize (the window sets it itself, e.g. a wrapping
    /// label whose maximum width follows the window width).
    /// </summary>
    protected void LeaveMaxSizeAlone(Control c) => _maxSizeFree.Add(c);

    /// <summary>Called after all controls were re-scaled (e.g. to refit list-view columns).</summary>
    protected virtual void OnDpiApplied() { }

    /// <summary>
    /// DPI to lay out for: the window's own once it exists, otherwise that of the monitor the
    /// window is about to open on (<paramref name="near"/>), never the stale process DPI.
    /// </summary>
    protected int DpiNear(Point near) => IsHandleCreated ? Dpi.Of(this) : Dpi.ForPoint(near);

    private const int WmDpiChanged = 0x02E0;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg != WmDpiChanged)
        {
            base.WndProc(ref m);
            return;
        }
        // DefWindowProc applies Windows' suggested rectangle before OnDpiChanged runs. That resize happens while
        // the old DPI is still recorded, so it must not be taken for the user's own resizing.
        _inDpiMessage = true;
        try
        {
            base.WndProc(ref m);
        }
        finally
        {
            _inDpiMessage = false;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RecordLogicalSize();
    }

    /// <summary>
    /// Remembers the window size in dpi-independent units, so the size after a DPI change does not depend on what
    /// Windows or WinForms suggest. Ignores sizes this class sets itself and minimised/maximised states. Also called
    /// when the window is loaded/shown, because a derived class may set the size before the handle (and so the DPI)
    /// exists and later set the same size again, which raises no Resize event.
    /// </summary>
    private void RecordLogicalSize()
    {
        if (_sizing || _inDpiMessage || _trackedDpi <= 0 || WindowState != FormWindowState.Normal || Width <= 0 || Height <= 0)
            return;
        _logicalSize = new SizeF(Width * 96f / _trackedDpi, Height * 96f / _trackedDpi);
        _logicalMin = new SizeF(MinimumSize.Width * 96f / _trackedDpi, MinimumSize.Height * 96f / _trackedDpi);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_trackedDpi <= 0)
            _trackedDpi = Dpi.OfWindow(this);
        ApplyDpi(_trackedDpi, force: false);
        AppIcon.ApplyTo(this, _trackedDpi);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyDpi(_trackedDpi, force: true);
        RecordLogicalSize();
    }

    /// <summary>
    /// Call right before <c>Show()</c>: the window stays fully transparent while it is created, laid
    /// out and re-scaled, and is revealed (after a synchronous repaint of every child) once it settled.
    /// </summary>
    protected void HideUntilSettled()
    {
        if (Visible || _revealPending)
            return;
        _revealPending = true;
        Opacity = 0;
        // Safety net: never leave the window invisible if Shown does not arrive.
        var guard = new System.Windows.Forms.Timer { Interval = 1500 };
        guard.Tick += (_, _) =>
        {
            guard.Stop();
            guard.Dispose();
            Reveal();
        };
        guard.Start();
    }

    private void Reveal()
    {
        if (!_revealPending || IsDisposed)
            return;
        _revealPending = false;
        if (IsHandleCreated)
            RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwAllChildren | RdwUpdateNow);
        Opacity = 1;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Child handles exist now: put back anything WinForms rescaled when it created them.
        ApplyDpi(_trackedDpi, force: true);
        // Posted, so it runs after derived classes' OnShown work (placement, focus) is done.
        BeginInvoke(() =>
        {
            if (IsDisposed)
                return;
            ApplyDpi(_trackedDpi, force: true);
            RecordLogicalSize();
            EnsureContentFits();
            Reveal();
        });
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);                  // raises the DpiChanged event for subscribers
        int oldDpi = _trackedDpi > 0 ? _trackedDpi : (e.DeviceDpiOld > 0 ? e.DeviceDpiOld : DesignDpi);
        _trackedDpi = e.DeviceDpiNew > 0 ? e.DeviceDpiNew : Dpi.OfWindow(this);
        var before = Bounds;
        var logicalBefore = _logicalSize;

        // Take over completely: WinForms' own handling would resize the window to whatever rectangle Windows suggests
        // and rescale the controls its own way (not margins/padding/table styles, fonts only after we are done).
        // Windows' suggestion is only used for the position.
        e.Cancel = true;
        _sizing = true;
        try
        {
            ApplyDpi(_trackedDpi, force: true);
            AppIcon.ApplyTo(this, _trackedDpi);
            ResizeForDpi(oldDpi, _trackedDpi, e.SuggestedRectangle);
            EnsureContentFits();
        }
        finally
        {
            _sizing = false;
        }
        ErrorLog.Info($"{GetType().Name} DPI {oldDpi}->{_trackedDpi}: was {before.Width}x{before.Height} at {before.Left},{before.Top}; " +
                      $"Windows suggested {e.SuggestedRectangle.Width}x{e.SuggestedRectangle.Height} at {e.SuggestedRectangle.Left},{e.SuggestedRectangle.Top}; " +
                      $"now {Width}x{Height} at {Left},{Top}; work area {Screen.FromControl(this).WorkingArea}; logical size was {logicalBefore.Width:0}x{logicalBefore.Height:0} dip");

        // Child fonts may still be rescaled by WinForms after this handler returns: put our values back and re-check.
        if (IsHandleCreated)
            BeginInvoke(() =>
            {
                if (IsDisposed)
                    return;
                _sizing = true;
                try
                {
                    ApplyDpi(_trackedDpi, force: true);
                    EnsureContentFits();
                }
                finally
                {
                    _sizing = false;
                }
            });
    }

    /// <summary>
    /// New outer size = the size the user had, in 96-dpi units, scaled to the new DPI; position from Windows' suggestion.
    /// Both are kept inside the working area of the monitor the window is moving to.
    /// </summary>
    private void ResizeForDpi(int oldDpi, int newDpi, Rectangle suggested)
    {
        if (WindowState != FormWindowState.Normal)
            return;
        var logical = _logicalSize.IsEmpty ? new SizeF(Width * 96f / oldDpi, Height * 96f / oldDpi) : _logicalSize;
        int w = (int)Math.Round(logical.Width * newDpi / 96f), h = (int)Math.Round(logical.Height * newDpi / 96f);
        MinimumSize = _logicalMin.IsEmpty
            ? Size.Empty
            : new Size((int)Math.Round(_logicalMin.Width * newDpi / 96f), (int)Math.Round(_logicalMin.Height * newDpi / 96f));

        var wa = Screen.FromRectangle(suggested).WorkingArea;
        w = Math.Min(w, wa.Width);
        h = Math.Min(h, wa.Height);
        // A minimum size larger than the monitor would push the window off the screen.
        MinimumSize = new Size(Math.Min(MinimumSize.Width, wa.Width), Math.Min(MinimumSize.Height, wa.Height));
        int x = Math.Max(wa.Left, Math.Min(suggested.Left, wa.Right - w));
        int y = Math.Max(wa.Top, Math.Min(suggested.Top, wa.Bottom - h));
        SetBounds(x, y, w, h);
    }

    /// <summary>The control whose preferred size says how much room the content needs (default: the only child).</summary>
    protected virtual Control? LayoutRoot => Controls.Count == 1 ? Controls[0] : null;

    /// <summary>
    /// Makes sure everything is visible: grows the window to the height the content needs (up to the working
    /// area) and, if even that is not enough, lets the window scroll vertically instead of clipping the content.
    /// Only the height is enforced: widths come from the design size, and wrapping text adapts to the width.
    /// </summary>
    private void EnsureContentFits()
    {
        var root = LayoutRoot;
        if (root is null || !IsHandleCreated || WindowState != FormWindowState.Normal)
            return;

        int needClientH = RequiredClientHeight(root);
        var wa = Screen.FromControl(this).WorkingArea;
        int frameH = Height - ClientSize.Height;
        int h = Math.Max(Height, Math.Min(needClientH + frameH, wa.Height));
        if (h != Height)
        {
            bool was = _sizing;
            _sizing = true;                    // growing for the content is not the user's chosen size
            try
            {
                SetBounds(Left, Math.Max(wa.Top, Math.Min(Top, wa.Bottom - h)), Width, h);
            }
            finally
            {
                _sizing = was;
            }
        }

        bool tooSmall = needClientH > ClientSize.Height;
        if (AutoScroll != tooSmall)
            AutoScroll = tooSmall;
        AutoScrollMinSize = tooSmall ? new Size(0, needClientH) : Size.Empty;
    }

    /// <summary>
    /// Client height the layout needs: rows that size to their content or have a fixed height count in full; rows
    /// that share the leftover space (percent rows, e.g. lists) only need a few lines.
    /// </summary>
    private int RequiredClientHeight(Control root)
    {
        if (root is TableLayoutPanel table)
        {
            table.PerformLayout();
            var rows = table.GetRowHeights();
            int sum = table.Padding.Vertical;
            for (int i = 0; i < rows.Length; i++)
            {
                bool percent = i < table.RowStyles.Count && table.RowStyles[i].SizeType == SizeType.Percent;
                sum += percent ? Font.Height * 3 : rows[i];
            }
            return sum;
        }
        return root.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
    }

    private void ApplyDpi(int dpi, bool force)
    {
        if (dpi <= 0) dpi = DesignDpi;
        CaptureDesign();
        if (!force && dpi == _appliedDpi)
            return;
        _appliedDpi = dpi;
        _changed = false;
        _sizesStale = dpi != _sizeDpi;
        _sizeDpi = dpi;

        SuspendLayout();
        try
        {
            Apply(this, dpi);
        }
        finally
        {
            ResumeLayout(_changed);     // a full layout pass only if something was actually assigned
        }
        if (_changed)
            OnDpiApplied();
    }

    /// <summary>
    /// Records the design-time (96 DPI) metrics of every control. Derived windows call this at
    /// the end of their constructor, before a handle exists and before WinForms can touch anything.
    /// </summary>
    protected void CaptureDesign()
    {
        if (_captured)
            return;
        _captured = true;
        var formFont = Font;
        Walk(this, c =>
        {
            var d = new Design
            {
                Font = c.Font,
                ExplicitFont = !ReferenceEquals(c, this) && !SameFont(c.Font, formFont),
                Margin = c.Margin,
                Padding = c.Padding,
                Min = c.MinimumSize,
                Max = c.MaximumSize,
                Size = c.Size,
                AutoSize = c.AutoSize
            };
            if (c is TableLayoutPanel t)
            {
                d.Columns = t.ColumnStyles.Cast<ColumnStyle>().Select(s => (s.SizeType, s.Width)).ToArray();
                d.Rows = t.RowStyles.Cast<RowStyle>().Select(s => (s.SizeType, s.Height)).ToArray();
            }
            if (c is ListView lv)
                d.ListColumns = lv.Columns.Cast<ColumnHeader>().Select(h => h.Width).ToArray();
            _design[c] = d;
            if (!ReferenceEquals(c, this))
                c.HandleCreated += OnChildHandleCreated;
        });
    }

    private void OnChildHandleCreated(object? sender, EventArgs e)
    {
        if (sender is Control c && _design.TryGetValue(c, out var d) && d.ExplicitFont && _appliedDpi > 0)
            c.Font = ScaleFont(d.Font, _appliedDpi / (float)DesignDpi);
    }

    private static bool SameFont(Font a, Font b) =>
        a.Name == b.Name && a.Style == b.Style && a.Unit == b.Unit && Math.Abs(a.Size - b.Size) < 0.01f;

    private void Apply(Control root, int dpi)
    {
        float k = dpi / (float)DesignDpi;
        Walk(root, c =>
        {
            if (!_design.TryGetValue(c, out var d))
                return;

            if (ReferenceEquals(c, this) || d.ExplicitFont)
            {
                var font = ScaleFont(d.Font, k);
                if (SameFont(font, c.Font))
                    font.Dispose();
                else
                {
                    c.Font = font;
                    _changed = true;
                }
            }
            if (!ReferenceEquals(c, this))
            {
                var margin = ScalePadding(d.Margin, k);
                if (c.Margin != margin)
                {
                    c.Margin = margin;
                    _changed = true;
                }
                // Auto-sized labels, buttons and panels measure themselves. Everything else (text boxes
                // sitting in an auto-sized table row, date pickers, ...) takes its size from the design
                // value, otherwise a size grown at a higher DPI would stick as the new preferred size.
                // Docked controls get their size from the layout, so they are only rewritten when the
                // DPI changed; free-standing ones are also checked every time (WinForms may have
                // rescaled them when their handle was created).
                bool selfSizing = d.AutoSize && c is Label or ButtonBase or Panel;
                if (!selfSizing)
                {
                    var size = ScaleSize(d.Size, dpi);
                    if (_sizesStale || (c.Dock == DockStyle.None && c.Size != size))
                    {
                        c.Size = size;
                        _changed = true;
                    }
                }
            }
            var padding = ScalePadding(d.Padding, k);
            if (c.Padding != padding)
            {
                c.Padding = padding;
                _changed = true;
            }
            if (!ReferenceEquals(c, this))
            {
                var min = ScaleSize(d.Min, dpi);
                if (c.MinimumSize != min)
                {
                    c.MinimumSize = min;
                    _changed = true;
                }
                var max = ScaleSize(d.Max, dpi);
                if (c.MaximumSize != max && !_maxSizeFree.Contains(c))
                {
                    c.MaximumSize = max;
                    _changed = true;
                }
            }

            if (c is TableLayoutPanel t)
            {
                for (int i = 0; i < d.Columns!.Length && i < t.ColumnStyles.Count; i++)
                {
                    float w = d.Columns[i].Value * k;
                    if (d.Columns[i].Type == SizeType.Absolute && t.ColumnStyles[i].Width != w)
                    {
                        t.ColumnStyles[i].Width = w;
                        _changed = true;
                    }
                }
                for (int i = 0; i < d.Rows!.Length && i < t.RowStyles.Count; i++)
                {
                    float h = d.Rows[i].Value * k;
                    if (d.Rows[i].Type == SizeType.Absolute && t.RowStyles[i].Height != h)
                    {
                        t.RowStyles[i].Height = h;
                        _changed = true;
                    }
                }
            }
            if (c is ListView lv && d.ListColumns is { } cols)
                for (int i = 0; i < cols.Length && i < lv.Columns.Count; i++)
                {
                    int width = (int)Math.Round(cols[i] * k);
                    if (lv.Columns[i].Width != width)
                    {
                        lv.Columns[i].Width = width;
                        _changed = true;
                    }
                }
        });
    }

    private const uint RdwInvalidate = 0x0001, RdwAllChildren = 0x0080, RdwUpdateNow = 0x0100;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    private static void Walk(Control c, Action<Control> action)
    {
        action(c);
        foreach (Control child in c.Controls)
            Walk(child, action);
    }

    private static Font ScaleFont(Font design, float k)
    {
        float designPx = design.Unit == GraphicsUnit.Pixel ? design.Size : design.SizeInPoints * DesignDpi / 72f;
        return new Font(design.FontFamily, (float)Math.Round(designPx * k), design.Style, GraphicsUnit.Pixel);
    }

    private static Padding ScalePadding(Padding p, float k) => new(
        (int)Math.Round(p.Left * k), (int)Math.Round(p.Top * k),
        (int)Math.Round(p.Right * k), (int)Math.Round(p.Bottom * k));

    private static Size ScaleSize(Size s, int dpi) => new(
        s.Width == 0 ? 0 : Dpi.Px(dpi, s.Width),
        s.Height == 0 ? 0 : Dpi.Px(dpi, s.Height));

    private sealed class Design
    {
        public Font Font = null!;
        public bool ExplicitFont;
        public Padding Margin, Padding;
        public Size Min, Max, Size;
        public bool AutoSize;
        public (SizeType Type, float Value)[]? Columns, Rows;
        public int[]? ListColumns;
    }
}
