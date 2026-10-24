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
/// </summary>
internal abstract class DpiForm : Form, IDpiTracked
{
    public const int DesignDpi = 96;
    private const float DesignFontPx = 12f; // 9 pt at 96 DPI

    private readonly Dictionary<Control, Design> _design = [];
    private int _appliedDpi = DesignDpi;
    private bool _captured;
    private int _trackedDpi;

    public int TrackedDpi => _trackedDpi;

    protected DpiForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", DesignFontPx, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    /// <summary>Called after all controls were re-scaled (e.g. to refit list-view columns).</summary>
    protected virtual void OnDpiApplied() { }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_trackedDpi <= 0)
            _trackedDpi = Dpi.OfWindow(this);
        ApplyDpi(_trackedDpi, force: false);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _trackedDpi = e.DeviceDpiNew > 0 ? e.DeviceDpiNew : Dpi.OfWindow(this);
        ApplyDpi(_trackedDpi, force: true);
        // WinForms rescales child fonts after this handler returns; put our values back.
        if (IsHandleCreated)
            BeginInvoke(() =>
            {
                if (!IsDisposed)
                    ApplyDpi(Dpi.Of(this), force: true);
            });
    }

    private void ApplyDpi(int dpi, bool force)
    {
        if (dpi <= 0) dpi = DesignDpi;
        CaptureDesign();
        if (!force && dpi == _appliedDpi)
            return;
        _appliedDpi = dpi;

        SuspendLayout();
        try
        {
            Apply(this, dpi);
        }
        finally
        {
            ResumeLayout(true);
        }
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
        Walk(this, c =>
        {
            var d = new Design
            {
                Font = c.Font,
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
        });
    }

    private void Apply(Control root, int dpi)
    {
        float k = dpi / (float)DesignDpi;
        Walk(root, c =>
        {
            if (!_design.TryGetValue(c, out var d))
                return;

            c.Font = ScaleFont(d.Font, k);
            if (!ReferenceEquals(c, this))
            {
                c.Margin = ScalePadding(d.Margin, k);
                // Auto-sized labels, buttons and panels measure themselves. Everything else (text boxes
                // sitting in an auto-sized table row, date pickers, ...) takes its size from the design
                // value, otherwise a size grown at a higher DPI would stick as the new preferred size.
                bool selfSizing = d.AutoSize && c is Label or ButtonBase or Panel;
                if (!selfSizing)
                    c.Size = ScaleSize(d.Size, dpi);
            }
            c.Padding = ScalePadding(d.Padding, k);
            if (!ReferenceEquals(c, this))
            {
                c.MinimumSize = ScaleSize(d.Min, dpi);
                c.MaximumSize = ScaleSize(d.Max, dpi);
            }

            if (c is TableLayoutPanel t)
            {
                for (int i = 0; i < d.Columns!.Length && i < t.ColumnStyles.Count; i++)
                    if (d.Columns[i].Type == SizeType.Absolute)
                        t.ColumnStyles[i].Width = d.Columns[i].Value * k;
                for (int i = 0; i < d.Rows!.Length && i < t.RowStyles.Count; i++)
                    if (d.Rows[i].Type == SizeType.Absolute)
                        t.RowStyles[i].Height = d.Rows[i].Value * k;
            }
            if (c is ListView lv && d.ListColumns is { } cols)
                for (int i = 0; i < cols.Length && i < lv.Columns.Count; i++)
                    lv.Columns[i].Width = (int)Math.Round(cols[i] * k);
        });
    }

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
        public Padding Margin, Padding;
        public Size Min, Max, Size;
        public bool AutoSize;
        public (SizeType Type, float Value)[]? Columns, Rows;
        public int[]? ListColumns;
    }
}
