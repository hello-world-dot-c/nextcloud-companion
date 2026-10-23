using System.Runtime.InteropServices;

namespace NextcloudCompanion;

internal static class Dpi
{
    public static int System => (int)GetDpiForSystem();

    public static float Factor(Control c)
    {
        int dpi = c.IsHandleCreated && c.DeviceDpi > 0 ? c.DeviceDpi : System;
        if (dpi <= 0) dpi = 96;
        return dpi / 96f;
    }

    public static int Px(Control c, int designPx)
        => Px(c.IsHandleCreated && c.DeviceDpi > 0 ? c.DeviceDpi : System, designPx);

    public static int Px(int dpi, int designPx)
    {
        if (dpi <= 0) dpi = 96;
        return Math.Max(1, (int)Math.Round(designPx * (dpi / 96f)));
    }

    /// <summary>Effective DPI of the monitor that contains <paramref name="p"/>.</summary>
    public static int ForPoint(Point p)
    {
        var pt = new POINT { X = p.X, Y = p.Y };
        IntPtr monitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero
            && GetDpiForMonitor(monitor, MdEffectiveDpi, out uint dpiX, out _) == 0
            && dpiX > 0)
            return (int)dpiX;
        int system = System;
        return system > 0 ? system : 96;
    }

    public static Size Size(Control c, int width, int height)
        => new(Px(c, width), Px(c, height));

    public static Rectangle WorkingArea(Point near)
        => Screen.FromPoint(near).WorkingArea;

    /// <summary>
    /// Preferred outer size in design pixels, converted for this control's DPI
    /// and clamped so the window stays fully inside the working area.
    /// </summary>
    public static Size FitWindow(Control c, int designWidth, int designHeight, Rectangle work, int margin)
    {
        int maxW = Math.Max(280, work.Width - margin * 2);
        int maxH = Math.Max(200, work.Height - margin * 2);
        return new Size(
            Math.Min(Px(c, designWidth), maxW),
            Math.Min(Px(c, designHeight), maxH));
    }

    public static Point BottomRight(Size window, Rectangle work, int margin)
        => new(
            Math.Max(work.Left, work.Right - window.Width - margin),
            Math.Max(work.Top, work.Bottom - window.Height - margin));

    private const uint MonitorDefaultToNearest = 2;
    private const int MdEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
