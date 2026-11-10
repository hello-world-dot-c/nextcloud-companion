using System.Text.Json;
using System.Text.Json.Serialization;

namespace NextcloudCompanion;

/// <summary>
/// Per-user settings in %AppData%\NextcloudCompanion\settings.json.
/// App passwords are DPAPI blobs, not the password text. Saving is atomic, reports failure, and keeps fields this
/// version does not know about; an unreadable file is set aside before anything can overwrite it.
/// </summary>
public static class Settings
{
    public const int MaxRecentSearches = 100;
    private const int MaxRecoveryHistory = 50;

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NextcloudCompanion");
    private static readonly string PathFile = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();

    private static readonly List<string> Recent = [];
    private static readonly Dictionary<string, string> Passwords = new(StringComparer.Ordinal);
    private static readonly List<DateTime> RecoveryAttempts = [];
    private static Dictionary<string, JsonElement>? _extra;

    public static bool NotifyOnError { get; set; } = true;
    public static bool StartWithWindows { get; set; }
    /// <summary>File search also asks the server (files that are not in a synced folder).</summary>
    public static bool SearchServer { get; set; } = true;
    /// <summary>Restart Nextcloud automatically when syncing appears stuck. Off unless the user turns it on.</summary>
    public static bool AutoRestartNextcloud { get; set; }
    /// <summary>Explicit directory with the client's logs; overrides the one found automatically.</summary>
    public static string? LogDirOverride { get; set; }
    public static IReadOnlyList<string> RecentSearches => Recent;

    /// <summary>Why the last <see cref="Save"/> failed, or null if it worked.</summary>
    public static string? LastSaveError { get; private set; }

    static Settings() => Load();

    private sealed class Persisted
    {
        public bool NotifyOnError { get; set; } = true;
        public bool StartWithWindows { get; set; }
        public bool SearchServer { get; set; } = true;
        public bool AutoRestartNextcloud { get; set; }
        public string? LogDirOverride { get; set; }
        public List<string> RecentSearches { get; set; } = [];
        public List<DateTime> RecoveryAttemptsUtc { get; set; } = [];
        public Dictionary<string, string> AppPasswords { get; set; } = new();

        /// <summary>Fields written by another version stay in the file.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    public static void Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(PathFile)) return;
                string text = File.ReadAllText(PathFile);
                Persisted? data;
                try
                {
                    data = JsonSerializer.Deserialize<Persisted>(text, JsonOptions);
                }
                catch (JsonException)
                {
                    SetAside();
                    return;
                }
                if (data is null) return;
                NotifyOnError = data.NotifyOnError;
                StartWithWindows = data.StartWithWindows;
                SearchServer = data.SearchServer;
                AutoRestartNextcloud = data.AutoRestartNextcloud;
                LogDirOverride = string.IsNullOrWhiteSpace(data.LogDirOverride) ? null : data.LogDirOverride;
                Recent.Clear();
                Recent.AddRange(data.RecentSearches.Where(s => !string.IsNullOrWhiteSpace(s)).Take(MaxRecentSearches));
                RecoveryAttempts.Clear();
                RecoveryAttempts.AddRange(data.RecoveryAttemptsUtc.Select(t => DateTime.SpecifyKind(t, DateTimeKind.Utc)));
                Passwords.Clear();
                foreach (var pair in data.AppPasswords)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                        Passwords[pair.Key] = pair.Value;
                }
                _extra = data.Extra;
            }
            catch (IOException)
            {
                // Keep defaults; the file is left alone.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>A file that cannot be parsed is copied aside, so the next save cannot destroy the app passwords in it.</summary>
    private static void SetAside()
    {
        try
        {
            File.Copy(PathFile, PathFile + ".unreadable", overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Saves everything. Returns false (and sets <see cref="LastSaveError"/>) if the file could not be written.</summary>
    public static bool Save()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var data = new Persisted
                {
                    NotifyOnError = NotifyOnError,
                    StartWithWindows = StartWithWindows,
                    SearchServer = SearchServer,
                    AutoRestartNextcloud = AutoRestartNextcloud,
                    LogDirOverride = LogDirOverride,
                    RecentSearches = Recent.Take(MaxRecentSearches).ToList(),
                    RecoveryAttemptsUtc = RecoveryAttempts.TakeLast(MaxRecoveryHistory).ToList(),
                    AppPasswords = new Dictionary<string, string>(Passwords),
                    Extra = _extra
                };
                string temp = PathFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
                File.Move(temp, PathFile, overwrite: true);
                LastSaveError = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LastSaveError = ex.Message;
                ErrorLog.Write("Saving settings failed", ex);
                return false;
            }
        }
    }

    public static void RememberSearch(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
            return;
        Recent.RemoveAll(s => s.Equals(query, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, query);
        if (Recent.Count > MaxRecentSearches)
            Recent.RemoveRange(MaxRecentSearches, Recent.Count - MaxRecentSearches);
        Save();
    }

    public static void ClearSearches()
    {
        Recent.Clear();
        Save();
    }

    /// <summary>UTC times of the automatic Nextcloud restarts, newest last. Survives restarts of Companion.</summary>
    public static IReadOnlyList<DateTime> AutomaticRestartsUtc
    {
        get { lock (Gate) return RecoveryAttempts.ToList(); }
    }

    public static bool RecordAutomaticRestart(DateTime utc)
    {
        lock (Gate)
            RecoveryAttempts.Add(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        return Save();
    }

    public static string AccountKey(string serverUrl, string davUser)
        => serverUrl.TrimEnd('/') + "\n" + davUser;

    public static string? GetAppPassword(string serverUrl, string davUser)
    {
        string? stored;
        lock (Gate)
        {
            if (!Passwords.TryGetValue(AccountKey(serverUrl, davUser), out stored))
                return null;
        }
        try { return SecretStore.Unprotect(stored); }
        catch { return null; }
    }

    public static void SetAppPassword(string serverUrl, string davUser, string password)
    {
        lock (Gate)
            Passwords[AccountKey(serverUrl, davUser)] = SecretStore.Protect(password);
        Save();
    }

    public static void ClearAppPassword(string serverUrl, string davUser)
    {
        bool removed;
        lock (Gate)
            removed = Passwords.Remove(AccountKey(serverUrl, davUser));
        if (removed)
            Save();
    }
}
