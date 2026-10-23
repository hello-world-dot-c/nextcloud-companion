using Microsoft.Data.Sqlite;

namespace NextcloudCompanion;

public sealed class FileHit
{
    public required string Name { get; init; }
    /// <summary>Full path inside Nextcloud, using forward slashes, e.g. <c>/Acme (shared-ext)/Marketing/Readme.md</c>.</summary>
    public required string InternalPath { get; init; }
    public required string LocalPath { get; init; }
    public required string Account { get; init; }
    public required string ServerUrl { get; init; }
    public required bool IsDirectory { get; init; }
    /// <summary>False when the journal knows the item but it is not on this PC (selective sync / not downloaded).</summary>
    public bool ExistsLocally { get; init; }
    /// <summary>Last content change from the sync journal (<c>modtime</c>) or the local file.</summary>
    public DateTime? Modified { get; init; }

    public string ModifiedLabel => Modified is DateTime t ? FolderStatus.FormatTime(t) : "";

    public string KindLabel => IsDirectory
        ? (ExistsLocally ? "Folder" : "Remote folder")
        : (ExistsLocally ? "File" : "Remote file");

    /// <summary>
    /// Files app URL for this item. Directories open as the current folder;
    /// files open the parent folder and scroll to the name.
    /// </summary>
    public string? WebUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ServerUrl))
                return null;

            string dir = IsDirectory ? InternalPath : ParentInternal(InternalPath);
            if (string.IsNullOrEmpty(dir))
                dir = "/";

            string url = ServerUrl.TrimEnd('/') + "/apps/files/?dir=" + EncodeDir(dir);
            if (!IsDirectory)
                url += "&scrollto=" + Uri.EscapeDataString(Name);
            return url;
        }
    }

    private static string ParentInternal(string path)
    {
        string trimmed = path.Replace('\\', '/').TrimEnd('/');
        int i = trimmed.LastIndexOf('/');
        return i <= 0 ? "/" : trimmed[..i];
    }

    private static string EncodeDir(string path)
    {
        string trimmed = path.Replace('\\', '/').Trim('/');
        if (trimmed.Length == 0)
            return "/";
        return "/" + string.Join("/", trimmed.Split('/').Select(Uri.EscapeDataString));
    }
}

/// <summary>
/// Finds files and folders by a substring of their name (not of the whole path).
/// Prefers the Nextcloud sync journal so results use the same internal paths as the server.
/// </summary>
public static class FileSearch
{
    public const int DefaultLimit = 400;

    public static IReadOnlyList<FileHit> Find(
        IEnumerable<FolderStatus> folders,
        string query,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0)
            return [];

        var hits = new List<FileHit>();
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hits.Count >= limit)
                break;

            try
            {
                if (!SearchJournal(folder, query, hits, limit, cancellationToken))
                    SearchDisk(folder, query, hits, limit, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ErrorLog.Write($"Search failed in {folder.LocalPath}", ex);
                try { SearchDisk(folder, query, hits, limit, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception diskEx) { ErrorLog.Write($"Disk search failed in {folder.LocalPath}", diskEx); }
            }
        }

        return hits
            .OrderBy(h => h.IsDirectory ? 0 : 1)
            .ThenBy(h => h.InternalPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool SearchJournal(
        FolderStatus folder,
        string query,
        List<FileHit> hits,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(folder.JournalPath))
            return false;

        string live = Path.Combine(folder.LocalPath, folder.JournalPath);
        if (!File.Exists(live))
            return false;

        string tmpDir = Path.Combine(Path.GetTempPath(), "NextcloudCompanion", "search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        string tmpDb = Path.Combine(tmpDir, "journal.db");
        try
        {
            File.Copy(live, tmpDb, overwrite: true);
            foreach (var ext in new[] { "-wal", "-shm" })
            {
                string side = live + ext;
                if (File.Exists(side))
                    File.Copy(side, tmpDb + ext, overwrite: true);
            }

            using var con = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = tmpDb,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            con.Open();

            using var probe = con.CreateCommand();
            probe.CommandText = "SELECT 1 FROM pragma_table_info('metadata') WHERE name = 'modtime'";
            bool hasModtime = probe.ExecuteScalar() is not null;

            using var cmd = con.CreateCommand();
            cmd.CommandText = hasModtime
                ? "SELECT path, type, modtime FROM metadata WHERE path LIKE $q ESCAPE '\\' COLLATE NOCASE"
                : "SELECT path, type, NULL FROM metadata WHERE path LIKE $q ESCAPE '\\' COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(query) + "%");

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (hits.Count >= limit)
                    break;

                string relative = reader.GetString(0).Replace('\\', '/');
                if (string.IsNullOrEmpty(relative))
                    continue;

                string name = NameOf(relative);
                if (name.StartsWith(".sync_", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                bool isDir = !reader.IsDBNull(1) && reader.GetInt64(1) == 2;
                DateTime? modified = reader.IsDBNull(2) ? null : FromUnix(reader.GetInt64(2));
                hits.Add(MakeHit(folder, relative, name, isDir, modified));
            }

            return true;
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    private static void SearchDisk(
        FolderStatus folder,
        string query,
        List<FileHit> hits,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder.LocalPath))
            return;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        foreach (var path in Directory.EnumerateFileSystemEntries(folder.LocalPath, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hits.Count >= limit)
                return;

            string name = Path.GetFileName(path);
            if (name.Length == 0 || name.StartsWith(".sync_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            string relative = Path.GetRelativePath(folder.LocalPath, path).Replace('\\', '/');
            bool isDir = Directory.Exists(path);
            DateTime? modified = null;
            try
            {
                var stamp = isDir ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
                if (stamp.Year >= 1980)
                    modified = stamp;
            }
            catch { /* ignore unreadable stamps */ }
            hits.Add(MakeHit(folder, relative, name, isDir, modified));
        }
    }

    private static FileHit MakeHit(FolderStatus folder, string relative, string name, bool isDir, DateTime? modified)
    {
        string internalPath = CombineInternal(folder.TargetPath, relative);
        string local = Path.Combine(folder.LocalPath, relative.Replace('/', Path.DirectorySeparatorChar));
        bool exists = isDir ? Directory.Exists(local) : File.Exists(local);
        if (modified is null && exists)
        {
            try
            {
                var stamp = isDir ? Directory.GetLastWriteTime(local) : File.GetLastWriteTime(local);
                if (stamp.Year >= 1980)
                    modified = stamp;
            }
            catch { /* ignore */ }
        }
        return new FileHit
        {
            Name = name,
            InternalPath = internalPath,
            LocalPath = local,
            Account = folder.Account,
            ServerUrl = folder.ServerUrl,
            IsDirectory = isDir,
            ExistsLocally = exists,
            Modified = modified
        };
    }

    private static DateTime? FromUnix(long seconds)
    {
        if (seconds <= 0) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static string CombineInternal(string targetPath, string relative)
    {
        string target = (targetPath ?? "/").Replace('\\', '/').TrimEnd('/');
        string rel = relative.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrEmpty(target) || target == "/")
            return "/" + rel;
        return target + "/" + rel;
    }

    private static string NameOf(string relative)
    {
        int i = relative.LastIndexOf('/');
        return i < 0 ? relative : relative[(i + 1)..];
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
