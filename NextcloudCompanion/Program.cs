using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace NextcloudCompanion;

internal static class Program
{
    private const string MutexName = "Local\\NextcloudCompanion_SingleInstance_B7C41E2A";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase)))
        {
            DumpStatus();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            ErrorLog.Write("UI thread exception", e.Exception);
            Dialogs.Message(e.Exception.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                ErrorLog.Write("Unhandled exception", ex);
        };

        if (args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase)))
        {
            ShowOnce();
            return;
        }

        int searchArg = Array.FindIndex(args, a => a.Equals("--search", StringComparison.OrdinalIgnoreCase));
        if (searchArg >= 0)
        {
            DumpSearch(searchArg + 1 < args.Length ? args[searchArg + 1] : "");
            return;
        }

        // A restarted instance (after a display/DPI change) waits for its predecessor to release the mutex.
        bool restarted = args.Any(a => a.Equals("--restarted", StringComparison.OrdinalIgnoreCase));
        var deadline = DateTime.UtcNow.AddSeconds(restarted ? 10 : 0);
        Mutex mutex;
        bool createdNew;
        while (true)
        {
            mutex = new Mutex(initiallyOwned: true, name: MutexName, out createdNew);
            if (createdNew || DateTime.UtcNow >= deadline)
                break;
            mutex.Dispose();
            Thread.Sleep(200);
        }
        using var mutexHolder = mutex;
        if (!createdNew)
            return;

        try
        {
            Application.Run(new TrayApplicationContext());
        }
        catch (Exception ex)
        {
            Dialogs.Message(
                $"{AppInfo.Name} encountered an unhandled error and must exit.\n\n{ex.Message}",
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void DumpStatus()
    {
        var status = new StatusMonitor().Scan();
        var lines = new List<string>
        {
            $"Overall: {status.Overall}",
            $"Client running: {status.ProcessRunning}",
            $"Summary: {status.Summary}",
            $"Checked: {status.CheckedAt:yyyy-MM-dd HH:mm:ss}",
            $"Log: {status.LogFile}",
            ""
        };
        foreach (var f in status.Folders)
        {
            lines.Add($"{f.DisplayName}  [{f.Account}]");
            lines.Add($"  path:    {f.LocalPath}");
            lines.Add($"  health:  {f.Health}{(f.Paused ? " (paused)" : "")}");
            lines.Add($"  sync:    {f.LastSync}");
            lines.Add($"  error:   {f.ErrorMessage}");
            lines.Add($"  item:    {f.ErrorPath}");
            lines.Add($"  when:    {f.LastError}");
            lines.Add("");
        }
        var path = Path.Combine(Path.GetTempPath(), "nextcloud-status-dump.txt");
        File.WriteAllText(path, string.Join(Environment.NewLine, lines));
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private static void DumpSearch(string query)
    {
        IReadOnlyList<FolderStatus> folders;
        try { folders = new StatusMonitor().Scan().Folders; }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "nextcloud-status-search.txt"), ex.ToString());
            return;
        }
        var hits = FileSearch.Find(folders, query);
        var lines = new List<string>
        {
            $"Query: {query}",
            $"Hits: {hits.Count}",
            ""
        };
        foreach (var h in hits)
            lines.Add($"{(h.IsDirectory ? "DIR " : "FILE")}  {h.InternalPath}  [{h.Account}]");
        var path = Path.Combine(Path.GetTempPath(), "nextcloud-status-search.txt");
        File.WriteAllText(path, string.Join(Environment.NewLine, lines));
    }

    private static void ShowOnce()
    {
        var window = new StatusWindow { HideInsteadOfClose = false };
        window.ShowStatus(new StatusMonitor().Scan(), Cursor.Position);
        Application.Run(window);
    }
}
