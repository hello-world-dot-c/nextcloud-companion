using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace NextcloudCompanion;

public enum NotificationLevel
{
    Info,
    Warning,
    Error
}

/// <summary>Where notifications end up (Windows toasts; a fake in tests).</summary>
public interface INotificationSink
{
    void Show(string title, string text, NotificationLevel level);
}

/// <summary>
/// Decides whether a notification is shown at all. Every notification has a key and a minimum interval for that key,
/// and there is a ceiling on the total number per minute, so a condition that flaps (or a bug) can never flood the
/// screen. What the user wants to hear less of is configured in Windows (Settings, Notifications).
/// </summary>
public sealed class Notifier(INotificationSink sink, IClock clock)
{
    public const int MaxPerMinute = 4;

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _lastShown = new();
    private readonly Queue<long> _recent = new();

    /// <summary>Raised for every notification that is actually shown (for the recovery log).</summary>
    public event Action<string>? Shown;

    public bool Show(string key, string title, string text, NotificationLevel level, TimeSpan minInterval)
    {
        long now = clock.MonotonicMilliseconds;
        lock (_gate)
        {
            if (_lastShown.TryGetValue(key, out long last) && now - last < (long)minInterval.TotalMilliseconds)
                return false;
            while (_recent.Count > 0 && now - _recent.Peek() > 60_000)
                _recent.Dequeue();
            if (_recent.Count >= MaxPerMinute)
                return false;
            _lastShown[key] = now;
            _recent.Enqueue(now);
        }

        Shown?.Invoke($"Notification: {title} - {text}");
        sink.Show(title, text, level);
        return true;
    }
}

/// <summary>
/// Shows notifications through the Windows toast framework, so they appear in Windows' own notification list and can
/// be configured or silenced per app in Settings. The app registers itself under its own application id (a registry
/// entry in the user's profile, no shortcut needed). If toasts cannot be used at all, the old tray balloon is used.
/// </summary>
public sealed class WindowsToastSink(Action<string, string, NotificationLevel> fallback) : INotificationSink
{
    public const string AppId = "hello-world-dot-c.NextcloudCompanion";

    private bool _registered;
    private bool _unavailable;
    private bool _loggedDisabled;

    public void Show(string title, string text, NotificationLevel level)
    {
        if (!_unavailable)
        {
            try
            {
                Register();
                var setting = WinRt.ShowToast(AppId, title, text);
                if (setting != 0 && !_loggedDisabled)
                {
                    // The user (or policy) switched notifications of this app off in Windows; that is respected.
                    _loggedDisabled = true;
                    ErrorLog.Recovery($"Windows notifications for this app are disabled (setting {setting}); nothing is shown.");
                }
                return;
            }
            catch (Exception ex) when (ex is COMException or DllNotFoundException or EntryPointNotFoundException
                                           or InvalidCastException or IOException or UnauthorizedAccessException
                                           or SecurityException or ExternalException)
            {
                _unavailable = true;
                ErrorLog.Write("Windows toast notifications are not available; using tray balloons", ex);
            }
        }
        fallback(title, text, level);
    }

    private void Register()
    {
        if (_registered)
            return;
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
        key.SetValue("DisplayName", AppInfo.Name);
        string? icon = EnsureIconFile();
        if (icon is not null)
            key.SetValue("IconUri", icon);
        _registered = true;
    }

    /// <summary>The toast needs an image file; the program icon is written once next to the settings.</summary>
    private static string? EnsureIconFile()
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NextcloudCompanion");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "toast-icon.png");
            if (File.Exists(path))
                return path;
            using var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "");
            if (icon is null)
                return null;
            using var bitmap = icon.ToBitmap();
            bitmap.Save(path, ImageFormat.Png);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// The few Windows Runtime calls needed for a toast, made directly: the Windows SDK projection that would otherwise
/// be required adds about 25 MB to a program that is otherwise under 1 MB.
/// </summary>
internal static class WinRt
{
    private static readonly Guid IidStatics = new("50AC103F-D235-4598-BBEF-98FE4D1A3AD4");
    private static readonly Guid IidFactory = new("04124B20-82C6-4229-B109-FD9ED4662B53");

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll")]
    private static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);

    // Each interface starts with the three IInspectable methods, which are never called here.

    [ComImport, Guid("50AC103F-D235-4598-BBEF-98FE4D1A3AD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotificationManagerStatics
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        [PreserveSig] int CreateToastNotifier(out IToastNotifier notifier);
        [PreserveSig] int CreateToastNotifierWithId(IntPtr applicationId, out IToastNotifier notifier);
        [PreserveSig] int GetTemplateContent(int type, out IXmlDocument content);
    }

    [ComImport, Guid("75927B93-03F3-41EC-91D3-6E5BAC1B38E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotifier
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        [PreserveSig] int Show(IToastNotification notification);
        [PreserveSig] int Hide(IToastNotification notification);
        [PreserveSig] int GetSetting(out int setting);
    }

    [ComImport, Guid("04124B20-82C6-4229-B109-FD9ED4662B53"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotificationFactory
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        [PreserveSig] int CreateToastNotification(IXmlDocument content, out IToastNotification notification);
    }

    [ComImport, Guid("997E2675-059E-4E60-8B06-1760917C8B80"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotification
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
    }

    [ComImport, Guid("F7F3A506-1E87-42D6-BCFB-B8C809FA5494"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IXmlDocument
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
    }

    [ComImport, Guid("6CD0E74E-EE65-4489-9EBF-CA43E87BA637"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IXmlDocumentIO
    {
        void GetIids();
        void GetRuntimeClassName();
        void GetTrustLevel();
        [PreserveSig] int LoadXml(IntPtr xml);
    }

    private static IntPtr Hs(string text)
    {
        Check(WindowsCreateString(text, text.Length, out var h));
        return h;
    }

    private static void Check(int hr)
    {
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    private static T Factory<T>(string className, Guid iid) where T : class
    {
        IntPtr name = Hs(className);
        try
        {
            Check(RoGetActivationFactory(name, ref iid, out var factory));
            try { return (T)Marshal.GetObjectForIUnknown(factory); }
            finally { Marshal.Release(factory); }
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    /// <summary>Shows a two-line toast for the given application id. Returns Windows' notification setting for the app (0 = enabled).</summary>
    public static int ShowToast(string appId, string title, string text)
    {
        string xml = "<toast><visual><binding template=\"ToastGeneric\"><text>" + SecurityElement.Escape(title)
                     + "</text><text>" + SecurityElement.Escape(text) + "</text></binding></visual></toast>";

        // The XML document.
        IntPtr docName = Hs("Windows.Data.Xml.Dom.XmlDocument");
        object doc;
        try
        {
            Check(RoActivateInstance(docName, out var instance));
            try { doc = Marshal.GetObjectForIUnknown(instance); }
            finally { Marshal.Release(instance); }
        }
        finally
        {
            WindowsDeleteString(docName);
        }
        IntPtr xmlText = Hs(xml);
        try { Check(((IXmlDocumentIO)doc).LoadXml(xmlText)); }
        finally { WindowsDeleteString(xmlText); }

        // The toast, and the notifier of this application.
        var factory = Factory<IToastNotificationFactory>("Windows.UI.Notifications.ToastNotification", IidFactory);
        Check(factory.CreateToastNotification((IXmlDocument)doc, out var toast));

        var statics = Factory<IToastNotificationManagerStatics>("Windows.UI.Notifications.ToastNotificationManager", IidStatics);
        IntPtr id = Hs(appId);
        try
        {
            Check(statics.CreateToastNotifierWithId(id, out var notifier));
            // Windows answers "element not found" for the setting of an app it has not listed yet; that means enabled.
            int setting = notifier.GetSetting(out int reported) >= 0 ? reported : 0;
            if (setting == 0)
                Check(notifier.Show(toast));
            return setting;
        }
        finally
        {
            WindowsDeleteString(id);
        }
    }
}
