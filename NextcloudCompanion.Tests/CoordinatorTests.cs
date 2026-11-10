namespace NextcloudCompanion.Tests;

/// <summary>When waiting is a stall, and when an automatic restart is allowed.</summary>
public sealed class CoordinatorTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeStore _store = new();
    private readonly ProcessIdentity _process;
    private bool _enabled = true;

    public CoordinatorTests()
    {
        _process = new ProcessIdentity(4711, _clock.Now.AddHours(-3), @"C:\Program Files\Nextcloud\nextcloud.exe", 1);
    }

    private RecoveryCoordinator Coordinator(IRecoveryStore? store = null, Func<bool>? enabled = null)
        => new(_clock, store ?? _store, new RecoveryPolicy(), enabled ?? (() => _enabled));

    private FolderStatus Folder(string name, SyncActivity activity, TimeSpan? since = null, string account = "alice", bool paused = false,
        Health health = Health.Unknown, string? error = null, DateTime? at = null) => new()
    {
        LocalPath = @"C:\Users\alice\" + name,
        DisplayName = name,
        Account = account + "@cloud.example.com",
        DavUser = account,
        ServerHost = "cloud.example.com",
        ServerUrl = "https://cloud.example.com",
        TargetPath = "/" + name,
        Paused = paused,
        Health = health,
        ErrorMessage = error,
        Activity = paused ? SyncActivity.Paused : activity,
        ActivitySince = at ?? (since is TimeSpan s ? _clock.Now - s : null)
    };

    private ClientStatus Status(ProcessIdentity? process, bool showsStates, params FolderStatus[] folders) => new()
    {
        ProcessRunning = process is not null,
        Process = process,
        Folders = folders,
        CheckedAt = _clock.Now,
        Observation = new Observation { State = ObservationState.Ok, ShowsSyncStates = showsStates, LastRecordAt = _clock.Now }
    };

    /// <summary>One folder waiting since <paramref name="waitingFor"/> ago, observed again and again every 4 seconds.</summary>
    private RecoveryDecision ObserveWaiting(RecoveryCoordinator coordinator, TimeSpan waitingSince, TimeSpan howLong, out ClientStatus last,
        Func<TimeSpan, FolderStatus[]>? others = null)
    {
        var decision = RecoveryDecision.None;
        last = Status(_process, true, Folder("A", SyncActivity.Waiting, waitingSince));
        var start = _clock.MonotonicMilliseconds;
        var origin = waitingSince;
        while (_clock.MonotonicMilliseconds - start <= howLong.TotalMilliseconds)
        {
            var elapsed = TimeSpan.FromMilliseconds(_clock.MonotonicMilliseconds - start);
            var folders = new List<FolderStatus> { Folder("A", SyncActivity.Waiting, origin + elapsed) };
            if (others is not null)
                folders.AddRange(others(elapsed));
            last = Status(_process, true, folders.ToArray());
            decision = coordinator.Observe(last);
            _clock.Advance(TimeSpan.FromSeconds(4));
        }
        return decision;
    }

    /// <summary>Same, for a folder whose waiting began at a fixed time in the log.</summary>
    private RecoveryDecision ObserveWaiting(RecoveryCoordinator coordinator, DateTime since, TimeSpan howLong, out ClientStatus last,
        Func<TimeSpan, FolderStatus[]>? others = null)
        => ObserveWaiting(coordinator, _clock.Now - since, howLong, out last, others);

    [Fact]
    public void HealthyIdleOperationNeverTriggersRecovery()
    {
        var coordinator = Coordinator();
        for (int i = 0; i < 2000; i++)
        {
            var status = Status(_process, true,
                Folder("A", SyncActivity.Idle, TimeSpan.FromHours(1), health: Health.Ok),
                Folder("B", SyncActivity.Idle, TimeSpan.FromHours(2), health: Health.Ok));
            var decision = coordinator.Observe(status);
            Assert.Equal(DecisionKind.None, decision.Kind);
            Assert.False(status.Folders.Any(f => f.Stalled));
            _clock.Advance(TimeSpan.FromSeconds(4));
        }
    }

    [Fact]
    public void WaitingBehindAProgressingFolderNeverTriggersRecovery()
    {
        var coordinator = Coordinator();
        // Folder A has waited for hours, but folder B is syncing the whole time: that is the scheduler working.
        var decision = ObserveWaiting(coordinator, TimeSpan.FromHours(3), TimeSpan.FromMinutes(40), out var last,
            _ => [Folder("B", SyncActivity.Syncing, TimeSpan.FromMinutes(1))]);

        Assert.True(decision.Kind != DecisionKind.Due, "must not restart while another folder makes progress");
        Assert.False(last.Folders.Any(f => f.Stalled));
    }

    [Fact]
    public void LongWaitWithoutProgressIsFlaggedAndOnlyThenDue()
    {
        var coordinator = Coordinator();
        var waitingSince = _clock.Now.AddMinutes(-1);        // fixed: the log says when the wait began

        // After 9 minutes of observed waiting: not yet.
        var early = ObserveWaiting(coordinator, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(9), out var earlyStatus);
        Assert.True(early.Kind != DecisionKind.Due, "not due after 9 minutes");

        // Keep observing: after 10+ minutes observed (and 11+ in the log) it is due.
        var decision = RecoveryDecision.None;
        ClientStatus status = earlyStatus;
        for (int i = 0; i < 60 && decision.Kind != DecisionKind.Due; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(4));
            status = Status(_process, true, Folder("A", SyncActivity.Waiting, at: waitingSince));
            decision = coordinator.Observe(status);
        }

        Assert.Equal(DecisionKind.Due, decision.Kind);
        Assert.True(status.Folders[0].Stalled, "the folder is marked as possibly stuck");
        Assert.Equal(Health.Warning, status.Overall);
        Assert.Contains("may be stuck", status.Summary);
    }

    [Fact]
    public void StartupGraceSuppressesTheNotice()
    {
        var coordinator = Coordinator();
        var young = new ProcessIdentity(1, _clock.Now.AddSeconds(-30), _process.ExePath, 1);
        ClientStatus status = Status(young, true, Folder("A", SyncActivity.Waiting, TimeSpan.FromMinutes(30)));
        RecoveryDecision decision = RecoveryDecision.None;
        for (int i = 0; i < 20; i++)      // 80 seconds: still inside the two-minute grace
        {
            status = Status(young, true, Folder("A", SyncActivity.Waiting, TimeSpan.FromMinutes(30)));
            decision = coordinator.Observe(status);
            _clock.Advance(TimeSpan.FromSeconds(4));
        }

        Assert.False(status.Folders[0].Stalled);
        Assert.True(decision.Kind != DecisionKind.Due);
    }

    [Fact]
    public void SuspendAndResumeStartTheEvidenceOver()
    {
        var coordinator = Coordinator();
        ObserveWaiting(coordinator, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(9), out _);

        coordinator.NotifyResume();           // the computer was asleep: the old observations mean nothing
        RecoveryDecision decision = RecoveryDecision.None;
        for (int i = 0; i < 20; i++)          // right after waking: grace period
        {
            _clock.Advance(TimeSpan.FromSeconds(4));
            decision = coordinator.Observe(Status(_process, true, Folder("A", SyncActivity.Waiting, TimeSpan.FromMinutes(40))));
        }

        Assert.True(decision.Kind != DecisionKind.Due, "must not restart right after a resume");
    }

    [Fact]
    public void ChangingTheWallClockDoesNotTriggerRecovery()
    {
        var coordinator = Coordinator();
        // The clock jumps forward by five hours; the log says the folder has "waited" since long ago.
        _clock.JumpWallClock(TimeSpan.FromHours(5));
        RecoveryDecision decision = RecoveryDecision.None;
        for (int i = 0; i < 6; i++)           // ~20 seconds of real observation
        {
            decision = coordinator.Observe(Status(_process, true, Folder("A", SyncActivity.Waiting, TimeSpan.FromHours(5))));
            _clock.Advance(TimeSpan.FromSeconds(4));
        }

        Assert.True(decision.Kind != DecisionKind.Due, "durations are measured by observation, not by the wall clock");
    }

    [Fact]
    public void AnIncidentGetsOneAttemptEvenAfterTheCooldown()
    {
        var coordinator = Coordinator();
        var decision = ObserveWaiting(coordinator, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(11), out _);
        Assert.Equal(DecisionKind.Due, decision.Kind);

        Assert.True(coordinator.BeginAttempt(decision));
        Assert.Equal(1, _store.Items.Count);

        // The very same stall continues for three more hours: never a second attempt.
        _clock.Advance(TimeSpan.FromHours(3));
        for (int i = 0; i < 10; i++)
        {
            decision = coordinator.Observe(Status(_process, true, Folder("A", SyncActivity.Waiting, TimeSpan.FromMinutes(1) + TimeSpan.FromMinutes(11) + TimeSpan.FromHours(3))));
            _clock.Advance(TimeSpan.FromSeconds(4));
        }
        Assert.True(decision.Kind != DecisionKind.Due, "one attempt per incident");
    }

    [Fact]
    public void CooldownAndDailyLimitSurviveARestartOfCompanion()
    {
        // The attempt history lives in the store, not in the coordinator object.
        var utc = _clock.Now.ToUniversalTime();
        _store.Items.Add(utc.AddMinutes(-30));
        var afterRestart = Coordinator();
        var cooling = ObserveWaiting(afterRestart, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(11), out _);
        Assert.Equal(DecisionKind.Suppressed, cooling.Kind);
        Assert.Contains("less than an hour", cooling.Reason);

        var limited = new FakeStore();
        limited.Items.AddRange([utc.AddHours(-20), utc.AddHours(-5)]);
        var decision = ObserveWaiting(Coordinator(limited), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(11), out _);
        Assert.Equal(DecisionKind.Suppressed, decision.Kind);
        Assert.Contains("limited to 2 per 24 hours", decision.Reason);

        var oldHistory = new FakeStore();
        oldHistory.Items.Add(utc.AddHours(-2));
        Assert.Equal(DecisionKind.Due, ObserveWaiting(Coordinator(oldHistory), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(11), out _).Kind);
    }

    [Fact]
    public void AnEntryFromTheFutureCountsAsRecent()
    {
        var store = new FakeStore();
        store.Items.Add(_clock.Now.ToUniversalTime().AddHours(5));      // the clock was set back
        var decision = ObserveWaiting(Coordinator(store), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(11), out _);

        Assert.Equal(DecisionKind.Suppressed, decision.Kind);
    }

    [Fact]
    public void AutomaticRestartIsDisabledWhenTheLogShowsNoSyncStates()
    {
        var coordinator = Coordinator();
        var decision = coordinator.Observe(Status(_process, false, Folder("A", SyncActivity.Unknown)));

        Assert.Equal(DecisionKind.Disabled, decision.Kind);
        Assert.Contains("cannot tell waiting from normal idle", decision.Reason);
    }

    [Fact]
    public void NothingIsDoneWhileTheSettingIsOff()
    {
        _enabled = false;
        var decision = ObserveWaiting(Coordinator(), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15), out var last);

        Assert.Equal(DecisionKind.None, decision.Kind);
        Assert.True(last.Folders[0].Stalled, "the notice is still shown, only the unattended action is off");
    }

    [Fact]
    public void AccountProblemsAndPausedFoldersMeanWaitingIsExpected()
    {
        // Another folder of the same account is paused.
        var coordinator = Coordinator();
        var d1 = ObserveWaiting(coordinator, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15), out var s1,
            _ => [Folder("P", SyncActivity.Paused, paused: true)]);
        Assert.False(s1.Folders[0].Stalled);
        Assert.True(d1.Kind != DecisionKind.Due);

        // The server of the account is unreachable.
        var d2 = ObserveWaiting(Coordinator(), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15), out var s2,
            _ => [Folder("D", SyncActivity.Unknown, health: Health.Disconnected, error: "Server unreachable")]);
        Assert.False(s2.Folders[0].Stalled);
        Assert.True(d2.Kind != DecisionKind.Due);

        // The account needs the user to sign in.
        var d3 = ObserveWaiting(Coordinator(), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15), out var s3,
            _ => [Folder("E", SyncActivity.Unknown, health: Health.Error, error: "Credentials required: 401 Unauthorized")]);
        Assert.False(s3.Folders[0].Stalled);
        Assert.True(d3.Kind != DecisionKind.Due);
    }

    [Fact]
    public void InsufficientMonitoringSuppressesRecovery()
    {
        var coordinator = Coordinator();
        ClientStatus stale = new()
        {
            ProcessRunning = true,
            Process = _process,
            Folders = [Folder("A", SyncActivity.Waiting, TimeSpan.FromHours(2))],
            Observation = new Observation { State = ObservationState.NoRecentRecords, ShowsSyncStates = true }
        };
        var decision = coordinator.Observe(stale);

        Assert.Equal(DecisionKind.Suppressed, decision.Kind);
        Assert.False(stale.Folders[0].Stalled);
    }

    [Fact]
    public void ARestartHistoryThatCannotBeSavedIsReported()
    {
        var failing = new FakeStore { FailToSave = true };
        var coordinator = Coordinator(failing);
        var decision = ObserveWaiting(coordinator, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(11), out _);
        Assert.Equal(DecisionKind.Due, decision.Kind);

        Assert.False(coordinator.BeginAttempt(decision), "the caller must see that the limits could not be persisted");
    }

    [Fact]
    public void SwitchingTheOptionOnLaterUsesTheEvidenceAlreadyCollected()
    {
        _enabled = false;                    // Companion was started with the option off
        var coordinator = Coordinator();
        var since = _clock.Now.AddMinutes(-1);
        ObserveWaiting(coordinator, since, TimeSpan.FromMinutes(12), out _);

        _enabled = true;                     // ...and the user switches it on in the menu, without restarting Companion
        var decision = coordinator.Observe(Status(_process, true, Folder("A", SyncActivity.Waiting, at: since)));

        Assert.Equal(DecisionKind.Due, decision.Kind);
    }

    [Fact]
    public void TheForecastCountsDownToTheAttempt()
    {
        var coordinator = Coordinator();
        var since = _clock.Now.AddMinutes(-1);
        ObserveWaiting(coordinator, since, TimeSpan.FromMinutes(4), out _);

        var remaining = coordinator.Forecast;
        Assert.NotNull(remaining, "a stall is being counted down");
        Assert.True(remaining!.Value > TimeSpan.FromMinutes(5) && remaining.Value <= TimeSpan.FromMinutes(7), "about six minutes left: " + remaining);
        Assert.Contains("attempting automatic recovery in about", coordinator.ForecastNote!);

        ObserveWaiting(coordinator, since, TimeSpan.FromMinutes(7), out _);
        Assert.Equal(TimeSpan.Zero, coordinator.Forecast);
    }

    [Fact]
    public void NoForecastWhenWaitingIsExpected()
    {
        var coordinator = Coordinator();
        ObserveWaiting(coordinator, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), out _,
            _ => [Folder("P", SyncActivity.Paused, paused: true)]);
        Assert.Null(coordinator.Forecast);

        var healthy = Coordinator();
        healthy.Observe(Status(_process, true, Folder("A", SyncActivity.Idle, TimeSpan.FromHours(1), health: Health.Ok)));
        Assert.Null(healthy.Forecast);
    }

    [Fact]
    public void ForecastAfterAnAttemptIsTheRemainingCooldown()
    {
        var store = new FakeStore();
        store.Items.Add(_clock.Now.ToUniversalTime().AddMinutes(-20));
        var coordinator = Coordinator(store);
        ObserveWaiting(coordinator, _clock.Now.AddMinutes(-1), TimeSpan.FromMinutes(11), out _);

        var left = coordinator.Forecast;
        Assert.NotNull(left);
        Assert.True(left!.Value > TimeSpan.FromMinutes(25) && left.Value < TimeSpan.FromMinutes(40), "about 29 minutes of cooldown left: " + left);
    }

    private ClientStatus SilentStatus(ProcessIdentity process, params FolderStatus[] folders) => new()
    {
        ProcessRunning = true,
        Process = process,
        Folders = folders,
        CheckedAt = _clock.Now,
        Observation = new Observation
        {
            State = ObservationState.NoRecentRecords,
            Silent = true,
            ShowsSyncStates = true,
            LastRecordAt = _clock.Now.AddMinutes(-20)
        }
    };

    [Fact]
    public void ASilentLogThatLastSaidWaitingIsStillAStall()
    {
        // A stalled client writes nothing at all: the newest record is old, but the last state it logged is "waiting".
        var coordinator = Coordinator();
        var since = _clock.Now.AddMinutes(-30);               // after the process started (3 hours ago)
        var decision = RecoveryDecision.None;
        ClientStatus status = SilentStatus(_process);
        for (int i = 0; i < 200 && decision.Kind != DecisionKind.Due; i++)
        {
            status = SilentStatus(_process, Folder("A", SyncActivity.Waiting, at: since));
            decision = coordinator.Observe(status);
            _clock.Advance(TimeSpan.FromSeconds(4));
        }

        Assert.Equal(DecisionKind.Due, decision.Kind);
        Assert.True(status.Folders[0].Stalled);
    }

    [Fact]
    public void ASilentLogFromBeforeTheProcessStartedIsNoEvidence()
    {
        var coordinator = Coordinator();
        var since = _clock.Now.AddHours(-5);                  // written before the current process existed
        var decision = RecoveryDecision.None;
        for (int i = 0; i < 400; i++)
        {
            decision = coordinator.Observe(SilentStatus(_process, Folder("A", SyncActivity.Waiting, at: since)));
            _clock.Advance(TimeSpan.FromSeconds(4));
        }

        Assert.True(decision.Kind != DecisionKind.Due, "an old instance's log must not restart the current one");
        Assert.Null(coordinator.Forecast);
    }

    [Fact]
    public void ASilentLogWithoutSyncStatesIsStillUnusable()
    {
        var coordinator = Coordinator();
        var status = new ClientStatus
        {
            ProcessRunning = true,
            Process = _process,
            Folders = [Folder("A", SyncActivity.Waiting, at: _clock.Now.AddMinutes(-30))],
            Observation = new Observation { State = ObservationState.NoRecentRecords, Silent = false, ShowsSyncStates = false }
        };

        Assert.Equal(DecisionKind.Suppressed, coordinator.Observe(status).Kind);
    }
}
