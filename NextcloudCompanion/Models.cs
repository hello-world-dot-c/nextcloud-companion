namespace NextcloudCompanion;

public enum Health
{
    Unknown,
    Offline,
    Disconnected,
    Ok,
    Warning,
    Error,
    Paused
}

/// <summary>What a folder is doing right now, as far as the log shows. Unknown means no evidence.</summary>
public enum SyncActivity
{
    Unknown,
    /// <summary>"Not yet started": the client's scheduler has not started this folder yet.</summary>
    Waiting,
    Preparing,
    Syncing,
    /// <summary>The last recorded state is a finished sync (success, possibly with ignored files).</summary>
    Idle,
    Paused
}

/// <summary>How well Companion could observe the client in the latest scan.</summary>
public enum ObservationState
{
    Ok,
    /// <summary>No log directory was found.</summary>
    LogsMissing,
    /// <summary>A log directory exists but could not be read.</summary>
    LogsUnreadable,
    /// <summary>Logs exist, but nothing recent was written to them.</summary>
    NoRecentRecords,
    /// <summary>Recent records exist, but none that Companion understands.</summary>
    UnsupportedFormat,
    /// <summary>The scan itself failed; everything shown is out of date or unknown.</summary>
    ScanFailed
}

/// <summary>The evidence source of a scan: which logs were used and what they showed.</summary>
public sealed class Observation
{
    public ObservationState State { get; init; } = ObservationState.Ok;
    /// <summary>Human-readable description of the source, e.g. "%AppData%\Nextcloud\logs (default)".</summary>
    public string Source { get; init; } = "";
    /// <summary>Timestamp of the newest record found in the monitored logs.</summary>
    public DateTime? LastRecordAt { get; init; }
    /// <summary>True when the logs contained per-folder sync state records ("Sync state changed ...").</summary>
    public bool ShowsSyncStates { get; init; }
    public string Reason { get; init; } = "";
    public int UnreadableFiles { get; init; }
    public int UnattributedRecords { get; init; }
    /// <summary>
    /// The log has sync state records, but nothing at all was written for a while. A client that is running normally
    /// keeps writing; the last states it wrote are still the best evidence of what it is doing.
    /// </summary>
    public bool Silent { get; init; }

    public bool Usable => State == ObservationState.Ok;

    /// <summary>Good enough for the recovery coordinator: current logs, or a silent log that does show sync states.</summary>
    public bool UsableForRecovery => Usable || (State == ObservationState.NoRecentRecords && Silent && ShowsSyncStates);
}

public enum RecoveryPhase
{
    None,
    Restarting,
    WaitingForEvidence,
    Resumed,
    Failed
}

/// <summary>State of a Nextcloud restart started by Companion (manually or automatically).</summary>
public sealed class RecoveryStatus
{
    public static readonly RecoveryStatus None = new();

    public RecoveryPhase Phase { get; init; } = RecoveryPhase.None;
    public string Message { get; init; } = "";
    public bool Automatic { get; init; }
    public DateTime? Since { get; init; }

    public bool Active => Phase is RecoveryPhase.Restarting or RecoveryPhase.WaitingForEvidence;
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
    public SyncActivity Activity { get; set; } = SyncActivity.Unknown;
    /// <summary>Log time at which <see cref="Activity"/> last changed.</summary>
    public DateTime? ActivitySince { get; set; }
    /// <summary>Newest log record that could be attributed to this folder.</summary>
    public DateTime? LastObservedAt { get; set; }
    public DateTime? LastSyncStartedAt { get; set; }
    /// <summary>A sync run ended, successfully or not.</summary>
    public DateTime? LastSyncCompletedAt { get; set; }
    /// <summary>A sync run ended without problems (including a run that found nothing to do).</summary>
    public DateTime? LastSuccessfulSyncAt { get; set; }
    /// <summary>The server answered an ETag check. This is connection evidence, not a sync result.</summary>
    public DateTime? LastConnectionCheckAt { get; set; }
    /// <summary>The client reported the account as connected (log time); used as a grace period.</summary>
    public DateTime? AccountConnectedAt { get; set; }
    public string? ErrorPath { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? LastError { get; set; }
    /// <summary>Set by the recovery coordinator: waiting for long enough, with enough evidence, to suspect a stall.</summary>
    public bool Stalled { get; set; }

    /// <summary>Last sync that is known to have succeeded (kept for the status window).</summary>
    public DateTime? LastSync => LastSuccessfulSyncAt;

    /// <summary>Stable identity of the folder: account plus remote and local location.</summary>
    public string Identity => string.Join("|", ServerUrl.TrimEnd('/'), DavUser, TargetPath.TrimEnd('/'), LocalPath).ToLowerInvariant();

    /// <summary>Same format as <see cref="Settings.AccountKey"/>.</summary>
    public string AccountKey => ServerUrl.TrimEnd('/') + "\n" + DavUser;

    /// <summary>The configuration of this folder without any observed state.</summary>
    public FolderStatus CloneConfiguration() => new()
    {
        LocalPath = LocalPath,
        DisplayName = DisplayName,
        Account = Account,
        DavUser = DavUser,
        ServerHost = ServerHost,
        ServerUrl = ServerUrl,
        TargetPath = TargetPath,
        JournalPath = JournalPath,
        Paused = Paused
    };

    public string Headline
    {
        get
        {
            if (Paused) return "Paused";
            if (Stalled)
                return ActivitySince is DateTime stuck
                    ? $"May be stuck waiting to sync (waiting since {FormatTime(stuck)})"
                    : "May be stuck waiting to sync";
            switch (Health)
            {
                case Health.Error:
                    return ErrorMessage is { Length: > 0 } error ? $"Error · {error}" : "Error";
                case Health.Warning:
                    return ErrorMessage is { Length: > 0 } warning ? $"Warning · {warning}" : "Warning";
                case Health.Disconnected:
                    return ErrorMessage is { Length: > 0 } down ? down : "Server unreachable";
                case Health.Offline:
                    return "Client not running";
            }

            switch (Activity)
            {
                case SyncActivity.Waiting:
                    return ActivitySince is DateTime waiting
                        ? $"Waiting to start syncing (since {FormatTime(waiting)})"
                        : "Waiting to start syncing";
                case SyncActivity.Preparing:
                    return "Preparing to sync";
                case SyncActivity.Syncing:
                    return "Syncing";
            }

            if (Health == Health.Ok)
                return LastSuccessfulSyncAt is DateTime ok
                    ? $"Last sync completed successfully at {FormatTime(ok)}"
                    : "Last sync completed successfully";

            return LastSyncCompletedAt is DateTime done
                ? $"No sync result in the logs (a sync ended {FormatTime(done)})"
                : "No sync result in the logs yet";
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
    /// <summary>The running client, when it could be identified (exact executable, PID, start time, session).</summary>
    public ProcessIdentity? Process { get; init; }
    /// <summary>More than one client process was found; recovery refuses to act on an ambiguous target.</summary>
    public bool AmbiguousProcess { get; init; }
    /// <summary>
    /// One health value for the icon. Derived from the folders and the observation, in this order: scan failure,
    /// not running, errors, unreachable servers, warnings or a suspected stall, all paused. Healthy only when every
    /// enabled folder has a successful result and the evidence is current; anything else is Unknown.
    /// </summary>
    public Health Overall
    {
        get
        {
            if (Observation.State == ObservationState.ScanFailed)
                return Health.Unknown;
            if (!ProcessRunning)
                return Health.Offline;
            if (Folders.Any(f => f.Health == Health.Error))
                return Health.Error;
            if (Folders.Any(f => f.Health == Health.Disconnected))
                return Health.Disconnected;
            if (Folders.Any(f => f.Health == Health.Warning || f.Stalled))
                return Health.Warning;
            var enabled = EnabledFolders.ToList();
            if (Folders.Count > 0 && enabled.Count == 0)
                return Health.Paused;
            if (enabled.Count > 0 && Observation.Usable && enabled.All(f => f.Health == Health.Ok))
                return Health.Ok;
            return Health.Unknown;
        }
    }
    public IReadOnlyList<FolderStatus> Folders { get; init; } = [];
    public DateTime CheckedAt { get; init; } = DateTime.Now;
    public string? LogFile { get; init; }
    public Observation Observation { get; init; } = new();
    /// <summary>Restart state; filled in by the tray after each scan.</summary>
    public RecoveryStatus Recovery { get; set; } = RecoveryStatus.None;
    /// <summary>E.g. "attempting automatic recovery in about 6 minutes"; set by the tray from the coordinator's forecast.</summary>
    public string? RecoveryNote { get; set; }

    public IEnumerable<FolderStatus> EnabledFolders => Folders.Where(f => !f.Paused);

    /// <summary>One line that says what is known, and says plainly when nothing is.</summary>
    public string Summary
    {
        get
        {
            string text = BaseSummary;
            if (RecoveryNote is { Length: > 0 } note && ProcessRunning && Recovery.Phase == RecoveryPhase.None
                && !Folders.Any(f => f.Health is Health.Error or Health.Disconnected))
                return text + " — " + note;
            return text;
        }
    }

    private string BaseSummary
    {
        get
        {
            if (Observation.State == ObservationState.ScanFailed)
                return "Sync status unavailable — Companion could not read Nextcloud's state"
                       + (string.IsNullOrWhiteSpace(Observation.Reason) ? "" : $" ({Observation.Reason})");

            if (!ProcessRunning)
                return "Nextcloud client is not running";

            if (Recovery.Phase != RecoveryPhase.None && Recovery.Message.Length > 0)
                return Recovery.Message;

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

            if (Folders.Any(f => f.Stalled))
                return "Nextcloud may be stuck waiting to sync";

            if (Folders.Any(f => f.Health == Health.Warning))
                return "Nextcloud has warnings";

            var enabled = EnabledFolders.ToList();
            if (Folders.Count > 0 && enabled.Count == 0)
                return "All folders are paused";

            // Everything below makes a claim about now, which needs a usable, recent source.
            if (!Observation.Usable)
                return ObservationSummary();

            if (enabled.Any(f => f.Activity is SyncActivity.Preparing or SyncActivity.Syncing))
                return "Nextcloud is syncing";

            if (enabled.Any(f => f.Activity == SyncActivity.Waiting))
                return "Nextcloud is waiting to start syncing";

            if (enabled.Count > 0 && enabled.All(f => f.Health == Health.Ok))
            {
                var last = enabled.Max(f => f.LastSuccessfulSyncAt);
                return last is DateTime t
                    ? $"Last sync completed successfully at {FolderStatus.FormatTime(t)}"
                    : "Last sync completed successfully";
            }

            if (enabled.Any(f => f.Health == Health.Ok))
            {
                int unknown = enabled.Count(f => f.Health != Health.Ok);
                return $"No sync result in the logs yet for {unknown} of {enabled.Count} folders";
            }

            return "Nextcloud is running — sync status unknown";
        }
    }

    private string ObservationSummary() => Observation.State switch
    {
        ObservationState.NoRecentRecords when Observation.Silent && Observation.LastRecordAt is DateTime last
            && Folders.Any(f => !f.Paused && f.Activity == SyncActivity.Waiting)
            => $"No log activity since {FolderStatus.FormatTime(last)}; Nextcloud last reported waiting to start syncing",
        ObservationState.LogsMissing or ObservationState.LogsUnreadable
            => "Sync status unavailable — Companion cannot read the activity source",
        ObservationState.UnsupportedFormat
            => "Sync status unavailable — the log records are not in a format Companion understands",
        ObservationState.NoRecentRecords
            => Observation.Reason.Length > 0
                ? "No recent sync activity: " + Observation.Reason
                : "No recent sync activity was found in the monitored logs",
        _ => "Nextcloud is running — sync status unknown"
    };
}
