using System.IO.Compression;
using System.Text;

namespace NextcloudCompanion.Tests;

/// <summary>A clock that only moves when the test moves it (optionally a little on every monotonic read).</summary>
public sealed class FakeClock : IClock
{
    private long _mono;

    public DateTime Now { get; set; } = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Local);

    /// <summary>Added to the monotonic reading each time it is read, so polling loops with deadlines terminate.</summary>
    public long AutoAdvanceMilliseconds { get; set; }

    public long MonotonicMilliseconds
    {
        get
        {
            _mono += AutoAdvanceMilliseconds;
            return _mono;
        }
    }

    /// <summary>Moves wall-clock and monotonic time together, like real time passing.</summary>
    public void Advance(TimeSpan span)
    {
        Now += span;
        _mono += (long)span.TotalMilliseconds;
    }

    /// <summary>Moves only the wall clock (the user or a time service changed the clock).</summary>
    public void JumpWallClock(TimeSpan span) => Now += span;
}

public sealed class FakeProcessController : IProcessController
{
    private int _nextId = 1000;
    private readonly FakeClock _clock;

    public FakeProcessController(FakeClock clock) => _clock = clock;

    public List<ProcessIdentity> Clients { get; } = [];
    public string? CommandLineText { get; set; } = "\"C:\\Program Files\\Nextcloud\\nextcloud.exe\"";
    public bool QuitSucceeds { get; set; } = true;
    /// <summary>When false, the client ignores --quit and keeps running.</summary>
    public bool ExitsAfterQuit { get; set; } = true;
    public bool LaunchStartsClient { get; set; } = true;
    public List<string> Calls { get; } = [];
    public List<string[]> Launched { get; } = [];
    public List<string[]> QuitArguments { get; } = [];
    public Action? AfterLaunch { get; set; }

    public ProcessIdentity AddClient(string? exe = @"C:\Program Files\Nextcloud\nextcloud.exe")
    {
        var identity = new ProcessIdentity(_nextId++, _clock.Now.AddHours(-3), exe, 1);
        Clients.Add(identity);
        return identity;
    }

    /// <summary>Makes process enumeration fail, as when the system refuses access.</summary>
    public bool ThrowOnFind { get; set; }

    public IReadOnlyList<ProcessIdentity> FindClients()
        => ThrowOnFind ? throw new InvalidOperationException("process list unavailable") : Clients.ToList();

    public bool IsSame(ProcessIdentity process) => Clients.Contains(process);

    public string? GetCommandLine(ProcessIdentity process) => CommandLineText;

    public Task<bool> RequestQuitAsync(string exePath, IReadOnlyList<string> contextArguments, CancellationToken cancellationToken)
    {
        Calls.Add("quit");
        QuitArguments.Add(contextArguments.ToArray());
        if (QuitSucceeds && ExitsAfterQuit)
            Clients.Clear();
        return Task.FromResult(QuitSucceeds);
    }

    public Task<bool> WaitForExitAsync(ProcessIdentity process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Calls.Add("wait");
        return Task.FromResult(!Clients.Contains(process));
    }

    public void Launch(string exePath, IReadOnlyList<string> arguments)
    {
        Calls.Add("launch");
        Launched.Add(arguments.ToArray());
        if (LaunchStartsClient)
        {
            var identity = new ProcessIdentity(_nextId++, _clock.Now, exePath, 1);
            Clients.Add(identity);
        }
        AfterLaunch?.Invoke();
    }

    public void ForceKill(ProcessIdentity process)
    {
        Calls.Add("kill");
        Clients.Remove(process);
    }
}

public sealed class FakeStatusSource(Func<ClientStatus> produce) : IStatusSource
{
    public int Scans { get; private set; }

    public ClientStatus Scan()
    {
        Scans++;
        return produce();
    }
}

public sealed class FakeStore : IRecoveryStore
{
    public List<DateTime> Items { get; } = [];
    public bool FailToSave { get; set; }

    public IReadOnlyList<DateTime> AutomaticRestartsUtc => Items.ToList();

    public bool Record(DateTime utc)
    {
        if (FailToSave)
            return false;
        Items.Add(utc);
        return true;
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ncc-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Text of sanitized client files (see the Fixtures folder).</summary>
public static class Fixture
{
    public static string Text(string name) =>
        File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>Writes log lines in the format of the Nextcloud 35.x client.</summary>
public static class LogLines
{
    public const string Cloud = "https://cloud.example.com";

    public static string Line(DateTime t, string message) =>
        $"{t:yyyy-MM-dd HH:mm:ss}:{t.Millisecond:000} [ info nextcloud.test C:\\build\\desktop\\src\\gui\\x.cpp:1 ]:\t{message}";

    public static string State(DateTime t, string remoteUrl, string state) =>
        Line(t, $"Sync state changed for folder  \"{remoteUrl}\" :  \"{state}\"");

    public static string Started(DateTime t, string localPath, string account, string remote) =>
        Line(t, $">========== Sync started for folder [{localPath}] of account [{account}] with remote [{remote}]");

    public static string Finished(DateTime t, string localPath, string account, string remote) =>
        Line(t, $"<========== Sync finished for folder [{localPath}] of account [{account}] with remote [{remote}]");

    public static string EngineOk(DateTime t) => Line(t, "SyncEngine finished without problem.");

    public static string EngineError(DateTime t) => Line(t, "SyncEngine finished with ERROR");

    public static string EtagOk(DateTime t, string remoteUrl) =>
        Line(t, $"Request Etag of QUrl(\"{remoteUrl}\") FINISHED WITH STATUS \"OK\"");

    public static string Heartbeat(DateTime t) => Line(t, "Etag poll timer timeout");

    public static string Remote(string host, string user, string target = "") =>
        $"https://{host}/remote.php/dav/files/{user}/{target}";

    public static void Write(string path, IEnumerable<string> lines) =>
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));

    public static void WriteGzip(string path, IEnumerable<string> lines)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        writer.Write(string.Join("\n", lines) + "\n");
    }
}

/// <summary>A scratch client installation: nextcloud.cfg, a log directory, a monitor wired to fakes.</summary>
public sealed class Workspace : IDisposable
{
    public TempDir Dir { get; } = new();
    public FakeClock Clock { get; } = new();
    public FakeProcessController Processes { get; }
    public string ConfigPath => Dir.Combine("nextcloud.cfg");
    public string LogDir => Dir.Combine("logs");
    public string TempLogDir => Dir.Combine("tmp-logdir");
    public string? Override { get; set; }

    public Workspace()
    {
        Processes = new FakeProcessController(Clock);
        Directory.CreateDirectory(LogDir);
    }

    public void WriteConfig(string text) => File.WriteAllText(ConfigPath, text);

    public StatusMonitor Monitor() => new(new StatusMonitorOptions
    {
        ConfigPath = ConfigPath,
        DefaultLogDir = LogDir,
        TemporaryLogDir = TempLogDir,
        LogDirOverride = () => Override,
        Processes = Processes,
        Clock = Clock
    });

    /// <summary>Configuration with one account and the given (local path, target path, paused) folders.</summary>
    public static string Config(string host, string user, params (string Local, string Target, bool Paused)[] folders)
        => Config([(host, user, folders)]);

    public static string Config(IEnumerable<(string Host, string User, (string Local, string Target, bool Paused)[] Folders)> accounts, string group = "Folders", string extraGeneral = "")
    {
        var sb = new StringBuilder();
        sb.AppendLine("[General]").AppendLine("clientVersion=35.0.3").Append(extraGeneral).AppendLine("[Accounts]");
        int a = 0;
        foreach (var (host, user, folders) in accounts)
        {
            sb.AppendLine($"{a}\\url=https://{host}").AppendLine($"{a}\\dav_user={user}");
            int f = 1;
            foreach (var (local, target, paused) in folders)
            {
                sb.AppendLine($"{a}\\{group}\\{f}\\localPath={local}");
                sb.AppendLine($"{a}\\{group}\\{f}\\targetPath={target}");
                sb.AppendLine($"{a}\\{group}\\{f}\\journalPath=.sync_{a}{f}.db");
                sb.AppendLine($"{a}\\{group}\\{f}\\paused={(paused ? "true" : "false")}");
                f++;
            }
            a++;
        }
        sb.AppendLine("[Nextcloud]").AppendLine("x=1");
        return sb.ToString();
    }

    public void Dispose() => Dir.Dispose();
}
