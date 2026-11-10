using System.Text.RegularExpressions;

namespace NextcloudCompanion;

public sealed class LogAnalysis
{
    /// <summary>The logs contained per-folder sync state records.</summary>
    public bool SawSyncStates { get; set; }
    /// <summary>Records about a folder or account that could not be attributed to exactly one folder.</summary>
    public int Unattributed { get; set; }
    /// <summary>State texts that were not recognised.</summary>
    public int UnknownStates { get; set; }
    /// <summary>How many recognised sync records were found at all.</summary>
    public int RecognisedRecords { get; set; }
}

/// <summary>
/// Turns the relevant log records of the Nextcloud client (35.x formats) into per-folder evidence: activity, last
/// sync start, last completed sync, last successful sync, errors and connection state. Records that cannot be
/// attributed to exactly one folder are counted but never assigned to a guess.
/// </summary>
public static class LogAnalyzer
{
    // "Sync state changed for folder  "https://host/remote.php/dav/files/user/dir/" :  "Not yet started""
    private static readonly Regex StateChanged = new(
        @"Sync state changed for folder\s+""(?<url>[^""]+)""\s*:\s*""(?<state>[^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex SyncStarted = new(
        @"Sync started for folder \[(?<folder>[^\]]+)\] of account \[(?<account>[^\]]+)\](?: with remote \[(?<remote>[^\]]*)\])?",
        RegexOptions.Compiled);

    private static readonly Regex SyncFinished = new(
        @"Sync finished for folder \[(?<folder>[^\]]+)\] of account \[(?<account>[^\]]+)\](?: with remote \[(?<remote>[^\]]*)\])?",
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

    public static LogAnalysis Apply(IReadOnlyList<FolderStatus> folders, IEnumerable<LogRecord> records)
    {
        var analysis = new LogAnalysis();
        var resolver = new FolderResolver(folders);
        FolderStatus? current = null;
        string? lastCheckServerUrl = null;

        foreach (var (time, line) in records)
        {
            var state = StateChanged.Match(line);
            if (state.Success)
            {
                analysis.SawSyncStates = true;
                analysis.RecognisedRecords++;
                var target = resolver.ByRemote(state.Groups["url"].Value, out bool ambiguous);
                if (target is null)
                {
                    analysis.Unattributed++;
                    continue;
                }
                if (!ApplyState(target, time, state.Groups["state"].Value))
                    analysis.UnknownStates++;
                continue;
            }

            var started = SyncStarted.Match(line);
            if (started.Success)
            {
                analysis.RecognisedRecords++;
                current = resolver.ByFolderRecord(started, out _);
                if (current is null)
                    analysis.Unattributed++;
                else
                {
                    current.LastSyncStartedAt = time;
                    Observe(current, time);
                }
                continue;
            }

            var finished = SyncFinished.Match(line);
            if (finished.Success)
            {
                analysis.RecognisedRecords++;
                current = resolver.ByFolderRecord(finished, out _);
                if (current is null)
                    analysis.Unattributed++;
                else
                {
                    // The run ended; whether it succeeded is decided by the engine result or the state record.
                    current.LastSyncCompletedAt = time;
                    Observe(current, time);
                }
                continue;
            }

            var prop = PropagationError.Match(line);
            if (prop.Success)
            {
                analysis.RecognisedRecords++;
                string status = prop.Groups["status"].Value;
                if (status is "FileIgnored" or "SoftError")
                    continue;       // ignored Office lock files etc. must not turn the icon red
                if (!ApplyError(current ?? resolver.ByRelativePath(prop.Groups["path"].Value), time,
                        prop.Groups["path"].Value, prop.Groups["msg"].Value, isWarning: status is "RestorationError"))
                    analysis.Unattributed++;
                continue;
            }

            var notified = NotificationError.Match(line);
            if (notified.Success)
            {
                analysis.RecognisedRecords++;
                if (!ApplyError(current ?? resolver.ByRelativePath(notified.Groups["path"].Value), time,
                        notified.Groups["path"].Value, notified.Groups["msg"].Value, isWarning: false))
                    analysis.Unattributed++;
                continue;
            }

            var engine = EngineResult.Match(line);
            if (engine.Success)
            {
                analysis.RecognisedRecords++;
                if (current is null)
                {
                    analysis.Unattributed++;
                    continue;
                }
                if (engine.Groups["result"].Value == "without problem")
                {
                    MarkSuccess(current, time);
                }
                else if (current.Health != Health.Error)
                {
                    current.Health = Health.Error;
                    current.LastError = time;
                    current.ErrorMessage ??= "Sync finished with errors";
                }
                Observe(current, time);
                continue;
            }

            var etag = EtagCheck.Match(line);
            if (etag.Success)
            {
                analysis.RecognisedRecords++;
                if (int.TryParse(etag.Groups["secs"].Value, out int secs))
                {
                    var ago = time.AddSeconds(-secs);
                    foreach (var folder in resolver.ForUrl(etag.Groups["url"].Value))
                    {
                        // "time since last sync" is the client's own bookkeeping of the last finished run.
                        if (folder.LastSyncCompletedAt is null || ago > folder.LastSyncCompletedAt)
                            folder.LastSyncCompletedAt = ago;
                    }
                }
                continue;
            }

            var etagDone = EtagFinished.Match(line);
            if (etagDone.Success)
            {
                analysis.RecognisedRecords++;
                var targets = resolver.ForUrl(etagDone.Groups["url"].Value);
                if (etagDone.Groups["status"].Value.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
                {
                    // The server answered. That is connection evidence only; it says nothing about a sync result.
                    foreach (var folder in targets)
                    {
                        folder.LastConnectionCheckAt = time;
                        if (folder.Health == Health.Disconnected)
                            ClearConnectionProblem(folder);
                    }
                }
                else
                {
                    MarkDisconnected(targets, time, etagDone.Groups["status"].Value);
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
                analysis.RecognisedRecords++;
                MarkDisconnected(resolver.ForUrl(lastCheckServerUrl), time, "Server did not respond");
                continue;
            }

            var disc = AccountDisconnected.Match(line);
            if (disc.Success)
            {
                analysis.RecognisedRecords++;
                MarkDisconnected(resolver.ForAccount(disc.Groups["account"].Value), time, "Server unreachable");
                continue;
            }

            var conn = AccountConnected.Match(line);
            if (conn.Success)
            {
                analysis.RecognisedRecords++;
                foreach (var folder in resolver.ForAccount(conn.Groups["account"].Value))
                {
                    folder.AccountConnectedAt = time;
                    if (folder.Health == Health.Disconnected)
                        ClearConnectionProblem(folder);
                }
            }
        }

        return analysis;
    }

    /// <summary>Returns false for a state text that is not recognised.</summary>
    private static bool ApplyState(FolderStatus folder, DateTime time, string text)
    {
        folder.LastObservedAt = time;
        text = text.Trim();

        if (text.StartsWith("Not yet started", StringComparison.OrdinalIgnoreCase))
            SetActivity(folder, SyncActivity.Waiting, time);
        else if (text.StartsWith("Preparing", StringComparison.OrdinalIgnoreCase))
            SetActivity(folder, SyncActivity.Preparing, time);
        else if (text.StartsWith("Sync running", StringComparison.OrdinalIgnoreCase))
            SetActivity(folder, SyncActivity.Syncing, time);
        else if (text.StartsWith("Success", StringComparison.OrdinalIgnoreCase))
        {
            MarkSuccess(folder, time);
            SetActivity(folder, SyncActivity.Idle, time);
        }
        else if (text.Equals("Error", StringComparison.OrdinalIgnoreCase) || text.Equals("Setup Error", StringComparison.OrdinalIgnoreCase))
        {
            folder.Health = Health.Error;
            folder.LastError = time;
            folder.ErrorMessage ??= "Sync finished with errors";
            folder.LastSyncCompletedAt = time;
            SetActivity(folder, SyncActivity.Idle, time);
        }
        else
            return false;
        return true;
    }

    private static void SetActivity(FolderStatus folder, SyncActivity activity, DateTime time)
    {
        // A repeated record of the same state does not restart the clock of that state.
        if (folder.Activity == activity && folder.ActivitySince is not null)
            return;
        folder.Activity = activity;
        folder.ActivitySince = time;
    }

    private static void MarkSuccess(FolderStatus folder, DateTime time)
    {
        folder.Health = Health.Ok;
        folder.ErrorPath = null;
        folder.ErrorMessage = null;
        folder.LastError = null;
        folder.LastSyncCompletedAt = time;
        folder.LastSuccessfulSyncAt = time;
    }

    private static void Observe(FolderStatus folder, DateTime time)
    {
        if (folder.LastObservedAt is null || time > folder.LastObservedAt)
            folder.LastObservedAt = time;
    }

    private static void ClearConnectionProblem(FolderStatus folder)
    {
        folder.Health = Health.Unknown;
        folder.ErrorMessage = null;
        folder.ErrorPath = null;
        folder.LastError = null;
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

    private static bool ApplyError(FolderStatus? target, DateTime time, string relativePath, string message, bool isWarning)
    {
        if (target is null)
            return false;

        var health = isWarning ? Health.Warning : Health.Error;
        // Don't let a later warning overwrite an error in the same scan.
        if (target.Health == Health.Error && health == Health.Warning)
            return true;

        target.Health = health;
        target.ErrorPath = relativePath;
        target.ErrorMessage = string.IsNullOrWhiteSpace(message) ? "Sync error" : message;
        target.LastError = time;
        return true;
    }

    /// <summary>
    /// Maps log texts to folders. A record is attributed only when exactly one folder fits; ambiguity yields null.
    /// </summary>
    internal sealed class FolderResolver(IReadOnlyList<FolderStatus> folders)
    {
        /// <summary>Resolves the remote folder URL the client logs ("https://host/remote.php/dav/files/user/dir/").</summary>
        public FolderStatus? ByRemote(string url, out bool ambiguous)
        {
            ambiguous = false;
            if (!TryParseRemote(url, out string host, out string? user, out string target))
                return null;

            var onServer = folders.Where(f => f.ServerHost.Equals(host, StringComparison.OrdinalIgnoreCase)
                                              && (user is null || f.DavUser.Equals(user, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var exact = onServer.Where(f => NormalizeRemote(f.TargetPath).Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1)
                return exact[0];
            if (exact.Count > 1)
            {
                ambiguous = true;
                return null;
            }
            if (onServer.Count == 1)
                return onServer[0];
            ambiguous = onServer.Count > 1;
            return null;
        }

        /// <summary>"Sync started/finished for folder [local] of account [account] with remote [url]".</summary>
        public FolderStatus? ByFolderRecord(Match record, out bool ambiguous)
        {
            ambiguous = false;
            string remote = record.Groups["remote"].Success ? record.Groups["remote"].Value : "";
            if (remote.Length > 0)
            {
                var byRemote = ByRemote(remote, out ambiguous);
                if (byRemote is not null)
                    return byRemote;
            }

            string path = NormalizeLocal(record.Groups["folder"].Value);
            var candidates = folders.Where(f => NormalizeLocal(f.LocalPath).Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count > 1)
            {
                string account = record.Groups["account"].Value;
                var narrowed = candidates.Where(f => AccountMatches(f, account)).ToList();
                if (narrowed.Count > 0)
                    candidates = narrowed;
            }
            if (candidates.Count == 1)
                return candidates[0];
            ambiguous = candidates.Count > 1;
            return null;
        }

        /// <summary>A path relative to the sync root; only attributed when exactly one folder can own it.</summary>
        public FolderStatus? ByRelativePath(string relative)
        {
            if (folders.Count == 1)
                return folders[0];
            var matches = folders.Where(f => relative.StartsWith(f.DisplayName, StringComparison.OrdinalIgnoreCase)).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        /// <summary>Folders an ETag or connection record is about: by remote URL, else everything on that host.</summary>
        public List<FolderStatus> ForUrl(string url)
        {
            var one = ByRemote(url, out bool ambiguous);
            if (one is not null)
                return [one];
            if (ambiguous && TryParseRemote(url, out string h1, out string? u1, out string t1))
                return folders.Where(f => f.ServerHost.Equals(h1, StringComparison.OrdinalIgnoreCase)
                                          && (u1 is null || f.DavUser.Equals(u1, StringComparison.OrdinalIgnoreCase))
                                          && NormalizeRemote(f.TargetPath).Equals(t1, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
                return [];
            return folders.Where(f => f.ServerHost.Equals(uri.Host, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public List<FolderStatus> ForAccount(string account)
        {
            if (string.IsNullOrWhiteSpace(account))
                return [];
            var exact = folders.Where(f => f.Account.Equals(account, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0)
                return exact;
            return folders.Where(f => AccountMatches(f, account)).ToList();
        }

        private static bool AccountMatches(FolderStatus f, string account)
        {
            if (f.Account.Equals(account, StringComparison.OrdinalIgnoreCase))
                return true;
            bool host = !string.IsNullOrEmpty(f.ServerHost) && account.Contains(f.ServerHost, StringComparison.OrdinalIgnoreCase);
            bool user = !string.IsNullOrEmpty(f.DavUser) && account.Contains(f.DavUser, StringComparison.OrdinalIgnoreCase);
            return host && (user || string.IsNullOrEmpty(f.DavUser));
        }

        internal static bool TryParseRemote(string url, out string host, out string? user, out string target)
        {
            host = "";
            user = null;
            target = "/";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
                return false;
            host = uri.Host;
            string path = Uri.UnescapeDataString(uri.AbsolutePath);

            int i = path.IndexOf("/remote.php/dav/files/", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                string rest = path[(i + "/remote.php/dav/files/".Length)..];
                int slash = rest.IndexOf('/');
                user = slash < 0 ? rest : rest[..slash];
                target = NormalizeRemote(slash < 0 ? "/" : rest[slash..]);
                return true;
            }
            i = path.IndexOf("/remote.php/webdav", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                target = NormalizeRemote(path[(i + "/remote.php/webdav".Length)..]);
                return true;
            }
            return true;    // a plain server URL: only the host is known
        }

        private static string NormalizeRemote(string path)
        {
            path = (path ?? "/").Replace('\\', '/').Trim();
            if (!path.StartsWith('/'))
                path = "/" + path;
            path = path.TrimEnd('/');
            return path.Length == 0 ? "/" : path;
        }

        private static string NormalizeLocal(string path)
            => path.Replace('/', Path.DirectorySeparatorChar).TrimEnd('\\');
    }
}
