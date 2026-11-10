namespace NextcloudCompanion;

/// <summary>
/// The text written by <c>--dump</c>: what was observed, from which source and how old it is, and what each folder
/// is doing. Contains no credentials (app passwords are never read here).
/// </summary>
public static class StatusDump
{
    public static List<string> Build(ClientStatus status, DateTime now, IReadOnlyList<DateTime>? automaticRestartsUtc = null, bool autoRestartEnabled = false)
    {
        var lines = new List<string>
        {
            $"Summary: {status.Summary}",
            $"Overall: {status.Overall}",
            $"Checked: {status.CheckedAt:yyyy-MM-dd HH:mm:ss}",
            "",
            "Client process"
        };

        if (status.Process is { } p)
        {
            lines.Add($"  running:     yes (pid {p.Id}, started {p.StartTime:yyyy-MM-dd HH:mm:ss}, session {p.SessionId})");
            lines.Add($"  executable:  {p.ExePath ?? "not readable"}");
        }
        else
        {
            lines.Add(status.AmbiguousProcess
                ? "  running:     more than one Nextcloud process (recovery refuses to act)"
                : $"  running:     {(status.ProcessRunning ? "yes (not identifiable)" : "no")}");
        }

        var o = status.Observation;
        lines.Add("");
        lines.Add("Observation");
        lines.Add($"  state:       {o.State}{(string.IsNullOrWhiteSpace(o.Reason) ? "" : " — " + o.Reason)}");
        lines.Add($"  source:      {(o.Source.Length > 0 ? o.Source : "none")}");
        lines.Add(o.LastRecordAt is DateTime last
            ? $"  newest log record: {last:yyyy-MM-dd HH:mm:ss} ({Age(now - last)} ago)"
            : "  newest log record: none");
        lines.Add($"  sync states in the log: {(o.ShowsSyncStates ? "yes" : "no")}");
        lines.Add($"  unreadable log files: {o.UnreadableFiles}; records not attributable to one folder: {o.UnattributedRecords}");
        lines.Add($"  log file:    {status.LogFile ?? "none"}");

        lines.Add("");
        lines.Add("Recovery");
        lines.Add($"  restart state:    {status.Recovery.Phase}{(status.Recovery.Message.Length > 0 ? " — " + status.Recovery.Message : "")}");
        lines.Add($"  automatic restart: {(autoRestartEnabled ? "on" : "off")}");
        var history = automaticRestartsUtc ?? [];
        lines.Add(history.Count == 0
            ? "  automatic restarts so far: none"
            : "  automatic restarts so far (UTC): " + string.Join(", ", history.TakeLast(5).Select(t => t.ToString("yyyy-MM-dd HH:mm"))));

        lines.Add("");
        foreach (var f in status.Folders)
        {
            lines.Add($"{f.DisplayName}  [{f.Account}]");
            lines.Add($"  path:        {f.LocalPath}");
            lines.Add($"  remote:      {f.TargetPath}");
            lines.Add($"  health:      {f.Health}{(f.Paused ? " (paused)" : "")}{(f.Stalled ? " — may be stuck waiting" : "")}");
            lines.Add($"  activity:    {f.Activity}{(f.ActivitySince is DateTime s ? $" since {s:yyyy-MM-dd HH:mm:ss}" : "")}");
            lines.Add($"  last observed:         {Stamp(f.LastObservedAt)}");
            lines.Add($"  last sync started:     {Stamp(f.LastSyncStartedAt)}");
            lines.Add($"  last sync ended:       {Stamp(f.LastSyncCompletedAt)}");
            lines.Add($"  last successful sync:  {Stamp(f.LastSuccessfulSyncAt)}");
            lines.Add($"  last connection check: {Stamp(f.LastConnectionCheckAt)}");
            lines.Add($"  error:       {f.ErrorMessage}");
            lines.Add($"  item:        {f.ErrorPath}");
            lines.Add($"  when:        {Stamp(f.LastError)}");
            lines.Add("");
        }
        return lines;
    }

    private static string Stamp(DateTime? t) => t is DateTime d ? d.ToString("yyyy-MM-dd HH:mm:ss") : "—";

    private static string Age(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return $"{(int)span.TotalSeconds} s";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min";
        return $"{(int)span.TotalHours} h {span.Minutes} min";
    }
}
