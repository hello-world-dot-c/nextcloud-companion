namespace NextcloudCompanion;

public enum Health
{
    Unknown,
    Offline,
    Disconnected,
    Ok,
    Warning,
    Error
}

public sealed class FolderStatus
{
    public required string LocalPath { get; init; }
    public required string DisplayName { get; init; }
    public required string Account { get; init; }
    /// <summary>WebDAV user id used to sign in, which can itself contain an @.</summary>
    public string DavUser { get; init; } = "";
    public required string ServerHost { get; init; }
    /// <summary>Account origin, e.g. <c>https://cloud.example.com</c>.</summary>
    public string ServerUrl { get; init; } = "";
    /// <summary>Remote folder on the server, e.g. <c>/</c> or <c>/Acme (shared-int)/Engineering</c>.</summary>
    public string TargetPath { get; init; } = "/";
    /// <summary>Sync journal file name inside <see cref="LocalPath"/>, e.g. <c>.sync_ff262985ffeb.db</c>.</summary>
    public string? JournalPath { get; init; }
    public bool Paused { get; init; }
    public Health Health { get; set; } = Health.Unknown;
    public DateTime? LastSync { get; set; }
    public string? ErrorPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? LastError { get; set; }

    public string Headline
    {
        get
        {
            if (Paused) return "Paused";
            return Health switch
            {
                Health.Ok => LastSync is DateTime t ? $"OK · last sync {FormatTime(t)}" : "OK",
                Health.Error => ErrorMessage is { Length: > 0 } msg
                    ? $"Error · {msg}"
                    : "Error",
                Health.Warning => ErrorMessage is { Length: > 0 } msg
                    ? $"Warning · {msg}"
                    : "Warning",
                Health.Disconnected => ErrorMessage is { Length: > 0 } msg
                    ? msg
                    : "Server unreachable",
                Health.Offline => "Client not running",
                _ => "No recent sync in the log"
            };
        }
    }

    public static string FormatTime(DateTime t)
    {
        var local = t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;
        var now = DateTime.Now;
        if (local.Date == now.Date)
            return local.ToString("HH:mm");
        if (local.Date == now.Date.AddDays(-1))
            return "yesterday " + local.ToString("HH:mm");
        if (local.Year == now.Year)
            return local.ToString("d MMM HH:mm");
        return local.ToString("d MMM yyyy");
    }
}

public sealed class ClientStatus
{
    public bool ProcessRunning { get; init; }
    public Health Overall { get; init; } = Health.Unknown;
    public IReadOnlyList<FolderStatus> Folders { get; init; } = [];
    public DateTime CheckedAt { get; init; } = DateTime.Now;
    public string? LogFile { get; init; }

    public string Summary
    {
        get
        {
            if (!ProcessRunning)
                return "Nextcloud client is not running";

            var errors = Folders.Where(f => f.Health == Health.Error).ToList();
            if (errors.Count == 1)
            {
                var f = errors[0];
                var item = string.IsNullOrWhiteSpace(f.ErrorPath) ? f.DisplayName : f.ErrorPath;
                return $"{f.DisplayName}: {f.ErrorMessage ?? "sync error"} ({item})";
            }
            if (errors.Count > 1)
                return $"{errors.Count} folders have sync errors";

            var down = Folders
                .Where(f => f.Health == Health.Disconnected)
                .Select(f => string.IsNullOrEmpty(f.ServerHost) ? f.Account : f.ServerHost)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (down.Count == 1)
                return $"{down[0]} is unreachable";
            if (down.Count > 1)
                return $"{down.Count} servers are unreachable";

            if (Folders.Any(f => f.Health == Health.Warning))
                return "Nextcloud has warnings";

            if (Folders.Count > 0 && Folders.All(f => f.Health == Health.Ok || f.Paused))
                return "All folders in sync";

            return "Nextcloud is running";
        }
    }
}
