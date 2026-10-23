namespace NextcloudCompanion;

internal static class ErrorLog
{
    private static readonly string PathFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NextcloudCompanion", "error.log");

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
