namespace NextcloudCompanion;

/// <summary>What Companion needs from nextcloud.cfg.</summary>
public sealed class ConfigData
{
    public List<FolderStatus> Folders { get; } = [];
    /// <summary>Log directory configured in the client (<c>logDir</c>), if any.</summary>
    public string? LogDir { get; init; }
    /// <summary>The client writes its logs to the temporary diagnostic directory.</summary>
    public bool LogToTemporaryDir { get; init; }
}

/// <summary>
/// Parses nextcloud.cfg. Folders are recognised in every group the supported clients use
/// (<c>Folders</c>, <c>FoldersWithPlaceholders</c>, <c>Multifolders</c>), and every folder is kept as its own entry:
/// two accounts that sync the same local path are two folders, not one.
/// </summary>
public static class ConfigReader
{
    private static readonly string[] FolderGroups = ["Folders", "FoldersWithPlaceholders", "Multifolders"];

    public static ConfigData Parse(string text)
    {
        var accounts = new Dictionary<string, (string Url, string User)>(StringComparer.Ordinal);
        var drafts = new Dictionary<(string Account, string Group, string Id), FolderDraft>();
        string section = "";
        string? logDir = null;
        bool logToTemp = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;
            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']');
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq < 0)
                continue;
            var key = line[..eq];
            var value = line[(eq + 1)..].Trim().Trim('"');

            if (section.Equals("General", StringComparison.OrdinalIgnoreCase) || section.Length == 0)
            {
                if (key.Equals("logDir", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                    logDir = value.Replace('/', Path.DirectorySeparatorChar);
                else if (key.Equals("logToTemporaryLogDir", StringComparison.OrdinalIgnoreCase)
                         || key.Equals("temporaryLogDir", StringComparison.OrdinalIgnoreCase))
                    logToTemp = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }

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
            else if (parts.Length == 4 && Array.IndexOf(FolderGroups, parts[1]) >= 0)
            {
                var id = (parts[0], parts[1], parts[2]);
                if (!drafts.TryGetValue(id, out var draft))
                    draft = new FolderDraft { AccountKey = parts[0] };
                switch (parts[3])
                {
                    case "localPath":   draft.LocalPath   = value.Replace('/', Path.DirectorySeparatorChar); break;
                    case "targetPath":  draft.TargetPath  = value; break;
                    case "journalPath": draft.JournalPath = value; break;
                    case "paused":      draft.Paused      = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                }
                drafts[id] = draft;
            }
        }

        var data = new ConfigData { LogDir = logDir, LogToTemporaryDir = logToTemp };
        foreach (var draft in drafts.Values)
        {
            if (string.IsNullOrWhiteSpace(draft.LocalPath))
                continue;
            accounts.TryGetValue(draft.AccountKey, out var acc);
            var host = HostOf(acc.Url);
            var user = acc.User;
            var local = draft.LocalPath.TrimEnd('\\', '/');
            data.Folders.Add(new FolderStatus
            {
                LocalPath = local,
                DisplayName = Path.GetFileName(local),
                Account = string.IsNullOrEmpty(user) ? host : $"{user}@{host}",
                DavUser = user ?? "",
                ServerHost = host,
                ServerUrl = (acc.Url ?? "").TrimEnd('/'),
                TargetPath = string.IsNullOrWhiteSpace(draft.TargetPath) ? "/" : draft.TargetPath,
                JournalPath = draft.JournalPath,
                Paused = draft.Paused,
                Health = Health.Unknown
            });
        }

        data.Folders.Sort((a, b) =>
        {
            int c = string.Compare(a.Account, b.Account, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            c = string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.LocalPath, b.LocalPath, StringComparison.OrdinalIgnoreCase);
        });
        return data;
    }

    private static string HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
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
