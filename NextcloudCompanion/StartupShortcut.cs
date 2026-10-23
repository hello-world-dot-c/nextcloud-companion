namespace NextcloudCompanion;

internal static class StartupShortcut
{
    private const string ShortcutName = AppInfo.Name + ".lnk";
    private const string LegacyShortcutName = "Nextcloud Status.lnk";

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        ShortcutName);

    public static bool Exists() => File.Exists(ShortcutPath);

    public static void Create(string exePath)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(t)
            ?? throw new InvalidOperationException("Could not create WScript.Shell.");
        dynamic shortcut = shell.CreateShortcut(ShortcutPath);
        shortcut.TargetPath = exePath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";
        shortcut.Description = AppInfo.Name + " in the system tray";
        shortcut.Save();
        DeleteIfExists(LegacyShortcutPath);
    }

    public static void Remove()
    {
        DeleteIfExists(ShortcutPath);
        DeleteIfExists(LegacyShortcutPath);
    }

    private static string LegacyShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        LegacyShortcutName);

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
