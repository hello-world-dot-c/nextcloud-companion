using System.Runtime.InteropServices;

namespace NextcloudCompanion;

/// <summary>A window that remembers the DPI it was last told about (creation or WM_DPICHANGED).</summary>
internal interface IDpiTracked
{
    int TrackedDpi { get; }
}

internal static class Dpi
{
    /// <summary>
    /// DPI the process started with. Stale after docking, undocking or an RDP change: only use it
    /// as a last resort; ask for the monitor's DPI (<see cref="ForPoint"/>) instead.
    /// </summary>
    private static int ProcessStart => (int)GetDpiForSystem();

    /// <summary>DPI of the primary monitor right now, where the taskbar and tray normally are.</summary>
    public static int Primary() => ForPoint(new Point(0, 0));

    /// <summary>
    /// The DPI of the monitor the window is on right now (asked from Windows, not WinForms'
    /// cached DeviceDpi, which lags behind while a DPI change is being processed).
    /// </summary>
    public static int Of(Control c)
    {
        if (c is IDpiTracked { TrackedDpi: > 0 } tracked)
            return tracked.TrackedDpi;
        return OfWindow(c);
    }

    /// <summary>Asks Windows for the window's DPI (falls back to the system DPI before a handle exists).</summary>
    public static int OfWindow(Control c)
    {
        int dpi = 0;
        if (c.IsHandleCreated)
        {
            try { dpi = (int)GetDpiForWindow(c.Handle); }
            catch { dpi = c.DeviceDpi; }
        }
        if (dpi <= 0) dpi = Primary();
        return dpi > 0 ? dpi : 96;
    }

    public static float Factor(Control c) => Of(c) / 96f;

    public static int Px(Control c, int designPx) => Px(Of(c), designPx);

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
        int system = ProcessStart;
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
    public static Size FitWindow(int dpi, int designWidth, int designHeight, Rectangle work, int margin)
    {
        int maxW = Math.Max(280, work.Width - margin * 2);
        int maxH = Math.Max(200, work.Height - margin * 2);
        return new Size(
            Math.Min(Px(dpi, designWidth), maxW),
            Math.Min(Px(dpi, designHeight), maxH));
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
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
