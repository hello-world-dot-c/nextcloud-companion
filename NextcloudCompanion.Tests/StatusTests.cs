using System.IO.Compression;
using static NextcloudCompanion.Tests.LogLines;

namespace NextcloudCompanion.Tests;

/// <summary>What Companion claims about the client, given what the config and the logs actually show.</summary>
public sealed class StatusTests
{
    private static readonly string Alice = Remote("cloud.example.com", "alice");

    private static Workspace Scratch(out DateTime now, params (string Local, string Target, bool Paused)[] folders)
    {
        var ws = new Workspace();
        ws.WriteConfig(Workspace.Config("cloud.example.com", "alice", folders.Length > 0 ? folders : [("C:/Users/alice/Nextcloud/", "/", false)]));
        ws.Processes.AddClient();
        now = ws.Clock.Now;
        return ws;
    }

    private static string LogFile(Workspace ws, string name = "20261110_1100_nextcloud.log.0") => Path.Combine(ws.LogDir, name);

    // ---- running client without evidence ---------------------------------------------------------------------

    [Fact]
    public void RunningClientWithoutSyncRecordsIsUnknownNotHealthy()
    {
        using var ws = Scratch(out var now);
        Write(LogFile(ws), [Heartbeat(now.AddMinutes(-1))]);
        var status = ws.Monitor().Scan();

        Assert.True(status.ProcessRunning);
        Assert.Equal(ObservationState.NoRecentRecords, status.Observation.State);
        Assert.Equal(Health.Unknown, status.Overall);
        Assert.Contains("No recent sync activity", status.Summary);
        Assert.Contains("no sync records", status.Summary);
        Assert.DoesNotContain("successfully", status.Summary);
    }

    [Fact]
    public void MissingLogDirectoryIsReportedAsUnavailable()
    {
        using var ws = Scratch(out _);
        Directory.Delete(ws.LogDir, recursive: true);
        var status = ws.Monitor().Scan();

        Assert.Equal(ObservationState.LogsMissing, status.Observation.State);
        Assert.Contains("cannot read the activity source", status.Summary);
        Assert.Equal(Health.Unknown, status.Overall);
    }

    [Fact]
    public void LogFileThatCannotBeOpenedIsReportedAsUnreadable()
    {
        using var ws = Scratch(out var now);
        string file = LogFile(ws);
        Write(file, [State(now.AddMinutes(-1), Alice, "Success")]);
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var status = ws.Monitor().Scan();

        Assert.Equal(ObservationState.LogsUnreadable, status.Observation.State);
        Assert.Equal(1, status.Observation.UnreadableFiles);
        Assert.Contains("cannot read the activity source", status.Summary);
    }

    [Fact]
    public void LogWithoutTimestampsIsReportedAsUnsupported()
    {
        using var ws = Scratch(out _);
        File.WriteAllText(LogFile(ws), "this is not\na client log\n");
        var status = ws.Monitor().Scan();

        Assert.Equal(ObservationState.UnsupportedFormat, status.Observation.State);
        Assert.Contains("not in a format Companion understands", status.Summary);
    }

    [Fact]
    public void StaleLogIsNotPresentedAsCurrent()
    {
        using var ws = Scratch(out var now);
        Write(LogFile(ws), [State(now.AddHours(-3), Alice, "Success")]);
        var status = ws.Monitor().Scan();

        Assert.Equal(ObservationState.NoRecentRecords, status.Observation.State);
        Assert.Contains("No recent sync activity", status.Summary);
        Assert.Contains("newest log record is from", status.Summary);
        Assert.Equal(Health.Unknown, status.Overall);
    }

    // ---- what a folder is doing -----------------------------------------------------------------------------

    [Fact]
    public void WaitingPreparingAndSyncingComeFromTheStateRecords()
    {
        using var ws = Scratch(out var now);
        string file = LogFile(ws);
        Write(file, [State(now.AddMinutes(-3), Alice, "Not yet started")]);
        var waiting = ws.Monitor().Scan();
        Assert.Equal(SyncActivity.Waiting, waiting.Folders[0].Activity);
        Assert.Contains("waiting to start syncing", waiting.Summary);

        Write(file, [State(now.AddMinutes(-3), Alice, "Not yet started"), State(now.AddMinutes(-2), Alice, "Preparing to sync"),
                     State(now.AddMinutes(-1), Alice, "Sync running")]);
        var syncing = ws.Monitor().Scan();
        Assert.Equal(SyncActivity.Syncing, syncing.Folders[0].Activity);
        Assert.Contains("is syncing", syncing.Summary);
    }

    [Fact]
    public void SuccessfulRunIsReportedWithItsTime()
    {
        using var ws = Scratch(out var now);
        var t = now.AddMinutes(-2);
        Write(LogFile(ws), [State(t, Alice, "Sync running"), State(t.AddSeconds(5), Alice, "Success")]);
        var status = ws.Monitor().Scan();

        Assert.Equal(Health.Ok, status.Overall);
        Assert.Equal(SyncActivity.Idle, status.Folders[0].Activity);
        Assert.Contains("Last sync completed successfully at", status.Summary);
        Assert.Equal(t.AddSeconds(5).ToString("HH:mm:ss"), status.Folders[0].LastSuccessfulSyncAt!.Value.ToString("HH:mm:ss"));
    }

    [Fact]
    public void ConnectionCheckAloneDoesNotInventASuccessfulSync()
    {
        using var ws = Scratch(out var now);
        Write(LogFile(ws), [EtagOk(now.AddMinutes(-1), Alice)]);
        var status = ws.Monitor().Scan();
        var folder = status.Folders[0];

        Assert.Equal(Health.Unknown, folder.Health);
        Assert.Null(folder.LastSuccessfulSyncAt);
        Assert.NotNull(folder.LastConnectionCheckAt);
        Assert.Equal(Health.Unknown, status.Overall);
        Assert.DoesNotContain("successfully", status.Summary);
    }

    [Fact]
    public void FailedRunIsNotStoredAsSuccessfulCompletion()
    {
        using var ws = Scratch(out var now);
        string local = "C:/Users/alice/Nextcloud";
        Write(LogFile(ws), [Started(now.AddMinutes(-3), local, "alice@cloud.example.com", Alice),
                            EngineError(now.AddMinutes(-2)),
                            Finished(now.AddMinutes(-1), local, "alice@cloud.example.com", Alice)]);
        var folder = ws.Monitor().Scan().Folders[0];

        Assert.Equal(Health.Error, folder.Health);
        Assert.Null(folder.LastSuccessfulSyncAt);
        Assert.NotNull(folder.LastSyncCompletedAt);
        Assert.NotNull(folder.LastSyncStartedAt);
    }

    // ---- several folders --------------------------------------------------------------------------------------

    [Fact]
    public void OneHealthyAndOneUnknownFolderIsNotOverallHealthy()
    {
        using var ws = Scratch(out var now, ("C:/Users/alice/A/", "/A", false), ("C:/Users/alice/B/", "/B", false));
        Write(LogFile(ws), [State(now.AddMinutes(-2), Remote("cloud.example.com", "alice", "A/"), "Success")]);
        var status = ws.Monitor().Scan();

        Assert.Equal(Health.Unknown, status.Overall);
        Assert.Contains("1 of 2 folders", status.Summary);
        Assert.DoesNotContain("Last sync completed successfully", status.Summary);
    }

    [Fact]
    public void AllPausedIsReportedAsPaused()
    {
        using var ws = Scratch(out var now, ("C:/Users/alice/A/", "/A", true), ("C:/Users/alice/B/", "/B", true));
        Write(LogFile(ws), [Heartbeat(now.AddMinutes(-1))]);
        var status = ws.Monitor().Scan();

        Assert.Equal(Health.Paused, status.Overall);
        Assert.Equal("All folders are paused", status.Summary);
        Assert.True(status.Folders.All(f => f.Activity == SyncActivity.Paused));
    }

    [Fact]
    public void ServerFailureIsStillReported()
    {
        using var ws = Scratch(out var now);
        Write(LogFile(ws), [Line(now.AddMinutes(-1), "Account \"alice@cloud.example.com\" disconnected or paused")]);
        var status = ws.Monitor().Scan();

        Assert.Equal(Health.Disconnected, status.Overall);
        Assert.Contains("is unreachable", status.Summary);
    }

    // ---- configuration and attribution ------------------------------------------------------------------------

    [Fact]
    public void EveryConfigurationGroupIsRecognised()
    {
        var data = ConfigReader.Parse(Fixture.Text("nextcloud-35.cfg"));

        Assert.Equal(4, data.Folders.Count);
        Assert.True(data.Folders.Any(f => f.TargetPath == "/Projects"), "FoldersWithPlaceholders");
        Assert.True(data.Folders.Any(f => f.TargetPath == "/Shared" && f.Paused), "Multifolders");
    }

    [Fact]
    public void SamePathOnTwoAccountsKeepsBothFolders()
    {
        var data = ConfigReader.Parse(Fixture.Text("nextcloud-35.cfg"));
        var shared = data.Folders.Where(f => f.LocalPath.EndsWith("Nextcloud", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Equal(2, shared.Count);
        Assert.True(shared[0].Identity != shared[1].Identity, "identities must differ");
    }

    [Fact]
    public void RecordsAreAttributedByAccountAndRemoteFolderNotByPathAlone()
    {
        using var ws = new Workspace();
        ws.WriteConfig(Fixture.Text("nextcloud-35.cfg"));
        ws.Processes.AddClient();
        ws.Clock.Now = new DateTime(2026, 11, 10, 10, 5, 0, DateTimeKind.Local);
        File.WriteAllText(LogFile(ws), Fixture.Text("client-35.0.3.log"));
        var status = ws.Monitor().Scan();

        var root = status.Folders.Single(f => f.TargetPath == "/" && f.DavUser == "alice");
        var edu = status.Folders.Single(f => f.DavUser == "alice@example.edu");
        var projects = status.Folders.Single(f => f.TargetPath == "/Projects");
        var multi = status.Folders.Single(f => f.TargetPath == "/Shared");

        Assert.Equal(SyncActivity.Idle, root.Activity);
        Assert.NotNull(root.LastSuccessfulSyncAt);
        // Same local path, other account: it must not inherit the first account's success.
        Assert.Null(edu.LastSuccessfulSyncAt);
        Assert.Equal(SyncActivity.Waiting, edu.Activity);
        Assert.Equal(SyncActivity.Waiting, projects.Activity);
        Assert.NotNull(projects.LastConnectionCheckAt);
        Assert.Equal(Health.Paused, multi.Health);
        Assert.Equal(0, status.Observation.UnattributedRecords);
    }

    [Fact]
    public void RecordsThatFitTwoFoldersStayUnattributed()
    {
        using var ws = new Workspace();
        ws.WriteConfig(Workspace.Config(
        [
            ("cloud.example.com", "alice", [("C:/Users/shared/Nextcloud/", "/", false)]),
            ("files.example.edu", "alice", [("C:/Users/shared/Nextcloud/", "/", false)])
        ]));
        ws.Processes.AddClient();
        var now = ws.Clock.Now;
        // Same local path on both accounts, and the record names neither of them.
        Write(LogFile(ws), [Started(now.AddMinutes(-2), "C:/Users/shared/Nextcloud", "someone@elsewhere", ""),
                            EngineOk(now.AddMinutes(-1))]);
        var status = ws.Monitor().Scan();

        Assert.True(status.Folders.All(f => f.LastSuccessfulSyncAt is null), "nothing may be guessed");
        Assert.True(status.Observation.UnattributedRecords >= 1, "the records are counted as unattributed");
    }

    // ---- log files ---------------------------------------------------------------------------------------------

    [Fact]
    public void RotatedAndCompressedLogsAreReadOldestFirst()
    {
        using var ws = Scratch(out var now);
        WriteGzip(Path.Combine(ws.LogDir, "20261110_1050_nextcloud.log.1.gz"),
            [State(now.AddMinutes(-9), Alice, "Not yet started"), State(now.AddMinutes(-8), Alice, "Success")]);
        Write(Path.Combine(ws.LogDir, "20261110_1100_nextcloud.log.0"),
            [State(now.AddMinutes(-1), Alice, "Sync running")]);
        File.SetLastWriteTime(Path.Combine(ws.LogDir, "20261110_1050_nextcloud.log.1.gz"), DateTime.Now.AddMinutes(-5));
        var folder = ws.Monitor().Scan().Folders[0];

        Assert.Equal(SyncActivity.Syncing, folder.Activity);
        Assert.NotNull(folder.LastSuccessfulSyncAt);
    }

    [Fact]
    public void TruncatedArchiveKeepsWhatWasReadAndCountsAsUnreadable()
    {
        using var ws = Scratch(out var now);
        string gz = Path.Combine(ws.LogDir, "20261110_1050_nextcloud.log.2.gz");
        var lines = Enumerable.Range(0, 4000).Select(i => Heartbeat(now.AddMinutes(-9).AddMilliseconds(i)))
            .Prepend(State(now.AddMinutes(-9), Alice, "Success")).ToList();
        WriteGzip(gz, lines);
        var bytes = File.ReadAllBytes(gz);
        File.WriteAllBytes(gz, bytes[..(bytes.Length / 2)]);
        Write(Path.Combine(ws.LogDir, "20261110_1100_nextcloud.log.0"), [Heartbeat(now.AddMinutes(-1))]);

        var status = ws.Monitor().Scan();

        Assert.Equal(1, status.Observation.UnreadableFiles);
        Assert.NotNull(status.Folders[0].LastSuccessfulSyncAt);
    }

    [Fact]
    public void ConfiguredLogDirectoryIsUsedAndReported()
    {
        using var ws = new Workspace();
        string custom = ws.Dir.Combine("custom-logs");
        Directory.CreateDirectory(custom);
        ws.WriteConfig(Workspace.Config([("cloud.example.com", "alice", [("C:/Users/alice/Nextcloud/", "/", false)])],
            extraGeneral: "logDir=" + custom.Replace('\\', '/') + "\n"));
        ws.Processes.AddClient();
        var now = ws.Clock.Now;
        Write(Path.Combine(custom, "20261110_1100_nextcloud.log.0"), [State(now.AddMinutes(-1), Alice, "Success")]);

        var status = ws.Monitor().Scan();

        Assert.Contains("configured in the client", status.Observation.Source);
        Assert.Equal(Health.Ok, status.Overall);
    }

    [Fact]
    public void TemporaryDiagnosticDirectoryIsUsedWhenTheClientLogsThere()
    {
        using var ws = new Workspace();
        Directory.CreateDirectory(ws.TempLogDir);
        ws.WriteConfig(Workspace.Config([("cloud.example.com", "alice", [("C:/Users/alice/Nextcloud/", "/", false)])],
            extraGeneral: "logToTemporaryLogDir=true\n"));
        ws.Processes.AddClient();
        var now = ws.Clock.Now;
        Write(Path.Combine(ws.TempLogDir, "20261110_1100_nextcloud.log.0"), [State(now.AddMinutes(-1), Alice, "Success")]);

        var status = ws.Monitor().Scan();

        Assert.Contains("temporary diagnostic logging", status.Observation.Source);
    }

    [Fact]
    public void ExplicitOverrideWinsOverEverythingElse()
    {
        using var ws = Scratch(out var now);
        string custom = ws.Dir.Combine("override-logs");
        Directory.CreateDirectory(custom);
        Write(Path.Combine(ws.LogDir, "20261110_1100_nextcloud.log.0"), [State(now.AddMinutes(-1), Alice, "Not yet started")]);
        Write(Path.Combine(custom, "20261110_1100_nextcloud.log.0"), [State(now.AddMinutes(-1), Alice, "Success")]);
        ws.Override = custom;

        var status = ws.Monitor().Scan();

        Assert.Contains("override", status.Observation.Source);
        Assert.Equal(SyncActivity.Idle, status.Folders[0].Activity);
    }

    // ---- failed scans -----------------------------------------------------------------------------------------

    [Fact]
    public void FailedScanIsPublishedInsteadOfKeepingAnApparentlyFreshResult()
    {
        using var ws = Scratch(out var now);
        Write(LogFile(ws), [State(now.AddMinutes(-1), Alice, "Success")]);
        var monitor = ws.Monitor();
        var good = monitor.Scan();
        Assert.Equal(Health.Ok, good.Overall);

        ws.Processes.ThrowOnFind = true;
        var failed = monitor.Scan();

        Assert.Equal(ObservationState.ScanFailed, failed.Observation.State);
        Assert.Equal(Health.Unknown, failed.Overall);
        Assert.Contains("Sync status unavailable", failed.Summary);
        Assert.Equal(1, failed.Folders.Count);
        Assert.Null(failed.Folders[0].LastSuccessfulSyncAt);
        Assert.Equal(Health.Unknown, failed.Folders[0].Health);
    }

    [Fact]
    public void UnreadableConfigurationIsAFailedScanNotAnEmptyOne()
    {
        using var ws = Scratch(out _);
        using var exclusive = new FileStream(ws.ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var status = ws.Monitor().Scan();

        Assert.Equal(ObservationState.ScanFailed, status.Observation.State);
        Assert.Contains("unavailable", status.Summary);
    }

    // ---- the dump ---------------------------------------------------------------------------------------------

    [Fact]
    public void DumpShowsSourceAgeActivityAndLastSuccess()
    {
        using var ws = Scratch(out var now);
        Write(LogFile(ws), [State(now.AddMinutes(-4), Alice, "Success"), Heartbeat(now.AddMinutes(-1))]);
        var status = ws.Monitor().Scan();

        string text = string.Join("\n", StatusDump.Build(status, now, [], autoRestartEnabled: false));

        Assert.Contains("source:", text);
        Assert.Contains("newest log record", text);
        Assert.Contains("sync states in the log: yes", text);
        Assert.Contains("activity:    Idle", text);
        Assert.Contains("last successful sync:", text);
        Assert.Contains("automatic restart: off", text);
        Assert.DoesNotContain("password", text.ToLowerInvariant());
    }

    [Fact]
    public void ASilentLogShowsWhatWasLastReportedAndTheRecoveryForecast()
    {
        using var ws = Scratch(out var now);
        // The client wrote its states after it started, then nothing for 20 minutes.
        Write(LogFile(ws), [State(now.AddMinutes(-25), Alice, "Not yet started")]);
        var status = ws.Monitor().Scan();

        Assert.Equal(ObservationState.NoRecentRecords, status.Observation.State);
        Assert.True(status.Observation.Silent, "stale but with sync states");
        Assert.True(status.Observation.UsableForRecovery);
        Assert.Contains("No log activity since", status.Summary);
        Assert.Contains("waiting to start syncing", status.Summary);

        status.RecoveryNote = RecoveryCoordinator.DescribeForecast(TimeSpan.FromMinutes(6));
        Assert.Contains("attempting automatic recovery in about 6 minutes", status.Summary);
    }

    [Fact]
    public void ALogWithoutAnySyncStatesIsNotSilentEvidence()
    {
        using var ws = Scratch(out var now);
        Write(LogFile(ws), [Heartbeat(now.AddMinutes(-25))]);
        var status = ws.Monitor().Scan();

        Assert.False(status.Observation.Silent);
        Assert.False(status.Observation.UsableForRecovery);
    }

    [Fact]
    public void SyncStatesEarlyInAHugeBusyLogAreStillFound()
    {
        using var ws = Scratch(out var now);
        string file = LogFile(ws);
        var lines = new List<string> { State(now.AddMinutes(-20), Alice, "Not yet started") };
        string filler = new('x', 400);
        for (int i = 0; i < 12000; i++)                      // about 5 MB of unrelated records after the state
            lines.Add(Line(now.AddMinutes(-19).AddMilliseconds(i), filler));
        Write(file, lines);
        File.SetLastWriteTime(file, now);

        var status = ws.Monitor().Scan();

        Assert.Equal(SyncActivity.Waiting, status.Folders[0].Activity);
        Assert.True(status.Observation.ShowsSyncStates);
    }

    [Fact]
    public void AGrowingLogIsReadIncrementallyAndAHalfWrittenLineWaits()
    {
        using var ws = Scratch(out var now);
        string file = LogFile(ws);
        Write(file, [State(now.AddMinutes(-3), Alice, "Not yet started"), Heartbeat(now.AddMinutes(-2))]);
        File.SetLastWriteTime(file, now);
        var monitor = ws.Monitor();
        Assert.Equal(SyncActivity.Waiting, monitor.Scan().Folders[0].Activity);

        // The client appends a complete line and then starts another one.
        string running = State(now.AddMinutes(-1), Alice, "Sync running");
        File.AppendAllText(file, running + "\n" + State(now, Alice, "Success").Substring(0, 40));
        File.SetLastWriteTime(file, now);
        Assert.Equal(SyncActivity.Syncing, monitor.Scan().Folders[0].Activity);

        // The rest of the half-written line arrives.
        File.AppendAllText(file, State(now, Alice, "Success").Substring(40) + "\n");
        File.SetLastWriteTime(file, now);
        var done = monitor.Scan();
        Assert.Equal(SyncActivity.Idle, done.Folders[0].Activity);
        Assert.NotNull(done.Folders[0].LastSuccessfulSyncAt);
    }
}
