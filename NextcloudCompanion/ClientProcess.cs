using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NextcloudCompanion;

/// <summary>Source of time. Wall-clock time is for display only; durations use the monotonic value.</summary>
public interface IClock
{
    DateTime Now { get; }
    /// <summary>Milliseconds from a monotonic source (not affected by clock changes).</summary>
    long MonotonicMilliseconds { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    private static readonly Stopwatch Mono = Stopwatch.StartNew();
    public DateTime Now => DateTime.Now;
    public long MonotonicMilliseconds => Mono.ElapsedMilliseconds;
}

/// <summary>
/// The exact client process: PID plus start time (a PID alone can be reused), the executable it runs from, and the
/// Windows session it belongs to.
/// </summary>
public sealed record ProcessIdentity(int Id, DateTime StartTime, string? ExePath, int SessionId)
{
    public bool HasKnownExecutable => !string.IsNullOrWhiteSpace(ExePath);
}

/// <summary>Everything Companion does to Nextcloud processes, behind one interface so it can be faked in tests.</summary>
public interface IProcessController
{
    /// <summary>All Nextcloud client processes of the current user's Windows session.</summary>
    IReadOnlyList<ProcessIdentity> FindClients();

    /// <summary>True if this very process (same PID and start time) is still running.</summary>
    bool IsSame(ProcessIdentity process);

    /// <summary>Command line of the process, or null if it cannot be read.</summary>
    string? GetCommandLine(ProcessIdentity process);

    /// <summary>Starts the executable with <c>--quit</c> (plus context arguments), which asks the running client to exit.</summary>
    Task<bool> RequestQuitAsync(string exePath, IReadOnlyList<string> contextArguments, CancellationToken cancellationToken);

    /// <summary>Waits until the process has exited; false on timeout.</summary>
    Task<bool> WaitForExitAsync(ProcessIdentity process, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Starts the client. Arguments are passed as a list, never composed into a command line string.</summary>
    void Launch(string exePath, IReadOnlyList<string> arguments);

    /// <summary>Ends the process abruptly. Only for an explicit user decision.</summary>
    void ForceKill(ProcessIdentity process);
}

public sealed class WindowsProcessController : IProcessController
{
    private const string ProcessName = "nextcloud";

    public IReadOnlyList<ProcessIdentity> FindClients()
    {
        var result = new List<ProcessIdentity>();
        int mySession;
        using (var me = Process.GetCurrentProcess())
            mySession = me.SessionId;

        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != mySession)
                        continue;
                    DateTime start = SafeStartTime(process);
                    string? path = null;
                    try { path = process.MainModule?.FileName; }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
                    result.Add(new ProcessIdentity(process.Id, start, path, process.SessionId));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // exited while being inspected
                }
            }
        }
        return result;
    }

    public bool IsSame(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Id);
            if (process.HasExited)
                return false;
            return Math.Abs((SafeStartTime(process) - identity.StartTime).TotalSeconds) < 2
                   && process.ProcessName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    public string? GetCommandLine(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.Id);
            return ReadCommandLine(process.Handle);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    public async Task<bool> RequestQuitAsync(string exePath, IReadOnlyList<string> contextArguments, CancellationToken cancellationToken)
    {
        var info = NewStartInfo(exePath, ["--quit", .. contextArguments]);
        ErrorLog.Recovery($"Asking Nextcloud to quit: \"{exePath}\" {string.Join(" ", info.ArgumentList)}");
        using var process = Process.Start(info);
        if (process is null)
            return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            ErrorLog.Recovery($"Quit request finished with exit code {process.ExitCode}.");
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<bool> WaitForExitAsync(ProcessIdentity identity, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (!IsSame(identity))
                return true;
            await Task.Delay(500, cancellationToken);
        }
        return !IsSame(identity);
    }

    public void Launch(string exePath, IReadOnlyList<string> arguments)
    {
        ErrorLog.Recovery($"Launching: \"{exePath}\" {string.Join(" ", arguments.Select(x => x.Contains(' ') ? $"\"{x}\"" : x))}");
        // Started the way the shell starts a program (as "Open Nextcloud" and the Start menu do): a restart that
        // launched it as a bare child process was seen to leave no client running.
        var info = NewStartInfo(exePath, arguments);
        info.UseShellExecute = true;
        info.CreateNoWindow = false;
        var process = Process.Start(info);
        if (process is null)
        {
            ErrorLog.Recovery("Launch: the system did not return a process.");
            return;
        }

        // Record what became of the new process, so a client that dies right away leaves a trace (exit code, lifetime).
        var started = DateTime.Now;
        int id = process.Id;
        ErrorLog.Recovery($"Launched: process {id}.");
        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                try
                {
                    ErrorLog.Recovery($"Launched process {id} exited with code {process.ExitCode} after {(DateTime.Now - started).TotalSeconds:0.#} s.");
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    ErrorLog.Recovery($"Launched process {id} exited (exit code not readable).");
                }
                finally
                {
                    process.Dispose();
                }
            };
            if (process.HasExited)       // it may have ended before the handler was attached
                return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            ErrorLog.Recovery("Launched process could not be watched: " + ex.Message);
        }
    }

    public void ForceKill(ProcessIdentity identity)
    {
        if (!IsSame(identity))
            return;
        using var process = Process.GetProcessById(identity.Id);
        process.Kill();
    }

    private static ProcessStartInfo NewStartInfo(string exePath, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static DateTime SafeStartTime(Process process)
    {
        try { return process.StartTime; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return DateTime.MinValue; }
    }

    // ---- reading another process's command line (same user, same bitness) -------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, int size, out IntPtr read);

    private static string? ReadCommandLine(IntPtr handle)
    {
        if (!Environment.Is64BitProcess)
            return null;
        var info = new ProcessBasicInformation();
        if (NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0
            || info.PebBaseAddress == IntPtr.Zero)
            return null;

        // PEB.ProcessParameters (x64 offset 0x20) -> RTL_USER_PROCESS_PARAMETERS.CommandLine (x64 offset 0x70).
        var pointer = new byte[IntPtr.Size];
        if (!ReadProcessMemory(handle, info.PebBaseAddress + 0x20, pointer, pointer.Length, out _))
            return null;
        var parameters = (IntPtr)BitConverter.ToInt64(pointer, 0);
        var unicodeString = new byte[16];     // USHORT Length, USHORT MaximumLength, 4 bytes padding, PWSTR Buffer
        if (parameters == IntPtr.Zero || !ReadProcessMemory(handle, parameters + 0x70, unicodeString, unicodeString.Length, out _))
            return null;
        int length = BitConverter.ToUInt16(unicodeString, 0);
        var buffer = (IntPtr)BitConverter.ToInt64(unicodeString, 8);
        if (length == 0 || buffer == IntPtr.Zero)
            return null;
        var text = new byte[length];
        if (!ReadProcessMemory(handle, buffer, text, length, out _))
            return null;
        return System.Text.Encoding.Unicode.GetString(text);
    }
}
