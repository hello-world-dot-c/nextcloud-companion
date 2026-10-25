namespace NextcloudCompanion;

internal static class ErrorLog
{
    private static readonly string PathFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NextcloudCompanion", "error.log");

    private static readonly string InfoFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NextcloudCompanion", "dpi.log");

    /// <summary>Diagnostics for DPI/size changes (rare events; the file is trimmed when it grows past 200 KB).</summary>
    public static void Info(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InfoFile)!);
            if (File.Exists(InfoFile) && new FileInfo(InfoFile).Length > 200_000)
                File.Delete(InfoFile);
            File.AppendAllText(InfoFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
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
