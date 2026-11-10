using System.Globalization;
using System.IO.Compression;

namespace NextcloudCompanion;

public readonly record struct LogRecord(DateTime Time, string Line);

public enum LogSourceKind
{
    Override,
    Configured,
    TemporaryDiagnostic,
    Default
}

/// <summary>The log directory that was chosen for a scan, the files read from it, and why it may be unusable.</summary>
public sealed class LogLocation
{
    public string? Directory { get; init; }
    public LogSourceKind Kind { get; init; }
    /// <summary>Recent log files, oldest first.</summary>
    public IReadOnlyList<FileInfo> Files { get; init; } = [];
    /// <summary>Ok when files were found; otherwise why not.</summary>
    public ObservationState State { get; init; } = ObservationState.Ok;
    public string Reason { get; init; } = "";
    /// <summary>Newest file of the source (log), for display.</summary>
    public string? NewestFile => Files.Count > 0 ? Files[^1].FullName : null;

    public string Describe() => Directory is null
        ? "no log directory found"
        : $"{Directory} ({Kind switch
        {
            LogSourceKind.Override => "override",
            LogSourceKind.Configured => "configured in the client",
            LogSourceKind.TemporaryDiagnostic => "temporary diagnostic logging",
            _ => "default"
        }})";
}

/// <summary>
/// Finds the client's diagnostic logs: an explicit override first, then the directory configured in nextcloud.cfg,
/// then the temporary diagnostic directory, then the default <c>%AppData%\Nextcloud\logs</c>. The first one that has
/// recent logs wins; if none has, the first one that exists is reported with the reason.
/// </summary>
public sealed class LogLocator(string defaultDir, string temporaryDir, Func<DateTime> now)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(12);
    public const int MaxFiles = 40;

    public LogLocation Locate(ConfigData? config, string? overrideDir)
    {
        var candidates = new List<(string Dir, LogSourceKind Kind)>();
        void Add(string? dir, LogSourceKind kind)
        {
            if (!string.IsNullOrWhiteSpace(dir) && !candidates.Any(c => string.Equals(c.Dir, dir, StringComparison.OrdinalIgnoreCase)))
                candidates.Add((dir, kind));
        }
        Add(overrideDir, LogSourceKind.Override);
        Add(config?.LogDir, LogSourceKind.Configured);
        if (config?.LogToTemporaryDir == true)
            Add(temporaryDir, LogSourceKind.TemporaryDiagnostic);
        Add(defaultDir, LogSourceKind.Default);
        if (config?.LogToTemporaryDir != true)
            Add(temporaryDir, LogSourceKind.TemporaryDiagnostic);

        LogLocation? firstExisting = null;
        LogLocation? firstUnreadable = null;
        foreach (var (dir, kind) in candidates)
        {
            var found = Inspect(dir, kind);
            if (found.State == ObservationState.Ok)
                return found;
            if (found.State == ObservationState.LogsUnreadable)
                firstUnreadable ??= found;
            else if (found.State == ObservationState.NoRecentRecords)
                firstExisting ??= found;
        }

        return firstExisting
            ?? firstUnreadable
            ?? new LogLocation
            {
                Directory = null,
                Kind = LogSourceKind.Default,
                State = ObservationState.LogsMissing,
                Reason = "no log directory exists (looked in " + string.Join("; ", candidates.Select(c => c.Dir)) + ")"
            };
    }

    private LogLocation Inspect(string dir, LogSourceKind kind)
    {
        try
        {
            if (!Directory.Exists(dir))
                return new LogLocation { Directory = dir, Kind = kind, State = ObservationState.LogsMissing, Reason = "directory does not exist" };

            var cutoff = now() - Window;
            var all = new DirectoryInfo(dir).GetFiles("*nextcloud.log*")
                .Where(f => !f.Name.Contains("permanent_delete", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var recent = all
                .Where(f => f.LastWriteTime >= cutoff)
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();
            if (recent.Count == 0)
                return new LogLocation
                {
                    Directory = dir,
                    Kind = kind,
                    State = ObservationState.NoRecentRecords,
                    Reason = all.Count == 0 ? "no log files in the directory" : "no log file was written in the last 12 hours"
                };

            return new LogLocation { Directory = dir, Kind = kind, Files = recent.TakeLast(MaxFiles).ToList() };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return new LogLocation { Directory = dir, Kind = kind, State = ObservationState.LogsUnreadable, Reason = ex.Message };
        }
    }
}

public sealed class LogReadResult
{
    /// <summary>Records relevant to sync status, oldest first.</summary>
    public List<LogRecord> Records { get; } = [];
    /// <summary>Newest timestamp of any record, relevant or not (proof that the client is writing logs).</summary>
    public DateTime? NewestRecord { get; set; }
    public int UnreadableFiles { get; set; }
    public int TotalRecords { get; set; }
}

/// <summary>
/// Reads log files (plain, rotated and gzip-compressed) and keeps only the records that matter for sync status.
/// Files that cannot change any more (compressed, or not modified for a while) are parsed once and cached.
/// </summary>
public sealed class LogReader
{
    // Cheap pre-filter: a record is kept only if it contains one of these.
    private static readonly string[] Relevant =
    [
        "Sync started for folder", "Sync finished for folder", "Could not complete propagation",
        "Error successfully added", "SyncEngine finished", "via ETag check", "FINISHED WITH STATUS",
        "CheckServerJob created", "status.php replied", "disconnected or paused", "connected, scheduling",
        "Sync state changed for folder"
    ];

    /// <summary>A plain log is read from its start (the sync states are written long before the end of a busy log).</summary>
    private const long MaxInitialBytes = 96L * 1024 * 1024;

    /// <param name="Offset">For plain files: how far the file has been consumed (complete lines only), to continue from there.</param>
    private sealed record Cached(long Length, long WriteTicks, List<LogRecord> Records, DateTime? Newest, int Total, bool Damaged = false, long Offset = 0);

    private readonly Dictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _now;

    public LogReader(Func<DateTime> now) => _now = now;

    public LogReadResult Read(IEnumerable<FileInfo> files)
    {
        var result = new LogReadResult();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            seen.Add(file.FullName);
            try
            {
                var entry = ReadFile(file);
                if (entry.Damaged)
                    result.UnreadableFiles++;       // what could be read before the damage is still used
                result.Records.AddRange(entry.Records);
                result.TotalRecords += entry.Total;
                if (entry.Newest is DateTime n && (result.NewestRecord is null || n > result.NewestRecord))
                    result.NewestRecord = n;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                result.UnreadableFiles++;
            }
        }
        foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList())
            _cache.Remove(stale);
        return result;
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            BytesRead += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private Cached ReadFile(FileInfo file)
    {
        file.Refresh();
        if (!file.Exists)
            return new Cached(0, 0, [], null, 0);

        bool gzipped = file.Name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase);
        bool settled = gzipped || _now() - file.LastWriteTime > TimeSpan.FromMinutes(2);
        if (settled && _cache.TryGetValue(file.FullName, out var cached)
            && cached.Length == file.Length && cached.WriteTicks == file.LastWriteTimeUtc.Ticks)
            return cached;

        _cache.TryGetValue(file.FullName, out var previous);
        if (!gzipped)
        {
            try
            {
                var plain = ReadPlain(file, previous, settled);
                _cache[file.FullName] = plain;
                return plain;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new Cached(0, 0, [], null, 0);
            }
        }

        var records = new List<LogRecord>();
        DateTime? newest = null;
        int total = 0;
        bool damaged = false;
        try
        {
            using var fileStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Stream stream = fileStream;
            CountingStream? counted = null;
            if (gzipped)
                stream = counted = new CountingStream(new GZipStream(fileStream, CompressionMode.Decompress, leaveOpen: true));

            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
            string? line;
            try
            {
                while ((line = reader.ReadLine()) is not null)
                {
                    if (!TryParseTime(line, out var ts))
                        continue;
                    total++;
                    if (newest is null || ts > newest)
                        newest = ts;
                    foreach (var key in Relevant)
                    {
                        if (line.Contains(key, StringComparison.Ordinal))
                        {
                            records.Add(new LogRecord(ts, line));
                            break;
                        }
                    }
                }
            }
            catch (InvalidDataException)
            {
                // A damaged archive: keep what was read before the damage.
                damaged = true;
            }

            // .NET ends a truncated archive silently, so compare with the size recorded in the gzip trailer
            // (the uncompressed length modulo 2^32, in the last four bytes).
            if (gzipped && !damaged && counted is not null && fileStream.Length >= 18)
            {
                var trailer = new byte[4];
                fileStream.Seek(-4, SeekOrigin.End);
                if (fileStream.Read(trailer, 0, 4) == 4 && BitConverter.ToUInt32(trailer, 0) != (uint)(counted.BytesRead & 0xFFFFFFFF))
                    damaged = true;
            }
        }
        catch (FileNotFoundException)
        {
            return new Cached(0, 0, [], null, 0);
        }
        catch (DirectoryNotFoundException)
        {
            return new Cached(0, 0, [], null, 0);
        }

        var entry = new Cached(file.Length, file.LastWriteTimeUtc.Ticks, records, newest, total, damaged);
        _cache[file.FullName] = entry;
        return entry;
    }

    /// <summary>
    /// Reads a plain log. A file that is still being written is read incrementally: each scan continues where the
    /// last one stopped, and only complete lines are consumed (the last, half-written line waits for its newline).
    /// </summary>
    private Cached ReadPlain(FileInfo file, Cached? previous, bool settled)
    {
        long length = file.Length;
        long writeTicks = file.LastWriteTimeUtc.Ticks;
        var records = new List<LogRecord>();
        DateTime? newest = null;
        int total = 0;
        long start = 0;
        if (previous is { Offset: > 0 } && length >= previous.Offset)
        {
            start = previous.Offset;
            records.AddRange(previous.Records);
            newest = previous.Newest;
            total = previous.Total;
        }
        bool skipPartialLine = false;
        if (start == 0 && length > MaxInitialBytes)
        {
            start = length - MaxInitialBytes;
            skipPartialLine = true;
        }

        void Process(byte[] bytes, int count, bool firstLineOfFile)
        {
            string line = System.Text.Encoding.UTF8.GetString(bytes, 0, count).TrimEnd('\r');
            if (firstLineOfFile)
                line = line.TrimStart('\uFEFF');
            if (!TryParseTime(line, out var ts))
                return;
            total++;
            if (newest is null || ts > newest)
                newest = ts;
            foreach (var key in Relevant)
            {
                if (line.Contains(key, StringComparison.Ordinal))
                {
                    records.Add(new LogRecord(ts, line));
                    break;
                }
            }
        }

        long consumed = start;
        using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[256 * 1024];
            var pending = new MemoryStream();
            int read;
            while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                int from = 0;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                        continue;
                    pending.Write(buffer, from, i - from);
                    from = i + 1;
                    if (skipPartialLine)
                        skipPartialLine = false;
                    else
                        Process(pending.GetBuffer(), (int)pending.Length, firstLineOfFile: consumed == 0);
                    consumed += pending.Length + 1;
                    pending.SetLength(0);
                }
                pending.Write(buffer, from, read - from);
            }
            if (settled && pending.Length > 0 && !skipPartialLine)
            {
                Process(pending.GetBuffer(), (int)pending.Length, firstLineOfFile: consumed == 0);
                consumed += pending.Length;
            }
        }

        return new Cached(length, writeTicks, records, newest, total, Damaged: false, Offset: consumed);
    }

    /// <summary>"2026-11-10 10:31:57:592 ..." and "2026-11-10 10:31:57 ..." (the client adds milliseconds after a colon).</summary>
    internal static bool TryParseTime(string line, out DateTime time)
    {
        time = default;
        if (line.Length < 19 || line[4] != '-' || line[7] != '-' || line[10] != ' ' || line[13] != ':' || line[16] != ':')
            return false;
        return DateTime.TryParseExact(line.AsSpan(0, 19), "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }
}
