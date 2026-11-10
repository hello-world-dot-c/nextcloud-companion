namespace NextcloudCompanion.Tests;

/// <summary>The restart sequence: what is asked, in which order, and what is claimed afterwards.</summary>
public sealed class RecoveryTests
{
    private const string Exe = @"C:\Program Files\Nextcloud\nextcloud.exe";

    private readonly FakeClock _clock = new() { AutoAdvanceMilliseconds = 1000 };
    private readonly FakeProcessController _processes;
    private readonly List<RecoveryStatus> _progress = [];

    /// <summary>What the next scan shows about the folders; set by each test.</summary>
    private Func<ClientStatus> _scan;

    public RecoveryTests()
    {
        _processes = new FakeProcessController(_clock);
        _scan = () => Status(Folder(syncedAfterLaunch: true));
    }

    private FolderStatus Folder(bool syncedAfterLaunch, string name = "Nextcloud") => new()
    {
        LocalPath = @"C:\Users\alice\" + name,
        DisplayName = name,
        Account = "alice@cloud.example.com",
        DavUser = "alice",
        ServerHost = "cloud.example.com",
        ServerUrl = "https://cloud.example.com",
        TargetPath = "/",
        Activity = syncedAfterLaunch ? SyncActivity.Idle : SyncActivity.Waiting,
        ActivitySince = syncedAfterLaunch ? _clock.Now : _clock.Now.AddHours(-1),
        LastSuccessfulSyncAt = syncedAfterLaunch ? _clock.Now : _clock.Now.AddHours(-1),
        Health = Health.Ok
    };

    private ClientStatus Status(params FolderStatus[] folders)
    {
        var process = _processes.Clients.FirstOrDefault();
        return new ClientStatus
        {
            ProcessRunning = process is not null,
            Process = process,
            Folders = folders,
            CheckedAt = _clock.Now,
            Observation = new Observation { State = ObservationState.Ok, ShowsSyncStates = true, LastRecordAt = _clock.Now }
        };
    }

    private ClientRecoveryService Service() => new(_processes, new FakeStatusSource(() => _scan()), _clock, new RecoveryOptions
    {
        QuitWait = TimeSpan.FromSeconds(5),
        VerifyWait = TimeSpan.FromSeconds(30),
        PollInterval = TimeSpan.FromMilliseconds(1)
    });

    private sealed class Collect(List<RecoveryStatus> into) : IProgress<RecoveryStatus>
    {
        public void Report(RecoveryStatus value) => into.Add(value);
    }

    [Fact]
    public async Task ARestartQuitsWaitsThenLaunchesInThatOrder()
    {
        _processes.AddClient();
        var result = await Service().RestartAsync(false, new Collect(_progress), CancellationToken.None);

        Assert.Equal(RecoveryResultKind.Resumed, result.Kind);
        Assert.Equal("quit,wait,launch", string.Join(",", _processes.Calls));
        Assert.Equal(1, _processes.Launched.Count);
        Assert.Equal("--background", _processes.Launched[0][0]);
        Assert.True(_progress.Any(p => p.Phase == RecoveryPhase.Restarting));
        Assert.True(_progress.Any(p => p.Phase == RecoveryPhase.WaitingForEvidence));
        Assert.Equal(RecoveryPhase.Resumed, _progress.Last().Phase);
    }

    [Fact]
    public async Task TheConfigurationContextIsPreserved()
    {
        _processes.AddClient();
        _processes.CommandLineText = "\"" + Exe + "\" --confdir \"D:\\My Config\" --logdebug --logdir=D:\\logs --unrelated value";
        await Service().RestartAsync(false, null, CancellationToken.None);

        string[] expected = ["--confdir", @"D:\My Config", "--logdebug", @"--logdir=D:\logs"];
        Assert.Equal(string.Join("|", expected), string.Join("|", _processes.QuitArguments[0]));
        Assert.Equal("--background|" + string.Join("|", expected), string.Join("|", _processes.Launched[0]));
    }

    [Fact]
    public async Task AClientThatDoesNotExitIsLeftAloneAndNoDuplicateIsStarted()
    {
        var original = _processes.AddClient();
        _processes.ExitsAfterQuit = false;

        var result = await Service().RestartAsync(false, new Collect(_progress), CancellationToken.None);

        Assert.Equal(RecoveryResultKind.QuitFailed, result.Kind);
        Assert.False(_processes.Calls.Contains("launch"), "no second client may be started");
        Assert.False(_processes.Calls.Contains("kill"), "a graceful restart never kills");
        Assert.True(_processes.Clients.Contains(original));
        Assert.Equal(RecoveryPhase.Failed, _progress.Last().Phase);
    }

    [Fact]
    public async Task ARefusedQuitRequestChangesNothing()
    {
        _processes.AddClient();
        _processes.QuitSucceeds = false;

        var result = await Service().RestartAsync(false, null, CancellationToken.None);

        Assert.Equal(RecoveryResultKind.QuitFailed, result.Kind);
        Assert.False(_processes.Calls.Contains("launch"));
        Assert.Equal(1, _processes.Clients.Count);
    }

    [Fact]
    public async Task NothingIsDoneWithoutExactlyOneIdentifiableClient()
    {
        // None running.
        var none = await Service().RestartAsync(false, null, CancellationToken.None);
        Assert.Equal(RecoveryResultKind.Refused, none.Kind);

        // Two running.
        _processes.AddClient();
        _processes.AddClient();
        var two = await Service().RestartAsync(false, null, CancellationToken.None);
        Assert.Equal(RecoveryResultKind.Refused, two.Kind);
        Assert.Contains("More than one", two.Message);

        // Unknown executable.
        _processes.Clients.Clear();
        _processes.AddClient(exe: null);
        var unknown = await Service().RestartAsync(false, null, CancellationToken.None);
        Assert.Equal(RecoveryResultKind.Refused, unknown.Kind);

        // Command line not readable.
        _processes.Clients.Clear();
        _processes.AddClient();
        _processes.CommandLineText = null;
        var unreadable = await Service().RestartAsync(false, null, CancellationToken.None);
        Assert.Equal(RecoveryResultKind.Refused, unreadable.Kind);

        Assert.Equal(0, _processes.Calls.Count);
    }

    [Fact]
    public async Task ANewProcessWithoutResumedSyncIsNotReportedAsRecovered()
    {
        _processes.AddClient();
        _scan = () => Status(Folder(syncedAfterLaunch: false));

        var result = await Service().RestartAsync(false, new Collect(_progress), CancellationToken.None);

        Assert.Equal(RecoveryResultKind.NotVerified, result.Kind);
        Assert.False(result.Succeeded);
        Assert.Contains("could not confirm", result.Message);
        Assert.Equal(RecoveryPhase.Failed, _progress.Last().Phase);
    }

    [Fact]
    public async Task EveryEnabledFolderMustShowEvidence()
    {
        _processes.AddClient();
        _scan = () => Status(Folder(true, "Good"), Folder(false, "Silent"));

        var result = await Service().RestartAsync(false, null, CancellationToken.None);

        Assert.Equal(RecoveryResultKind.NotVerified, result.Kind);
        Assert.Contains("Silent", result.Message);
        Assert.DoesNotContain("Good,", result.Message);
    }

    [Fact]
    public async Task AFolderInErrorIsNoEvidence()
    {
        _processes.AddClient();
        _scan = () =>
        {
            var f = Folder(true);
            f.Health = Health.Error;
            return Status(f);
        };

        var result = await Service().RestartAsync(false, null, CancellationToken.None);
        Assert.Equal(RecoveryResultKind.NotVerified, result.Kind);
    }

    [Fact]
    public async Task ASuccessfulCycleWithNothingToDoCountsAsEvidence()
    {
        _processes.AddClient();
        _scan = () =>
        {
            var f = Folder(false);
            f.LastSuccessfulSyncAt = _clock.Now;          // "Success" after the relaunch, no files changed
            return Status(f);
        };

        var result = await Service().RestartAsync(false, null, CancellationToken.None);
        Assert.Equal(RecoveryResultKind.Resumed, result.Kind);
    }

    [Fact]
    public async Task ARestartThatLeavesNoRunningClientSaysSo()
    {
        _processes.AddClient();
        _processes.LaunchStartsClient = false;

        var result = await Service().RestartAsync(false, null, CancellationToken.None);

        Assert.Equal(RecoveryResultKind.NotVerified, result.Kind);
        Assert.Contains("not running now", result.Message);
    }

    [Fact]
    public async Task IfNoClientAppearsTheStartIsRetriedOncePlainly()
    {
        _processes.AddClient();
        _processes.CommandLineText = "\"" + Exe + "\" --confdir D:\\cfg";
        _processes.LaunchStartsClient = false;

        await Service().RestartAsync(false, null, CancellationToken.None);

        Assert.Equal(2, _processes.Launched.Count);
        Assert.Equal("--background|--confdir|D:\\cfg", string.Join("|", _processes.Launched[0]));
        Assert.Equal("--confdir|D:\\cfg", string.Join("|", _processes.Launched[1]));
    }

    [Fact]
    public async Task ARunningClientIsNeverStartedTwice()
    {
        _processes.AddClient();
        await Service().RestartAsync(false, null, CancellationToken.None);
        Assert.Equal(1, _processes.Launched.Count);
    }

    [Fact]
    public async Task NoSecondClientIsStartedWhenAnotherAppearedMeanwhile()
    {
        _processes.AddClient();
        // The client quits, and (as with a scheduled task or the autostart entry) another instance comes up on its own.
        var service = Service();
        _processes.AfterLaunch = null;
        var task = service.RestartAsync(false, null, CancellationToken.None);
        await task;
        // The normal path launches exactly once.
        Assert.Equal(1, _processes.Launched.Count);

        _processes.Calls.Clear();
        _processes.Launched.Clear();
        var other = new FakeProcessController(_clock);
        other.AddClient();
        var existing = other.Clients[0];
        other.QuitSucceeds = true;
        // Simulate: after quit, a different instance is already present.
        var racing = new RacingProcesses(other, existing);
        var raced = new ClientRecoveryService(racing, new FakeStatusSource(() => Status(Folder(true))), _clock, new RecoveryOptions
        {
            QuitWait = TimeSpan.FromSeconds(1), VerifyWait = TimeSpan.FromSeconds(5), PollInterval = TimeSpan.FromMilliseconds(1)
        });
        await raced.RestartAsync(false, null, CancellationToken.None);
        Assert.Equal(0, other.Launched.Count);
    }

    /// <summary>After the original client is told to quit, an unrelated instance is already running.</summary>
    private sealed class RacingProcesses(FakeProcessController inner, ProcessIdentity original) : IProcessController
    {
        private bool _quit;

        public IReadOnlyList<ProcessIdentity> FindClients()
            => _quit ? [new ProcessIdentity(9999, DateTime.Now, original.ExePath, 1)] : inner.FindClients();

        public bool IsSame(ProcessIdentity process) => !_quit && inner.IsSame(process);
        public string? GetCommandLine(ProcessIdentity process) => inner.GetCommandLine(process);

        public async Task<bool> RequestQuitAsync(string exePath, IReadOnlyList<string> contextArguments, CancellationToken cancellationToken)
        {
            var result = await inner.RequestQuitAsync(exePath, contextArguments, cancellationToken);
            _quit = true;
            return result;
        }

        public Task<bool> WaitForExitAsync(ProcessIdentity process, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(true);
        public void Launch(string exePath, IReadOnlyList<string> arguments) => inner.Launch(exePath, arguments);
        public void ForceKill(ProcessIdentity process) => inner.ForceKill(process);
    }

    [Fact]
    public async Task ASecondRestartWhileOneRunsIsBusy()
    {
        _processes.AddClient();
        var gate = new TaskCompletionSource();
        _processes.AfterLaunch = null;
        var slow = new SlowQuit(_processes, gate.Task);
        var service = new ClientRecoveryService(slow, new FakeStatusSource(() => Status(Folder(true))), _clock, new RecoveryOptions
        {
            QuitWait = TimeSpan.FromSeconds(5), VerifyWait = TimeSpan.FromSeconds(5), PollInterval = TimeSpan.FromMilliseconds(1)
        });

        var first = service.RestartAsync(false, null, CancellationToken.None);
        Assert.True(service.Busy, "a restart is in progress");
        var second = await service.RestartAsync(true, null, CancellationToken.None);
        Assert.Equal(RecoveryResultKind.Busy, second.Kind);

        gate.SetResult();
        var firstResult = await first;
        Assert.Equal(RecoveryResultKind.Resumed, firstResult.Kind);
        Assert.False(service.Busy);
        Assert.Equal(1, _processes.Launched.Count);
    }

    private sealed class SlowQuit(FakeProcessController inner, Task release) : IProcessController
    {
        public IReadOnlyList<ProcessIdentity> FindClients() => inner.FindClients();
        public bool IsSame(ProcessIdentity process) => inner.IsSame(process);
        public string? GetCommandLine(ProcessIdentity process) => inner.GetCommandLine(process);

        public async Task<bool> RequestQuitAsync(string exePath, IReadOnlyList<string> contextArguments, CancellationToken cancellationToken)
        {
            await release;
            return await inner.RequestQuitAsync(exePath, contextArguments, cancellationToken);
        }

        public Task<bool> WaitForExitAsync(ProcessIdentity process, TimeSpan timeout, CancellationToken cancellationToken)
            => inner.WaitForExitAsync(process, timeout, cancellationToken);

        public void Launch(string exePath, IReadOnlyList<string> arguments) => inner.Launch(exePath, arguments);
        public void ForceKill(ProcessIdentity process) => inner.ForceKill(process);
    }

    [Fact]
    public async Task ForcedRestartIsExplicitAndRevalidatesTheTarget()
    {
        var original = _processes.AddClient();
        _processes.ExitsAfterQuit = false;
        var service = Service();

        var graceful = await service.RestartAsync(false, null, CancellationToken.None);
        Assert.Equal(RecoveryResultKind.QuitFailed, graceful.Kind);
        Assert.False(_processes.Calls.Contains("kill"));

        // The target the user was shown is not the one running any more: refused, nothing killed.
        var stale = new ProcessIdentity(original.Id, original.StartTime.AddMinutes(1), original.ExePath, 1);
        var refused = await service.ForceRestartAsync(stale, null, CancellationToken.None);
        Assert.Equal(RecoveryResultKind.Refused, refused.Kind);
        Assert.False(_processes.Calls.Contains("kill"));

        // The very process the user confirmed: killed, then started again after the exit.
        _processes.Calls.Clear();
        var forced = await service.ForceRestartAsync(original, null, CancellationToken.None);
        Assert.Equal(RecoveryResultKind.Resumed, forced.Kind);
        Assert.Equal("kill,wait,launch", string.Join(",", _processes.Calls));
    }

    [Fact]
    public async Task ACancelledRestartIsReportedAsCancelled()
    {
        _processes.AddClient();
        _scan = () => Status(Folder(false));
        using var cts = new CancellationTokenSource();
        var service = new ClientRecoveryService(_processes, new FakeStatusSource(() =>
        {
            cts.Cancel();
            return Status(Folder(false));
        }), _clock, new RecoveryOptions { VerifyWait = TimeSpan.FromMinutes(5), PollInterval = TimeSpan.FromSeconds(1) });

        var result = await service.RestartAsync(false, null, cts.Token);
        Assert.Equal(RecoveryResultKind.Cancelled, result.Kind);
    }

    [Fact]
    public async Task ARestartNeverTouchesTheClientConfiguration()
    {
        using var ws = new Workspace();
        ws.WriteConfig(Fixture.Text("nextcloud-35.cfg"));
        string before = File.ReadAllText(ws.ConfigPath);
        var journal = ws.Dir.Combine(".sync_aaaa1111.db");
        File.WriteAllText(journal, "journal");

        ws.Processes.AddClient();
        var service = new ClientRecoveryService(ws.Processes, new FakeStatusSource(() => ws.Monitor().Scan()), ws.Clock, new RecoveryOptions
        {
            VerifyWait = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(1)
        });
        ws.Clock.AutoAdvanceMilliseconds = 500;
        await service.RestartAsync(false, null, CancellationToken.None);

        Assert.Equal(before, File.ReadAllText(ws.ConfigPath));
        Assert.Equal("journal", File.ReadAllText(journal));
    }
}
