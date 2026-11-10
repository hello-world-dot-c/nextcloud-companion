using System.Text;

namespace NextcloudCompanion;

/// <summary>Anything that can produce a status snapshot (the status monitor; a fake in tests).</summary>
public interface IStatusSource
{
    ClientStatus Scan();
}

public enum RecoveryResultKind
{
    /// <summary>Restarted, and every relevant folder showed sync activity or a successful run afterwards.</summary>
    Resumed,
    /// <summary>Restarted, but resumed syncing could not be confirmed.</summary>
    NotVerified,
    /// <summary>Nothing was done: the target was not exactly one identifiable client.</summary>
    Refused,
    /// <summary>Another recovery is already running.</summary>
    Busy,
    /// <summary>The client did not exit gracefully; it was left running and no second client was started.</summary>
    QuitFailed,
    LaunchFailed,
    Cancelled
}

public sealed record RecoveryResult(RecoveryResultKind Kind, string Message)
{
    public bool Succeeded => Kind == RecoveryResultKind.Resumed;
}

public sealed class RecoveryOptions
{
    /// <summary>How long to wait for the original process to exit after <c>--quit</c>.</summary>
    public TimeSpan QuitWait { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>How long after the relaunch to look for evidence that syncing resumed.</summary>
    public TimeSpan VerifyWait { get; init; } = TimeSpan.FromMinutes(3);
    /// <summary>How long after a launch a client process must appear before the start is tried a second way.</summary>
    public TimeSpan LaunchWait { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Restarts the Nextcloud desktop client in a controlled way and checks the outcome. Process handling is separate
/// from the UI and the log parser. Only one recovery runs at a time; a second request is refused as busy.
/// The sequence: identify exactly one client, revalidate it, ask it to exit (<c>--quit</c>), wait for that very process
/// to exit, only then start the same installation with <c>--background</c>, and look for sync evidence from the new
/// instance. A new process id alone is never reported as recovery.
/// </summary>
public sealed class ClientRecoveryService(IProcessController processes, IStatusSource status, IClock clock, RecoveryOptions? options = null)
{
    private static readonly string[] ValueOptions = ["--confdir", "--logdir", "--logfile", "--logexpire"];
    private static readonly string[] FlagOptions = ["--logflush", "--logdebug"];

    private readonly RecoveryOptions _options = options ?? new RecoveryOptions();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Busy => _gate.CurrentCount == 0;

    /// <summary>The arguments that define the configuration context of a client and must survive a restart.</summary>
    public static IReadOnlyList<string> ContextArguments(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
            return result;
        var tokens = CommandLine.Split(commandLine);
        for (int i = 1; i < tokens.Count; i++)           // token 0 is the executable
        {
            string token = tokens[i];
            string name = token.Contains('=') ? token[..token.IndexOf('=')] : token;
            if (Array.Exists(ValueOptions, o => o.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                if (token.Contains('='))
                    result.Add(token);
                else if (i + 1 < tokens.Count)
                {
                    result.Add(token);
                    result.Add(tokens[++i]);
                }
            }
            else if (Array.Exists(FlagOptions, o => o.Equals(token, StringComparison.OrdinalIgnoreCase)))
                result.Add(token);
        }
        return result;
    }

    /// <summary>Graceful restart. Fails (and changes nothing) if the target is ambiguous or does not exit.</summary>
    public async Task<RecoveryResult> RestartAsync(bool automatic, IProgress<RecoveryStatus>? progress, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, CancellationToken.None))
            return new RecoveryResult(RecoveryResultKind.Busy, "A restart is already in progress.");
        try
        {
            return await RunAsync(automatic, forceTarget: null, progress, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Ends the given process abruptly, then restarts like <see cref="RestartAsync"/>. Only for an explicit user choice
    /// after a graceful exit failed; never used for automatic recovery.
    /// </summary>
    public async Task<RecoveryResult> ForceRestartAsync(ProcessIdentity target, IProgress<RecoveryStatus>? progress, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, CancellationToken.None))
            return new RecoveryResult(RecoveryResultKind.Busy, "A restart is already in progress.");
        try
        {
            return await RunAsync(automatic: false, forceTarget: target, progress, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RecoveryResult> RunAsync(bool automatic, ProcessIdentity? forceTarget, IProgress<RecoveryStatus>? progress, CancellationToken ct)
    {
        void Report(RecoveryPhase phase, string message) =>
            progress?.Report(new RecoveryStatus { Phase = phase, Message = message, Automatic = automatic, Since = clock.Now });

        // 1. Identify exactly one client and revalidate it before acting.
        var clients = processes.FindClients();
        if (clients.Count == 0)
            return Refuse("Nextcloud is not running, so there is nothing to restart.");
        if (clients.Count > 1)
            return Refuse("More than one Nextcloud process is running for your Windows account. Companion will not guess which one to restart.");
        var target = clients[0];
        if (forceTarget is not null && (forceTarget.Id != target.Id || forceTarget.StartTime != target.StartTime))
            return Refuse("The Nextcloud process changed in the meantime. Nothing was done.");
        if (!target.HasKnownExecutable)
            return Refuse("Companion cannot tell which Nextcloud installation is running (its executable path is not readable), so it cannot restart it safely.");
        string exe = target.ExePath!;

        // 2. Keep the configuration context: a client started with another config directory must come back with it.
        string? commandLine = processes.GetCommandLine(target);
        if (commandLine is null)
            return Refuse("Companion cannot read how Nextcloud was started, so it cannot restart it with the same settings. Restart it by hand.");
        var context = ContextArguments(commandLine);

        Report(RecoveryPhase.Restarting, "Restarting Nextcloud…");
        if (!processes.IsSame(target))
            return Refuse("The Nextcloud process changed in the meantime. Nothing was done.");

        // 3. Ask for a graceful exit (or, on explicit request only, end the process), then wait for that very process.
        try
        {
            if (forceTarget is not null)
                processes.ForceKill(target);
            else if (!await processes.RequestQuitAsync(exe, context, ct))
                return Failed(RecoveryResultKind.QuitFailed, "Companion could not ask Nextcloud to quit. It was left running.");

            if (!await processes.WaitForExitAsync(target, _options.QuitWait, ct))
                return Failed(RecoveryResultKind.QuitFailed,
                    $"Nextcloud did not close within {(int)_options.QuitWait.TotalSeconds} seconds (it may be busy or waiting for an answer in a dialog). "
                    + "It was left running and not restarted.");
        }
        catch (OperationCanceledException)
        {
            return Failed(RecoveryResultKind.Cancelled, "The restart was cancelled.");
        }

        // 4. Only after the exit: start the same installation again, unless something else already did.
        DateTime launchedAt = clock.Now;
        if (processes.FindClients().Count == 0)
        {
            try
            {
                processes.Launch(exe, ["--background", .. context]);
                // Nothing appeared: nothing is running, so starting once more cannot create a duplicate. Plain start,
                // without --background, as a user would do it.
                if (!await ClientAppearsAsync(ct))
                    processes.Launch(exe, context);
            }
            catch (OperationCanceledException)
            {
                return Failed(RecoveryResultKind.Cancelled, "The restart was cancelled.");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return Failed(RecoveryResultKind.LaunchFailed, "Nextcloud was closed but could not be started again: " + ex.Message);
            }
        }

        // 5. Look for evidence from the new instance.
        Report(RecoveryPhase.WaitingForEvidence, "Nextcloud restarted — waiting for sync evidence…");
        return await VerifyAsync(target, launchedAt, automatic, progress, ct);

        RecoveryResult Refuse(string message)
        {
            Report(RecoveryPhase.Failed, message);
            return new RecoveryResult(RecoveryResultKind.Refused, message);
        }

        RecoveryResult Failed(RecoveryResultKind kind, string message)
        {
            Report(RecoveryPhase.Failed, message);
            return new RecoveryResult(kind, message);
        }
    }

    private async Task<bool> ClientAppearsAsync(CancellationToken ct)
    {
        var deadline = clock.MonotonicMilliseconds + (long)_options.LaunchWait.TotalMilliseconds;
        while (true)
        {
            if (processes.FindClients().Count > 0)
                return true;
            if (clock.MonotonicMilliseconds >= deadline)
                return false;
            await Task.Delay(_options.PollInterval, ct);
        }
    }

    private async Task<RecoveryResult> VerifyAsync(ProcessIdentity original, DateTime launchedAt, bool automatic, IProgress<RecoveryStatus>? progress, CancellationToken ct)
    {
        var deadline = clock.MonotonicMilliseconds + (long)_options.VerifyWait.TotalMilliseconds;
        var tolerance = TimeSpan.FromSeconds(2);        // log timestamps have one-second resolution
        ClientStatus latest;
        while (true)
        {
            latest = status.Scan();
            bool newProcess = latest.Process is { } p && (p.Id != original.Id || p.StartTime != original.StartTime);
            if (newProcess)
            {
                var relevant = latest.EnabledFolders.ToList();
                if (relevant.Count > 0 && relevant.All(f => HasEvidenceSince(f, launchedAt - tolerance)))
                {
                    string done = "Sync resumed after the restart.";
                    progress?.Report(new RecoveryStatus { Phase = RecoveryPhase.Resumed, Message = done, Automatic = automatic, Since = clock.Now });
                    return new RecoveryResult(RecoveryResultKind.Resumed, done);
                }
            }

            if (clock.MonotonicMilliseconds >= deadline)
                break;
            try
            {
                await Task.Delay(_options.PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return new RecoveryResult(RecoveryResultKind.Cancelled, "The restart was cancelled while waiting for sync evidence.");
            }
        }

        string message;
        var enabled = latest.EnabledFolders.ToList();
        if (latest.Process is null)
            message = "Nextcloud was closed and started again, but it is not running now. Check Nextcloud.";
        else if (enabled.Count == 0)
            message = "Nextcloud was restarted. There is no enabled folder to check, so a resumed sync could not be verified.";
        else
        {
            var missing = enabled.Where(f => !HasEvidenceSince(f, launchedAt - tolerance)).Select(f => f.DisplayName).ToList();
            message = "Nextcloud was restarted, but Companion could not confirm that syncing resumed (no sync activity seen yet for "
                      + string.Join(", ", missing) + "). Check Nextcloud.";
        }
        progress?.Report(new RecoveryStatus { Phase = RecoveryPhase.Failed, Message = message, Automatic = automatic, Since = clock.Now });
        return new RecoveryResult(RecoveryResultKind.NotVerified, message);
    }

    /// <summary>A successful run (also one that found nothing to do), or a run in progress, after the relaunch.</summary>
    private static bool HasEvidenceSince(FolderStatus folder, DateTime since)
    {
        if (folder.Health is Health.Error or Health.Disconnected)
            return false;
        if (folder.LastSuccessfulSyncAt is DateTime ok && ok >= since)
            return true;
        return folder.ActivitySince is DateTime at && at >= since
               && folder.Activity is SyncActivity.Preparing or SyncActivity.Syncing or SyncActivity.Idle;
    }
}

/// <summary>Splits a Windows command line the way the C runtime does (quotes and backslashes before quotes).</summary>
public static class CommandLine
{
    public static IReadOnlyList<string> Split(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        bool any = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\')
            {
                int slashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    slashes++;
                    i++;
                }
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                        current.Append('"');
                    else
                        inQuotes = !inQuotes;
                    any = true;
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                    any = true;
                }
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any)
            args.Add(current.ToString());
        return args;
    }
}
