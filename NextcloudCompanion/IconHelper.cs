using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NextcloudCompanion;

/// <summary>
/// Runtime tray badge. Colour encodes health; glyph is readable at small sizes.
/// Drawn at the system small-icon size so it stays sharp on high-DPI screens.
/// </summary>
public static class IconHelper
{
    private static readonly Color ColourOk      = Color.FromArgb(0x2E, 0x7D, 0x32);
    private static readonly Color ColourWarn    = Color.FromArgb(0xEF, 0x6C, 0x00);
    private static readonly Color ColourError   = Color.FromArgb(0xC6, 0x28, 0x28);
    private static readonly Color ColourOffline = Color.FromArgb(0x75, 0x75, 0x75);
    private static readonly Color ColourUnknown = Color.FromArgb(0x15, 0x65, 0xC0);

    public static Icon Create(Health health, int? dpi = null)
    {
        Color bg = health switch
        {
            Health.Ok           => ColourOk,
            Health.Warning      => ColourWarn,
            Health.Error        => ColourError,
            Health.Offline      => ColourOffline,
            Health.Disconnected => ColourOffline,
            _                   => ColourUnknown
        };

        int size = IconPixelSize(dpi ?? Dpi.System);
        float k = size / 32f;

        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            using (var brush = new SolidBrush(bg))
                g.FillEllipse(brush, k, k, size - 2 * k, size - 2 * k);

            using var pen = new Pen(Color.White, 3.2f * k)
            {
                StartCap = LineCap.Round,
                EndCap   = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            switch (health)
            {
                case Health.Ok:
                    g.DrawLines(pen, new PointF[]
                    {
                        new(8f * k, 17f * k), new(14f * k, 23f * k), new(24f * k, 10f * k)
                    });
                    break;
                case Health.Error:
                    g.DrawLine(pen, 10f * k, 10f * k, 22f * k, 22f * k);
                    g.DrawLine(pen, 22f * k, 10f * k, 10f * k, 22f * k);
                    break;
                case Health.Warning:
                    g.DrawLine(pen, 16f * k, 8f * k, 16f * k, 18f * k);
                    using (var dot = new SolidBrush(Color.White))
                        g.FillEllipse(dot, 14.2f * k, 21.2f * k, 3.6f * k, 3.6f * k);
                    break;
                case Health.Offline:
                case Health.Disconnected:
                    g.DrawLine(pen, 10f * k, 16f * k, 22f * k, 16f * k);
                    break;
                default:
                    g.DrawLine(pen, 10f * k, 16f * k, 22f * k, 16f * k);
                    break;
            }
        }

        IntPtr hIcon = bmp.GetHicon();
        Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    public static Color BadgeColor(Health health) => health switch
    {
        Health.Ok           => ColourOk,
        Health.Warning      => ColourWarn,
        Health.Error        => ColourError,
        Health.Offline      => ColourOffline,
        Health.Disconnected => ColourOffline,
        _                   => ColourUnknown
    };

    private static int IconPixelSize(int dpi)
    {
        if (dpi <= 0) dpi = 96;
        int size;
        try { size = GetSystemMetricsForDpi(SmCxSmIcon, (uint)dpi); }
        catch { size = 16 * dpi / 96; }
        // Draw at least 32px (and 2× the tray slot) so Windows can downscale cleanly.
        return Math.Max(32, size * 2);
    }

    private const int SmCxSmIcon = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
