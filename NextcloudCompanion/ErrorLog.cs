namespace NextcloudCompanion;

internal static class ErrorLog
{
    private static readonly string PathFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NextcloudCompanion", "error.log");

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NextcloudCompanion");

    /// <summary>Diagnostics for DPI/size changes (rare events; the file is trimmed when it grows past 200 KB).</summary>
    public static void Info(string message) => Append("dpi.log", message);

    /// <summary>Why Companion did or did not restart Nextcloud, and how it ended. Local only, never uploaded.</summary>
    public static void Recovery(string message) => Append("recovery.log", message);

    private static void Append(string fileName, string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string file = Path.Combine(Folder, fileName);
            if (File.Exists(file) && new FileInfo(file).Length > 200_000)
                File.Delete(file);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the tray down.
        }
    }

    public static void Write(string title, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
            File.AppendAllText(PathFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {title}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the tray down.
        }
    }
}
