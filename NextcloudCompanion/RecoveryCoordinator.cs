using System.Text.RegularExpressions;

namespace NextcloudCompanion;

/// <summary>
/// Thresholds of the stall policy. These are starting values to validate on real machines, not guarantees of the
/// Nextcloud client.
/// </summary>
public sealed class RecoveryPolicy
{
    /// <summary>After the client started, waiting is normal while it connects and queues its folders.</summary>
    public TimeSpan StartupGrace { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan WakeGrace { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ReconnectGrace { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>How long a folder must have been waiting, without progress anywhere, before a stall is suspected.</summary>
    public TimeSpan WaitingThreshold { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Scans that must have seen the same waiting episode before the stall is mentioned.</summary>
    public int NoticeObservations { get; init; } = 2;
    /// <summary>Scans that must have seen it before an automatic restart is considered.</summary>
    public int ActionObservations { get; init; } = 3;
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromHours(1);
    public int MaxAttemptsPer24Hours { get; init; } = 2;
}

/// <summary>Where the history of automatic restarts is kept, so a restart of Companion cannot reset the limits.</summary>
public interface IRecoveryStore
{
    IReadOnlyList<DateTime> AutomaticRestartsUtc { get; }
    /// <summary>Returns false if the entry could not be persisted.</summary>
    bool Record(DateTime utc);
}

public sealed class SettingsRecoveryStore : IRecoveryStore
{
    public IReadOnlyList<DateTime> AutomaticRestartsUtc => Settings.AutomaticRestartsUtc;
    public bool Record(DateTime utc) => Settings.RecordAutomaticRestart(utc);
}

public enum DecisionKind
{
    /// <summary>Nothing to do.</summary>
    None,
    /// <summary>Automatic restart is switched on but cannot work with the evidence this client provides.</summary>
    Disabled,
    /// <summary>A stall is suspected, but no automatic restart is allowed right now.</summary>
    Suppressed,
    /// <summary>An automatic restart is due.</summary>
    Due
}

public sealed record RecoveryDecision(DecisionKind Kind, string Reason, string? IncidentId = null)
{
    public static readonly RecoveryDecision None = new(DecisionKind.None, "");
}

/// <summary>
/// Keeps evidence across scans (every scan creates new folder objects, so a stall timer cannot live in them),
/// marks folders that appear stuck waiting, and decides whether an automatic restart is due. Waiting alone is not
/// a reason: the client's scheduler legitimately queues folders while another one syncs. Durations are measured
/// with a monotonic clock, suspend/resume resets the evidence, and the attempt history is persisted.
/// </summary>
public sealed class RecoveryCoordinator
{
    private static readonly Regex AuthenticationText = new(
        @"auth|credential|password|unauthori[sz]ed|forbidden|\b40[13]\b|login|token|certificate",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class Episode
    {
        public required DateTime WaitingSince;
        public long FirstSeen;
        public int Observations;
        public DateTime ProgressMarker;
    }

    private readonly IClock _clock;
    private readonly IRecoveryStore _store;
    private readonly RecoveryPolicy _policy;
    private readonly Func<bool> _enabled;
    private readonly object _gate = new();

    private readonly Dictionary<string, Episode> _episodes = new();
    private readonly HashSet<string> _handled = new();
    private string? _processKey;
    private long _processSeen;
    private TimeSpan _processAgeWhenSeen;
    private long? _lastResume;
    private long? _lastAttempt;

    public RecoveryCoordinator(IClock clock, IRecoveryStore store, RecoveryPolicy? policy = null, Func<bool>? enabled = null)
    {
        _clock = clock;
        _store = store;
        _policy = policy ?? new RecoveryPolicy();
        _enabled = enabled ?? (() => false);
    }

    public RecoveryDecision LastDecision { get; private set; } = RecoveryDecision.None;

    /// <summary>How long until an automatic restart would be attempted, if one is being counted down; otherwise null.</summary>
    public TimeSpan? Forecast { get; private set; }

    /// <summary>Wording of <see cref="Forecast"/> for tooltips, or null.</summary>
    public string? ForecastNote => Forecast is TimeSpan t ? DescribeForecast(t) : null;

    /// <summary>True while automatic restart is on but the evidence (the client's log) is too poor to act on.</summary>
    public bool Inactive { get; private set; }

    /// <summary>What to add to the status line when automatic restart is on: the countdown, or why it cannot act.</summary>
    public string? Note => ForecastNote ?? (Inactive ? "automatic recovery inactive" : null);

    public static string DescribeForecast(TimeSpan remaining)
        => remaining < TimeSpan.FromMinutes(1)
            ? "attempting automatic recovery within a minute"
            : $"attempting automatic recovery in about {(int)Math.Ceiling(remaining.TotalMinutes)} minutes";

    /// <summary>The computer woke up: waiting right after a resume is normal, and old evidence is void.</summary>
    public void NotifyResume()
    {
        lock (_gate)
        {
            _lastResume = _clock.MonotonicMilliseconds;
            _episodes.Clear();
        }
    }

    /// <summary>Records an automatic attempt before it starts, so even a crash during it counts against the limits.</summary>
    public bool BeginAttempt(RecoveryDecision decision)
    {
        lock (_gate)
        {
            _lastAttempt = _clock.MonotonicMilliseconds;
            if (decision.IncidentId is not null)
                _handled.Add(decision.IncidentId);
            return _store.Record(_clock.Now.ToUniversalTime());
        }
    }

    /// <summary>
    /// Evaluates one scan. Sets <see cref="FolderStatus.Stalled"/> on the folders of that scan (clearing it
    /// first) and returns what, if anything, should be done.
    /// </summary>
    public RecoveryDecision Observe(ClientStatus status)
    {
        lock (_gate)
        {
            foreach (var folder in status.Folders)
                folder.Stalled = false;
            Forecast = null;
            Inactive = false;
            LastDecision = Evaluate(status);
            return LastDecision;
        }
    }

    private RecoveryDecision Evaluate(ClientStatus status)
    {
        DateTime now = _clock.Now;
        long mono = _clock.MonotonicMilliseconds;
        bool enabled = _enabled();

        if (status.Process is null || status.AmbiguousProcess)
        {
            Forget();
            return enabled
                ? new RecoveryDecision(DecisionKind.Suppressed, status.AmbiguousProcess
                    ? "More than one Nextcloud process is running."
                    : "Nextcloud is not running.")
                : RecoveryDecision.None;
        }

        string key = $"{status.Process.Id}:{status.Process.StartTime.Ticks}";
        if (key != _processKey)
        {
            _episodes.Clear();
            _processKey = key;
            _processSeen = mono;
            _processAgeWhenSeen = now - status.Process.StartTime > TimeSpan.Zero ? now - status.Process.StartTime : TimeSpan.Zero;
        }
        TimeSpan age = _processAgeWhenSeen + TimeSpan.FromMilliseconds(mono - _processSeen);

        if (!status.Observation.UsableForRecovery)
        {
            _episodes.Clear();
            Inactive = enabled;
            string why = status.Observation.Reason.Length > 0 ? status.Observation.Reason : status.Observation.State.ToString();
            return enabled
                ? new RecoveryDecision(DecisionKind.Suppressed, $"Companion cannot currently use Nextcloud's logs ({why}).")
                : RecoveryDecision.None;
        }

        var enabledFolders = status.EnabledFolders.ToList();
        DateTime marker = ProgressMarker(status.Folders);
        bool othersActive = enabledFolders.Any(f => f.Activity is SyncActivity.Preparing or SyncActivity.Syncing);

        var waiting = new List<(FolderStatus Folder, Episode Episode)>();
        var live = new HashSet<string>();
        foreach (var folder in enabledFolders)
        {
            if (folder.Activity != SyncActivity.Waiting || folder.ActivitySince is not DateTime since
                || folder.Health is Health.Error or Health.Disconnected)
                continue;
            // A silent log is only evidence about the client that wrote it: the waiting record must come from this
            // very process, not from an older instance or from some other client that used the same directory.
            if (status.Observation.Silent && since < status.Process.StartTime - TimeSpan.FromSeconds(5))
                continue;

            live.Add(folder.Identity);
            if (!_episodes.TryGetValue(folder.Identity, out var episode) || episode.WaitingSince != since)
            {
                episode = new Episode { WaitingSince = since, FirstSeen = mono, ProgressMarker = marker };
                _episodes[folder.Identity] = episode;
            }
            if (othersActive || marker > episode.ProgressMarker)
            {
                // Progress elsewhere is the scheduler doing its job: the clock starts over.
                episode.FirstSeen = mono;
                episode.Observations = 0;
                episode.ProgressMarker = marker;
            }
            episode.Observations++;
            waiting.Add((folder, episode));
        }
        foreach (var gone in _episodes.Keys.Where(k => !live.Contains(k)).ToList())
            _episodes.Remove(gone);

        // Which waiting folders look stuck (for the notice), and which of those allow acting.
        var actionable = new List<(FolderStatus Folder, Episode Episode)>();
        TimeSpan? soonest = null;
        foreach (var (folder, episode) in waiting)
        {
            if (Expected(folder, status, age, mono, now) is not null)
                continue;
            TimeSpan observed = TimeSpan.FromMilliseconds(mono - episode.FirstSeen);
            TimeSpan loggedWaiting = now - episode.WaitingSince;
            TimeSpan remaining = TimeSpan.Zero;
            if (_policy.WaitingThreshold - observed > remaining)
                remaining = _policy.WaitingThreshold - observed;
            if (_policy.WaitingThreshold - loggedWaiting > remaining)
                remaining = _policy.WaitingThreshold - loggedWaiting;
            if (soonest is null || remaining < soonest)
                soonest = remaining;
            if (episode.Observations < _policy.NoticeObservations || observed < TimeSpan.Zero)
                continue;
            if (loggedWaiting < _policy.WaitingThreshold)
                continue;
            folder.Stalled = true;
            if (episode.Observations >= _policy.ActionObservations && observed >= _policy.WaitingThreshold)
                actionable.Add((folder, episode));
        }

        if (!enabled)
            return RecoveryDecision.None;

        if (!status.Observation.ShowsSyncStates)
        {
            Inactive = true;
            return new RecoveryDecision(DecisionKind.Disabled,
                "Nextcloud's log has no sync state records, so Companion cannot tell waiting from normal idle time.");
        }

        Forecast = soonest;

        if (actionable.Count == 0)
        {
            var anyStalled = status.Folders.Any(f => f.Stalled);
            return anyStalled
                ? new RecoveryDecision(DecisionKind.Suppressed, "Waiting for more evidence before restarting.")
                : RecoveryDecision.None;
        }

        var (first, firstEpisode) = actionable[0];
        string incident = $"{key}|{first.Identity}|{firstEpisode.WaitingSince:O}";
        if (_handled.Contains(incident))
        {
            Forecast = null;
            return new RecoveryDecision(DecisionKind.Suppressed, "A restart was already tried for this stall and did not fix it.", incident);
        }

        DateTime utc = now.ToUniversalTime();
        var history = _store.AutomaticRestartsUtc;
        // An entry in the future (clock set back) counts as recent, never as expired.
        int last24 = history.Count(t => utc - t < TimeSpan.FromHours(24));
        if (last24 >= _policy.MaxAttemptsPer24Hours)
        {
            Forecast = null;
            return new RecoveryDecision(DecisionKind.Suppressed,
                $"Automatic restarts are limited to {_policy.MaxAttemptsPer24Hours} per 24 hours, and that limit is reached.", incident);
        }

        bool coolingDown = history.Any(t => utc - t < _policy.Cooldown)
                           || (_lastAttempt is long l && mono - l < (long)_policy.Cooldown.TotalMilliseconds);
        if (coolingDown)
        {
            var newest = history.Count > 0 ? history.Max() : DateTime.MinValue;
            TimeSpan left = _policy.Cooldown - (utc - newest);
            if (_lastAttempt is long attempt)
            {
                var sinceAttempt = TimeSpan.FromMilliseconds(mono - attempt);
                if (_policy.Cooldown - sinceAttempt > left)
                    left = _policy.Cooldown - sinceAttempt;
            }
            Forecast = left > TimeSpan.Zero ? left : null;
            return new RecoveryDecision(DecisionKind.Suppressed, "The last automatic restart was less than an hour ago.", incident);
        }

        Forecast = TimeSpan.Zero;
        return new RecoveryDecision(DecisionKind.Due,
            $"{first.DisplayName} has been waiting to start syncing since {FolderStatus.FormatTime(firstEpisode.WaitingSince)}.", incident);
    }

    /// <summary>A reason why waiting is expected for this folder right now (so it is not a stall), or null.</summary>
    private string? Expected(FolderStatus folder, ClientStatus status, TimeSpan age, long mono, DateTime now)
    {
        if (age < _policy.StartupGrace)
            return "Nextcloud was started a short while ago.";
        if (_lastResume is long resumed && mono - resumed < (long)_policy.WakeGrace.TotalMilliseconds)
            return "The computer woke up a short while ago.";
        if (folder.AccountConnectedAt is DateTime connected && now - connected < _policy.ReconnectGrace)
            return "The account reconnected a short while ago.";

        var sameAccount = status.Folders.Where(f => f.AccountKey == folder.AccountKey).ToList();
        if (sameAccount.Any(f => f.Health == Health.Disconnected))
            return "The server of this account is unreachable.";
        if (sameAccount.Any(f => f.Paused))
            return "A folder of this account is paused.";
        if (sameAccount.Any(f => f.Health is Health.Error or Health.Warning
                                 && f.ErrorMessage is { Length: > 0 } text && AuthenticationText.IsMatch(text)))
            return "The account needs the user to sign in or decide something.";
        return null;
    }

    private static DateTime ProgressMarker(IEnumerable<FolderStatus> folders)
    {
        DateTime marker = DateTime.MinValue;
        foreach (var f in folders)
        {
            foreach (var t in new[] { f.LastSyncStartedAt, f.LastSyncCompletedAt, f.LastSuccessfulSyncAt })
                if (t is DateTime d && d > marker)
                    marker = d;
            if (f.Activity != SyncActivity.Waiting && f.ActivitySince is DateTime a && a > marker)
                marker = a;
        }
        return marker;
    }

    private void Forget()
    {
        _processKey = null;
        _episodes.Clear();
    }
}
