namespace NextcloudCompanion;

/// <summary>Where the monitor looks and how it tells time and finds processes. Replaceable for tests.</summary>
public sealed class StatusMonitorOptions
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nextcloud");

    public string ConfigPath { get; init; } = Path.Combine(ConfigDir, "nextcloud.cfg");
    public string DefaultLogDir { get; init; } = Path.Combine(ConfigDir, "logs");
    /// <summary>The temporary diagnostic directory of the client (<c>%TEMP%\Nextcloud-logdir</c>).</summary>
    public string TemporaryLogDir { get; init; } = Path.Combine(Path.GetTempPath(), "Nextcloud-logdir");
    /// <summary>An explicit log directory chosen by the user, or null.</summary>
    public Func<string?> LogDirOverride { get; init; } = () => Settings.LogDirOverride;
    public IProcessController Processes { get; init; } = new WindowsProcessController();
    public IClock Clock { get; init; } = SystemClock.Instance;
    /// <summary>The logs are considered stale when their newest record is older than this.</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Reads nextcloud.cfg plus recent desktop-client logs and produces a per-folder snapshot. It reports what the
/// evidence shows, and says so when there is none: process, connection, activity and sync results are kept apart,
/// and a failed scan is published as such instead of leaving an earlier healthy result on screen.
/// </summary>
public sealed class StatusMonitor : IStatusSource
{
    private readonly StatusMonitorOptions _options;
    private readonly LogLocator _locator;
    private readonly LogReader _reader;

    public StatusMonitor() : this(new StatusMonitorOptions()) { }

    public StatusMonitor(StatusMonitorOptions options)
    {
        _options = options;
        _locator = new LogLocator(options.DefaultLogDir, options.TemporaryLogDir, () => options.Clock.Now);
        _reader = new LogReader(() => options.Clock.Now);
    }

    public ClientStatus Snapshot { get; private set; } = new() { ProcessRunning = false };

    /// <summary>Scans once. Never throws: a failure becomes a snapshot that says the status is unavailable.</summary>
    public ClientStatus Scan()
    {
        try
        {
            Snapshot = ScanCore();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Scan failed", ex);
            Snapshot = Failure(ex.Message);
        }
        return Snapshot;
    }

    /// <summary>A snapshot that keeps the configuration of the last one but none of its observed state.</summary>
    public ClientStatus Failure(string reason)
    {
        var previous = Snapshot;
        return new ClientStatus
        {
            ProcessRunning = previous.ProcessRunning,
            Process = previous.Process,
            AmbiguousProcess = previous.AmbiguousProcess,
            Folders = previous.Folders.Select(f => f.CloneConfiguration()).ToList(),
            CheckedAt = _options.Clock.Now,
            Observation = new Observation { State = ObservationState.ScanFailed, Reason = reason }
        };
    }

    private ClientStatus ScanCore()
    {
        var clients = _options.Processes.FindClients();
        bool running = clients.Count > 0;
        var identity = clients.Count == 1 ? clients[0] : null;

        ConfigData config = ReadConfig();
        var folders = config.Folders;

        var location = _locator.Locate(config, _options.LogDirOverride());
        LogReadResult read = new();
        LogAnalysis analysis = new();
        if (location.State == ObservationState.Ok)
        {
            read = _reader.Read(location.Files);
            analysis = LogAnalyzer.Apply(folders, read.Records);
        }

        foreach (var folder in folders)
        {
            if (folder.Paused)
            {
                folder.Health = Health.Paused;
                folder.Activity = SyncActivity.Paused;
            }
            else if (!running)
            {
                folder.Health = Health.Offline;
                folder.Activity = SyncActivity.Unknown;
            }
        }

        return new ClientStatus
        {
            ProcessRunning = running,
            Process = identity,
            AmbiguousProcess = clients.Count > 1,
            Folders = folders,
            CheckedAt = _options.Clock.Now,
            LogFile = location.NewestFile,
            Observation = Observe(location, read, analysis)
        };
    }

    private ConfigData ReadConfig()
    {
        if (!File.Exists(_options.ConfigPath))
            return new ConfigData();
        // An unreadable configuration is a failed scan (thrown), not an empty configuration.
        return ConfigReader.Parse(File.ReadAllText(_options.ConfigPath));
    }

    private Observation Observe(LogLocation location, LogReadResult read, LogAnalysis analysis)
    {
        string source = location.Describe();
        if (location.State != ObservationState.Ok)
            return new Observation { State = location.State, Source = source, Reason = location.Reason };

        DateTime now = _options.Clock.Now;
        ObservationState state;
        string reason = "";
        if (read.UnreadableFiles >= location.Files.Count)
        {
            state = ObservationState.LogsUnreadable;
            reason = "none of the recent log files could be read";
        }
        else if (read.TotalRecords == 0)
        {
            state = ObservationState.UnsupportedFormat;
            reason = "no line of the recent log files starts with a timestamp";
        }
        else if (read.NewestRecord is DateTime newest && now - newest > _options.StaleAfter)
        {
            state = ObservationState.NoRecentRecords;
            reason = $"the newest log record is from {FolderStatus.FormatTime(newest)}";
        }
        else if (analysis.RecognisedRecords == 0)
        {
            state = ObservationState.NoRecentRecords;
            reason = "the recent log has no sync records";
        }
        else
        {
            state = ObservationState.Ok;
        }

        return new Observation
        {
            State = state,
            Source = source,
            LastRecordAt = read.NewestRecord,
            ShowsSyncStates = analysis.SawSyncStates,
            Silent = state == ObservationState.NoRecentRecords && analysis.SawSyncStates
                     && read.NewestRecord is DateTime quiet && now - quiet > _options.StaleAfter,
            Reason = reason,
            UnreadableFiles = read.UnreadableFiles,
            UnattributedRecords = analysis.Unattributed
        };
    }
}
