using System.Drawing;
using System.Windows.Forms;

namespace NextcloudCompanion;

/// <summary>
/// Replacement for <see cref="MessageBox"/>, which always centres on the screen. The dialog opens next to
/// where the user last clicked (the tray), on that monitor, fully inside its working area, and is laid out
/// for that window's real DPI: sizes are written in 96-dpi units and scaled in the Load event, once the
/// window exists, with WinForms auto-scaling off so nothing is scaled twice.
/// </summary>
internal static class Dialogs
{
    // Where the user last clicked to open something (valid for a few seconds, then the cursor is used).
    private static Point _anchor;
    private static DateTime _anchorTime = DateTime.MinValue;

    public static void SetAnchor() { _anchor = Cursor.Position; _anchorTime = DateTime.UtcNow; }

    public static Point Anchor => DateTime.UtcNow - _anchorTime < TimeSpan.FromSeconds(15) ? _anchor : Cursor.Position;

    /// <summary>
    /// Bottom-right corner just past the click (that is where the tray is), clamped into the working area of the
    /// monitor that was clicked. Before the window exists its size is for the process's DPI, so it is projected
    /// onto the target monitor.
    /// </summary>
    public static void PlaceNear(Form f)
    {
        var p = Anchor;
        var wa = Screen.FromPoint(p).WorkingArea;
        int targetDpi = Dpi.ForPoint(p);
        float ratio = f.IsHandleCreated ? 1f : targetDpi / (float)Math.Max(1, f.DeviceDpi);
        int w = Math.Min((int)(f.Width * ratio), wa.Width), h = Math.Min((int)(f.Height * ratio), wa.Height);
        int margin = Dpi.Px(targetDpi, 24);
        int x = Math.Max(wa.Left, Math.Min(p.X - w + margin, wa.Right - w));
        int y = Math.Max(wa.Top, Math.Min(p.Y - h + margin, wa.Bottom - h));
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(x, y);
    }

    private static Form Make(string title, Action<Form, Func<int, int>> build)
    {
        var f = new Form
        {
            AutoScaleMode = AutoScaleMode.None,
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.Manual,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = true,
            TopMost = true,
            Icon = AppIcon.Get(32),
        };
        PlaceNear(f);       // before the window is created, so it opens on the monitor that was clicked
        f.Load += (_, _) =>
        {
            int dpi = Dpi.OfWindow(f);    // the DPI of the window we are in, not the process's
            AppIcon.ApplyTo(f, dpi);
            f.Font = new Font("Segoe UI", (float)Math.Round(12.0 * dpi / 96.0), FontStyle.Regular, GraphicsUnit.Pixel);
            build(f, v => Dpi.Px(dpi, v));
            f.PerformLayout();
            PlaceNear(f);                 // now the real, scaled size is known
        };
        f.Shown += (_, _) => PlaceNear(f);
        return f;
    }

    public static DialogResult Message(string text, string caption, MessageBoxButtons buttons = MessageBoxButtons.OK,
        MessageBoxIcon icon = MessageBoxIcon.None, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        Icon? img = icon switch
        {
            MessageBoxIcon.Warning => SystemIcons.Warning,
            MessageBoxIcon.Question => SystemIcons.Question,
            MessageBoxIcon.Information => SystemIcons.Information,
            MessageBoxIcon.Error => SystemIcons.Error,
            _ => null,
        };
        using var dlg = Make(caption, (f, U) =>
        {
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.Padding = new Padding(U(16));

            var layout = new TableLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 2, Dock = DockStyle.Fill
            };
            if (img != null)
                layout.Controls.Add(new PictureBox
                {
                    Image = img.ToBitmap(), SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, 0, U(16), 0)
                }, 0, 0);
            else
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
            layout.Controls.Add(new Label
            {
                Text = text, UseMnemonic = false, AutoSize = true,
                MaximumSize = new Size(U(340), 0), Margin = new Padding(0, U(4), 0, 0)
            }, 1, 0);

            var bar = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft,
                Dock = DockStyle.Fill, Margin = new Padding(0, U(20), 0, 0)
            };
            layout.Controls.Add(bar, 0, 1);
            layout.SetColumnSpan(bar, 2);

            var defs = buttons switch
            {
                MessageBoxButtons.YesNo => new[] { ("Yes", DialogResult.Yes), ("No", DialogResult.No) },
                MessageBoxButtons.OKCancel => new[] { ("OK", DialogResult.OK), ("Cancel", DialogResult.Cancel) },
                _ => new[] { ("OK", DialogResult.OK) },
            };
            var made = new List<Button>();
            foreach (var (label, result) in defs)
                made.Add(new Button
                {
                    Text = label, DialogResult = result, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    MinimumSize = new Size(U(80), U(27)), Margin = new Padding(U(8), 0, 0, 0)
                });
            for (int i = made.Count - 1; i >= 0; i--)
                bar.Controls.Add(made[i]);        // right-to-left flow: the last one added is leftmost

            f.Controls.Add(layout);
            int def = Math.Min((int)defaultButton / 256, made.Count - 1);     // Button1/2/3 = 0, 256, 512
            f.AcceptButton = made[def];
            f.CancelButton = made.FirstOrDefault(b => b.DialogResult is DialogResult.No or DialogResult.Cancel) ?? made[0];
            f.Shown += (_, _) => made[def].Focus();
        });
        return dlg.ShowDialog();
    }
}
