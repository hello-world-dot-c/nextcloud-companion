using System.Text.Json;

namespace NextcloudCompanion;

/// <summary>
/// Per-user settings in %AppData%\NextcloudCompanion\settings.json.
/// App passwords are DPAPI blobs, not the password text.
/// </summary>
public static class Settings
{
    public const int MaxRecentSearches = 100;

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NextcloudCompanion");
    private static readonly string PathFile = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly List<string> Recent = [];
    private static readonly Dictionary<string, string> Passwords = new(StringComparer.Ordinal);

    public static bool NotifyOnError { get; set; } = true;
    public static bool StartWithWindows { get; set; }
    public static IReadOnlyList<string> RecentSearches => Recent;

    static Settings() => Load();

    private sealed class Persisted
    {
        public bool NotifyOnError { get; set; } = true;
        public bool StartWithWindows { get; set; }
        public List<string> RecentSearches { get; set; } = [];
        public Dictionary<string, string> AppPasswords { get; set; } = new();
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(PathFile)) return;
            var data = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(PathFile), JsonOptions);
            if (data is null) return;
            NotifyOnError = data.NotifyOnError;
            StartWithWindows = data.StartWithWindows;
            Recent.Clear();
            Recent.AddRange(data.RecentSearches.Where(s => !string.IsNullOrWhiteSpace(s)).Take(MaxRecentSearches));
            Passwords.Clear();
            foreach (var pair in data.AppPasswords)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                    Passwords[pair.Key] = pair.Value;
            }
        }
        catch
        {
            // Keep defaults.
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var data = new Persisted
            {
                NotifyOnError = NotifyOnError,
                StartWithWindows = StartWithWindows,
                RecentSearches = Recent.Take(MaxRecentSearches).ToList(),
                AppPasswords = new Dictionary<string, string>(Passwords)
            };
            File.WriteAllText(PathFile, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch
        {
            // Best-effort.
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

    public static string AccountKey(string serverUrl, string davUser)
        => serverUrl.TrimEnd('/') + "\n" + davUser;

    public static string? GetAppPassword(string serverUrl, string davUser)
    {
        if (!Passwords.TryGetValue(AccountKey(serverUrl, davUser), out string? stored))
            return null;
        try { return SecretStore.Unprotect(stored); }
        catch { return null; }
    }

    public static void SetAppPassword(string serverUrl, string davUser, string password)
    {
        Passwords[AccountKey(serverUrl, davUser)] = SecretStore.Protect(password);
        Save();
    }

    public static void ClearAppPassword(string serverUrl, string davUser)
    {
        if (Passwords.Remove(AccountKey(serverUrl, davUser)))
            Save();
    }
}
