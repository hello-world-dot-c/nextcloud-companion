using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NextcloudCompanion;

/// <summary>
/// The app logo for window title bars and the taskbar/Alt-Tab. A Form without an icon shows the generic
/// WinForms one, and the single icon <c>Form.Icon</c> takes is scaled with the DPI the process started
/// with, so the title-bar (small) and Alt-Tab (big) icons are picked from the multi-size .ico for the
/// window's current DPI and set with WM_SETICON. app.ico is embedded as a resource of the same name.
/// </summary>
internal static class AppIcon
{
    private const int WmSetIcon = 0x0080, IconSmall = 0, IconBig = 1, SmCxIcon = 11, SmCxSmIcon = 49;

    private static readonly Dictionary<int, Icon> Cache = [];
    private static byte[]? _bytes;

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    /// <summary>The logo at (about) the given pixel size, or null if the resource is missing.</summary>
    public static Icon? Get(int px)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(px, out var cached))
                return cached;
            if (_bytes is null)
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
                if (stream is null)
                    return null;
                _bytes = new byte[stream.Length];
                stream.ReadExactly(_bytes);
            }
            var icon = new Icon(new MemoryStream(_bytes), px, px);
            Cache[px] = icon;      // kept for the process lifetime: the window only borrows the handle
            return icon;
        }
    }

    /// <summary>Sets the title-bar and Alt-Tab icons for the window's DPI. Call once the handle exists and after DPI changes.</summary>
    public static void ApplyTo(Form form, int dpi)
    {
        if (!form.IsHandleCreated)
            return;
        var small = Get(GetSystemMetricsForDpi(SmCxSmIcon, (uint)dpi));
        var big = Get(GetSystemMetricsForDpi(SmCxIcon, (uint)dpi));
        if (small is not null)
            SendMessage(form.Handle, WmSetIcon, IconSmall, small.Handle);
        if (big is not null)
            SendMessage(form.Handle, WmSetIcon, IconBig, big.Handle);
    }
}
