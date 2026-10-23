using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace NextcloudCompanion;

/// <summary>
/// Reads nextcloud.cfg plus recent desktop-client logs and produces a per-folder health snapshot.
/// The Nextcloud tray icon often shows a red X with no text; the actual reason is in these logs.
/// </summary>
public sealed class StatusMonitor
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nextcloud");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "nextcloud.cfg");
    private static readonly string LogDir = Path.Combine(ConfigDir, "logs");

    private static readonly Regex Timestamp = new(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})",
        RegexOptions.Compiled);

    private static readonly Regex SyncStarted = new(
        @"Sync started for folder \[(?<folder>[^\]]+)\] of account \[(?<account>[^\]]+)\]",
        RegexOptions.Compiled);

    private static readonly Regex SyncFinished = new(
        @"Sync finished for folder \[(?<folder>[^\]]+)\] of account \[(?<account>[^\]]+)\]",
        RegexOptions.Compiled);

    private static readonly Regex PropagationError = new(
        @"Could not complete propagation of ""(?<path>[^""]+)"".*status OCC::SyncFileItem::(?<status>\w+).*error: ""(?<msg>[^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex NotificationError = new(
        @"Error successfully added to the notification list:.*?""(?<path>[^""]+)"" ""(?<msg>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex EngineResult = new(
        @"SyncEngine finished (?<result>with ERROR|without problem)",
        RegexOptions.Compiled);

    private static readonly Regex EtagCheck = new(
        @"Trying to check ""(?<url>[^""]+)"" for changes via ETag check\. \(time since last sync: (?<secs>\d+) s\)",
        RegexOptions.Compiled);

    private static readonly Regex EtagFinished = new(
        @"Request Etag of QUrl\(""(?<url>[^""]+)""\) FINISHED WITH STATUS ""(?<status>[^""]+)""",
        RegexOptions.Compiled);

    // FolderMan logs this when ConnectionValidator fails (server down, DNS, refused, …).
    private static readonly Regex AccountDisconnected = new(
        @"Account ""(?<account>[^""]+)"" disconnected or paused",
        RegexOptions.Compiled);

    private static readonly Regex AccountConnected = new(
        @"Account ""(?<account>[^""]+)"" connected, scheduling",
        RegexOptions.Compiled);

    private static readonly Regex CheckServerCreated = new(
        @"OCC::CheckServerJob created for ""(?<url>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex StatusPhpError = new(
        @"error: status\.php replied",
        RegexOptions.Compiled);

    public ClientStatus Snapshot { get; private set; } = new() { ProcessRunning = false };

    public ClientStatus Scan()
    {
        bool running = ProcessRunning();
        var folders = ReadConfiguredFolders();

        if (folders.Count == 0)
        {
            Snapshot = new ClientStatus
            {
                ProcessRunning = running,
                Overall = running ? Health.Unknown : Health.Offline,
                Folders = []
            };
            return Snapshot;
        }

        var byPath = folders.ToDictionary(f => Normalize(f.LocalPath), StringComparer.OrdinalIgnoreCase);
        ApplyLog(byPath);

        foreach (var folder in folders)
        {
            if (!running)
                folder.Health = Health.Offline;
            else if (folder.Paused && folder.Health == Health.Unknown)
                folder.Health = Health.Ok;
        }

        Health overall;
        if (!running)
            overall = Health.Offline;
        else if (folders.Any(f => f.Health == Health.Error))
            overall = Health.Error;
        else if (folders.Any(f => f.Health == Health.Disconnected))
            overall = Health.Disconnected;
        else if (folders.Any(f => f.Health == Health.Warning))
            overall = Health.Warning;
        else if (folders.Any(f => f.Health == Health.Ok))
            overall = Health.Ok;
        else
            overall = Health.Unknown;

        string? logFile = null;
        try
        {
            logFile = Directory.Exists(LogDir)
                ? new DirectoryInfo(LogDir).GetFiles("*nextcloud.log*")
                    .Where(f => !f.Name.Contains("permanent_delete", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault()?.FullName
                : null;
        }
        catch { /* ignore */ }

        Snapshot = new ClientStatus
        {
            ProcessRunning = running,
            Overall = overall,
            Folders = folders,
            CheckedAt = DateTime.Now,
            LogFile = logFile
        };
        return Snapshot;
    }

    private static bool ProcessRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("nextcloud").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static List<FolderStatus> ReadConfiguredFolders()
    {
        var result = new List<FolderStatus>();
        if (!File.Exists(ConfigPath))
            return result;

        string text;
        try
        {
            text = File.ReadAllText(ConfigPath);
        }
        catch
        {
            return result;
        }

        var accounts = new Dictionary<string, (string Url, string User)>(StringComparer.Ordinal);
        var folderAcc = new Dictionary<(string Acc, string Id), FolderDraft>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('['))
                continue;

            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq];
            var value = line[(eq + 1)..];

            var parts = key.Split('\\');
            if (parts.Length == 2 && parts[1] == "url")
            {
                var slot = accounts.GetValueOrDefault(parts[0]);
                accounts[parts[0]] = (value, slot.User);
            }
            else if (parts.Length == 2 && parts[1] == "dav_user")
            {
                var slot = accounts.GetValueOrDefault(parts[0]);
                accounts[parts[0]] = (slot.Url, value);
            }
            else if (parts.Length == 4 && parts[1] == "Folders")
            {
                var id = (parts[0], parts[2]);
                if (!folderAcc.TryGetValue(id, out var draft))
                    draft = new FolderDraft { AccountKey = parts[0] };
                switch (parts[3])
                {
                    case "localPath":   draft.LocalPath   = value.Replace('/', Path.DirectorySeparatorChar); break;
                    case "targetPath":  draft.TargetPath  = value; break;
                    case "journalPath": draft.JournalPath = value; break;
                    case "paused":      draft.Paused      = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                }
                folderAcc[id] = draft;
            }
        }

        foreach (var draft in folderAcc.Values)
        {
            if (string.IsNullOrWhiteSpace(draft.LocalPath))
                continue;
            accounts.TryGetValue(draft.AccountKey, out var acc);
            var host = HostOf(acc.Url);
            var user = acc.User;
            var local = draft.LocalPath.TrimEnd('\\', '/');
            result.Add(new FolderStatus
            {
                LocalPath    = local,
                DisplayName  = Path.GetFileName(local),
                Account      = string.IsNullOrEmpty(user) ? host : $"{user}@{host}",
                DavUser      = user ?? "",
                ServerHost   = host,
                ServerUrl    = (acc.Url ?? "").TrimEnd('/'),
                TargetPath   = string.IsNullOrWhiteSpace(draft.TargetPath) ? "/" : draft.TargetPath,
                JournalPath  = draft.JournalPath,
                Paused       = draft.Paused,
                Health       = Health.Unknown
            });
        }

        return result.OrderBy(f => f.Account).ThenBy(f => f.DisplayName).ToList();
    }

    private static string HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
    }

    private static void ApplyLog(Dictionary<string, FolderStatus> folders)
    {
        var files = EnumerateRecentLogs().ToList();
        FolderStatus? current = null;
        string? lastCheckServerUrl = null;

        foreach (var file in files)
        {
            foreach (var (time, line) in ReadLogLines(file))
            {
                var started = SyncStarted.Match(line);
                if (started.Success)
                {
                    current = MatchFolder(folders, started.Groups["folder"].Value);
                    continue;
                }

                var finished = SyncFinished.Match(line);
                if (finished.Success)
                {
                    current = MatchFolder(folders, finished.Groups["folder"].Value);
                    if (current is not null)
                        current.LastSync = time;
                    continue;
                }

                var prop = PropagationError.Match(line);
                if (prop.Success)
                {
                    var status = prop.Groups["status"].Value;
                    if (status is "FileIgnored" or "SoftError")
                    {
                        // Ignored Office lock files etc. should not turn the icon red.
                        continue;
                    }
                    ApplyError(current, folders, time, prop.Groups["path"].Value, prop.Groups["msg"].Value,
                        isWarning: status is "RestorationError");
                    continue;
                }

                var notified = NotificationError.Match(line);
                if (notified.Success)
                {
                    ApplyError(current, folders, time, notified.Groups["path"].Value, notified.Groups["msg"].Value, isWarning: false);
                    continue;
                }

                var engine = EngineResult.Match(line);
                if (engine.Success && current is not null)
                {
                    if (engine.Groups["result"].Value == "without problem")
                    {
                        current.Health = Health.Ok;
                        current.ErrorPath = null;
                        current.ErrorMessage = null;
                        current.LastError = null;
                        current.LastSync = time;
                    }
                    else if (current.Health != Health.Error)
                    {
                        current.Health = Health.Error;
                        current.LastError = time;
                        current.ErrorMessage ??= "Sync finished with errors";
                    }
                    continue;
                }

                var etag = EtagCheck.Match(line);
                if (etag.Success)
                {
                    if (int.TryParse(etag.Groups["secs"].Value, out int secs))
                    {
                        var ago = time.AddSeconds(-secs);
                        foreach (var folder in FoldersForUrl(folders, etag.Groups["url"].Value))
                        {
                            if (folder.Health == Health.Unknown)
                                folder.LastSync = ago;
                        }
                    }
                    continue;
                }

                var etagDone = EtagFinished.Match(line);
                if (etagDone.Success)
                {
                    string status = etagDone.Groups["status"].Value;
                    var targets = FoldersForUrl(folders, etagDone.Groups["url"].Value);
                    if (status.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var folder in targets)
                        {
                            if (folder.Health is Health.Unknown or Health.Disconnected)
                                folder.Health = Health.Ok;
                        }
                    }
                    else
                    {
                        MarkDisconnected(targets, time, status);
                    }
                    continue;
                }

                var created = CheckServerCreated.Match(line);
                if (created.Success)
                {
                    lastCheckServerUrl = created.Groups["url"].Value;
                    continue;
                }

                if (StatusPhpError.IsMatch(line) && lastCheckServerUrl is not null)
                {
                    MarkDisconnected(FoldersForUrl(folders, lastCheckServerUrl), time, "Server did not respond");
                    continue;
                }

                var disc = AccountDisconnected.Match(line);
                if (disc.Success)
                {
                    MarkDisconnected(FoldersForAccount(folders, disc.Groups["account"].Value), time, "Server unreachable");
                    continue;
                }

                var conn = AccountConnected.Match(line);
                if (conn.Success)
                {
                    foreach (var folder in FoldersForAccount(folders, conn.Groups["account"].Value))
                    {
                        if (folder.Health == Health.Disconnected)
                        {
                            folder.Health = Health.Unknown;
                            folder.ErrorMessage = null;
                            folder.ErrorPath = null;
                            folder.LastError = null;
                        }
                    }
                }
            }
        }
    }

    private static void MarkDisconnected(IEnumerable<FolderStatus> targets, DateTime time, string message)
    {
        foreach (var folder in targets)
        {
            folder.Health = Health.Disconnected;
            folder.ErrorMessage = message;
            folder.ErrorPath = null;
            folder.LastError = time;
        }
    }

    private static void ApplyError(
        FolderStatus? current,
        Dictionary<string, FolderStatus> folders,
        DateTime time,
        string relativePath,
        string message,
        bool isWarning)
    {
        var target = current ?? MatchFolderByRelative(folders, relativePath);
        if (target is null) return;

        var health = isWarning ? Health.Warning : Health.Error;
        // Don't let a later warning overwrite an error in the same scan.
        if (target.Health == Health.Error && health == Health.Warning)
            return;

        target.Health = health;
        target.ErrorPath = relativePath;
        target.ErrorMessage = string.IsNullOrWhiteSpace(message) ? "Sync error" : message;
        target.LastError = time;
    }

    private static FolderStatus? MatchFolder(Dictionary<string, FolderStatus> folders, string path)
        => folders.TryGetValue(Normalize(path), out var f) ? f : null;

    private static List<FolderStatus> FoldersForUrl(Dictionary<string, FolderStatus> folders, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            return [];
        return folders.Values
            .Where(f => f.ServerHost.Equals(uri.Host, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<FolderStatus> FoldersForAccount(Dictionary<string, FolderStatus> folders, string account)
    {
        if (string.IsNullOrWhiteSpace(account))
            return [];
        var exact = folders.Values
            .Where(f => f.Account.Equals(account, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count > 0)
            return exact;
        return folders.Values
            .Where(f => !string.IsNullOrEmpty(f.ServerHost)
                        && account.Contains(f.ServerHost, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static FolderStatus? MatchFolderByRelative(Dictionary<string, FolderStatus> folders, string relative)
    {
        // If we somehow missed Sync started, attribute the error to the only matching folder,
        // or the first folder whose local tree could contain this relative path.
        if (folders.Count == 1)
            return folders.Values.First();
        return folders.Values.FirstOrDefault(f =>
            relative.StartsWith(f.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path)
        => path.Replace('/', Path.DirectorySeparatorChar).TrimEnd('\\');

    private static IEnumerable<FileInfo> EnumerateRecentLogs()
    {
        if (!Directory.Exists(LogDir))
            yield break;

        var cutoff = DateTime.Now.AddHours(-12);
        var files = new DirectoryInfo(LogDir)
            .GetFiles("*nextcloud.log*")
            .Where(f => !f.Name.Contains("permanent_delete", StringComparison.OrdinalIgnoreCase))
            .Where(f => f.LastWriteTime >= cutoff)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        // Newest uncompressed current log plus the previous session — enough for "why is it red now?"
        // Walk oldest→newest so later results overwrite.
        foreach (var f in files.TakeLast(6))
            yield return f;
    }

    private static IEnumerable<(DateTime Time, string Line)> ReadLogLines(FileInfo file)
    {
        FileStream fileStream;
        try
        {
            if (!file.Exists)
                yield break;
            fileStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            yield break;
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }

        Stream stream = fileStream;
        try
        {
            bool gzipped = file.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
            if (!gzipped && fileStream.Length > 1_500_000)
            {
                fileStream.Seek(-1_200_000, SeekOrigin.End);
                while (fileStream.Position < fileStream.Length)
                {
                    int b = fileStream.ReadByte();
                    if (b == '\n' || b < 0) break;
                }
            }

            if (gzipped)
                stream = new GZipStream(fileStream, CompressionMode.Decompress, leaveOpen: false);

            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var m = Timestamp.Match(line);
                if (!m.Success) continue;
                if (!DateTime.TryParseExact(m.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
                    continue;
                yield return (ts, line);
            }
        }
        finally
        {
            stream.Dispose();
        }
    }

    private struct FolderDraft
    {
        public string AccountKey;
        public string? LocalPath;
        public string? TargetPath;
        public string? JournalPath;
        public bool Paused;
    }
}
