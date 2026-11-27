using System.Globalization;
using System.Text.RegularExpressions;
using Nas.Contracts;
using Nas.Web.Alerting;
using Nas.Web.Data;

namespace Nas.Web;

public enum ScrubState { Running, Succeeded, Errors, Cancelled, Superseded }

/// <summary>
/// Runs scheduled tasks: starts those that are due, follows scrubs and scripts until they end and records each result.
/// No dependency on the signed-in user (the scheduler uses it too): the actor is passed as a parameter.
/// </summary>
public class ScheduledTaskCoordinator(AgentClient agent, Repo repo, ScheduledTaskRepo tasks, AlertService alerts)
{
    /// <summary>Delay beyond which an occurrence is considered missed (NAS or web interface stopped, agent unreachable).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    const string InProgress = "Scrub in progress.";
    const string ScriptInProgress = "Script in progress.";
    /// <summary>Prefix of <see cref="ScheduledRunRow.Pending"/> for a script; a pool name never contains “:”.</summary>
    public const string ScriptPrefix = "script:";

    // Static: the interface and the scheduler must not start the same task at the same time.
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static string? Validate(ScheduledTaskRow t)
    {
        if (t.Name.Trim().Length is 0 or > 100) return Msg.F("Enter a name (100 characters at most).");
        if (t.Type == ScheduledTaskType.Script)
            return ScriptRules.Validate(t.Script, t.RunAs, t.TimeoutMinutes) ?? t.Rule().Validate();
        if (t.Type is not (ScheduledTaskType.Snapshot or ScheduledTaskType.Scrub)) return Msg.F("Unknown task type.");
        var targets = t.TargetList();
        if (targets.Length == 0)
            return t.Type == ScheduledTaskType.Snapshot ? Msg.F("Choose at least one dataset.") : Msg.F("Choose at least one pool.");
        foreach (var target in targets)
            if ((t.Type == ScheduledTaskType.Snapshot ? Rules.ValidateDatasetName(target) : Rules.ValidatePoolName(target)) is { } e) return e;
        if (t.Type == ScheduledTaskType.Snapshot && t.Keep is < 1 or > 1000) return Msg.F("Keep between 1 and 1000 snapshots.");
        return t.Rule().Validate();
    }

    // ---- Scheduling

    /// <summary>One scheduler pass: concludes finished operations, then handles the tasks whose time has come.</summary>
    public async Task TickAsync(DateTime nowUtc, TimeZoneInfo zone, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            await SyncCoreAsync(ct);
            var due = (await tasks.GetTasksAsync()).Where(t => t.Enabled && t.NextRunUtc is { } n && Utc(n) <= nowUtc).ToList();
            if (due.Count == 0) return;

            // Agent unreachable: nothing is touched (HttpRequestException reaches the scheduler). The occurrence stays due and, once too late, is missed.
            try { await agent.GetPoolsAsync(ct); }
            catch (AgentException) { } // the agent answers; if ZFS is at fault, the task will say so

            foreach (var t in due)
            {
                var slot = Utc(t.NextRunUtc!.Value);
                var late = nowUtc - slot > Grace;
                // The next occurrence is saved before running: a stop along the way does not start the same one twice.
                await tasks.SetNextRunAsync(t.Id, t.Rule().NextAfter(nowUtc, zone));
                if (late && !t.CatchUp)
                    await RecordAsync(t, slot, TaskRunStatus.Missed, "Schedule", "UrsaNAS could not run the task at the scheduled time.");
                else if (await tasks.IsRunningAsync(t.Id))
                    await RecordAsync(t, nowUtc, TaskRunStatus.Skipped, "Schedule", "The previous run is not finished.");
                else
                    await StartCoreAsync(t, late ? "Catch-up" : "Schedule", ct);
            }
        }
        finally { Gate.Release(); }
    }

    public async Task RunNowAsync(int taskId, string actor, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var t = await tasks.GetTaskAsync(taskId) ?? throw new AgentException(Msg.F("Task not found."));
            if (await tasks.IsRunningAsync(taskId)) throw new AgentException(Msg.F("This task is already running."));
            await repo.AuditAsync(actor, "Scheduled task started manually", t.Name);
            await StartCoreAsync(t, "Manual", ct);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Asks the agent to stop the running script; the result is recorded on the next scheduler pass.</summary>
    public async Task StopAsync(int taskId, string actor, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var run = (await tasks.GetOpenRunsAsync()).FirstOrDefault(r => r.TaskId == taskId && ScriptId(r) is not null)
                ?? throw new AgentException(Msg.F("No script is running for this task."));
            await agent.CancelScriptAsync(ScriptId(run)!, ct);
            await repo.AuditAsync(actor, "Scheduled task stopped manually", run.TaskName);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Deletes the task and its history; returns its name. Inside the lock: the scheduler cannot start it meanwhile.</summary>
    public async Task<string> DeleteAsync(int taskId, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var t = await tasks.GetTaskAsync(taskId) ?? throw new AgentException(Msg.F("Task not found."));
            // Without follow-up, a script would keep running with no way to stop it or see its result.
            if (t.Type == ScheduledTaskType.Script && await tasks.IsRunningAsync(taskId))
                throw new AgentException(Msg.F("A script is running: stop it before deleting the task."));
            await tasks.DeleteTaskAsync(taskId);
            return t.Name;
        }
        finally { Gate.Release(); }
    }

    // ---- Execution

    async Task StartCoreAsync(ScheduledTaskRow t, string reason, CancellationToken ct)
    {
        var run = new ScheduledRunRow { TaskId = t.Id, TaskName = t.Name, StartedUtc = DateTime.UtcNow, Reason = reason };
        await tasks.AddRunAsync(run);
        var lines = new List<string>();
        try
        {
            if (t.Type == ScheduledTaskType.Snapshot) await RunSnapshotsAsync(t, run, lines, ct);
            else if (t.Type == ScheduledTaskType.Scrub) await StartScrubsAsync(t, run, lines, ct);
            else if (t.Type == ScheduledTaskType.Script) await StartScriptAsync(t, run, ct);
            else Fail(run, lines, Msg.F("Unknown task type."));
        }
        catch (AgentException e) { Fail(run, lines, e.Message); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { Fail(run, lines, "Agent unreachable."); }

        run.Details = lines.Count == 0 ? null : string.Join('\n', lines);
        if (run.Pending.Length > 0)
        {
            run.Message ??= InProgress; // an error already met while starting remains the message
            await tasks.UpdateRunAsync(run);
        }
        else await FinishAsync(run);
    }

    static void Fail(ScheduledRunRow run, List<string> lines, string error)
    {
        run.HasError = true;
        run.Message ??= error;
        lines.Add(error);
    }

    async Task RunSnapshotsAsync(ScheduledTaskRow t, ScheduledRunRow run, List<string> lines, CancellationToken ct)
    {
        var name = SnapshotName(t.SnapshotTag, DateTime.Now);
        int created = 0, deleted = 0;
        foreach (var dataset in t.TargetList())
        {
            try
            {
                await agent.CreateSnapshotAsync(dataset, name, ct);
                created++;
                lines.Add(Msg.F("Snapshot {0} created.", $"{dataset}@{name}"));
                // Retention only runs after a successful creation: a failure must never reduce what remains.
                foreach (var old in Expired(await agent.GetSnapshotsAsync(ct), dataset, t.SnapshotTag, t.Keep))
                {
                    await agent.DestroySnapshotAsync(old.Name, ct);
                    deleted++;
                    lines.Add(Msg.F("Old snapshot {0} deleted.", old.Name));
                }
            }
            catch (AgentException e) // the other datasets are still processed
            {
                run.HasError = true;
                run.Message ??= Msg.F("Dataset {0}: {1}", dataset, e.Message);
                lines.Add(Msg.F("Dataset {0}: {1}", dataset, e.Message));
            }
        }
        run.Message ??= Msg.F("{0} snapshot(s) created, {1} old snapshot(s) deleted.", created, deleted);
    }

    public static string SnapshotName(string tag, DateTime localNow) =>
        $"auto-{tag}-{localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";

    /// <summary>The task's snapshots beyond the <paramref name="keep"/> most recent ones; all others (manual, backups, other tasks) are ignored.</summary>
    public static IReadOnlyList<SnapshotInfo> Expired(IEnumerable<SnapshotInfo> all, string dataset, string tag, int keep)
    {
        if (tag.Length == 0 || keep < 1) return [];
        var mine = new Regex($@"^auto-{Regex.Escape(tag)}-\d{{8}}-\d{{6}}\z", RegexOptions.CultureInvariant);
        return all.Where(s => s.Dataset == dataset && mine.IsMatch(s.Snapshot))
            .OrderByDescending(s => s.Created).ThenByDescending(s => s.Snapshot, StringComparer.Ordinal)
            .Skip(keep).ToList();
    }

    async Task StartScrubsAsync(ScheduledTaskRow t, ScheduledRunRow run, List<string> lines, CancellationToken ct)
    {
        var pools = await agent.GetPoolsAsync(ct);
        var started = new List<string>();
        foreach (var name in t.TargetList())
        {
            var pool = pools.FirstOrDefault(p => p.Name == name);
            if (pool is null) { Fail(run, lines, Msg.F("Pool “{0}” does not exist.", name)); continue; }
            if (ReadScrub(pool.Scan, pool.Errors) == ScrubState.Running)
            {
                lines.Add(Msg.F("Pool {0}: a scrub is already running.", name));
                continue;
            }
            try
            {
                await agent.ScrubPoolAsync(name, ct);
                started.Add(name);
                lines.Add(Msg.F("Pool {0}: scrub started.", name));
            }
            catch (AgentException e) { Fail(run, lines, Msg.F("Pool {0}: {1}", name, e.Message)); }
            run.Pending = string.Join('\n', started); // updated as we go: losing the agent midway does not erase the scrubs already started
        }
        if (started.Count == 0 && !run.HasError)
        {
            run.Status = TaskRunStatus.Skipped;
            run.Message = "A scrub is already running.";
        }
    }

    /// <summary>
    /// Reads the “scan” line of <c>zpool status</c>. After a successful <c>zpool scrub</c> it describes that scrub until it ends:
    /// “scrub in progress…”, “scrub paused…”, “scrub canceled…” or “scrub repaired … with N errors on …”.
    /// </summary>
    public static ScrubState ReadScrub(string scan, string errors)
    {
        if (scan.StartsWith("scrub in progress", StringComparison.Ordinal) || scan.StartsWith("scrub paused", StringComparison.Ordinal)) return ScrubState.Running;
        if (scan.StartsWith("scrub canceled", StringComparison.Ordinal)) return ScrubState.Cancelled;
        var done = Regex.Match(scan, @"^scrub repaired .+? with (\d+) errors on ");
        if (!done.Success) return ScrubState.Superseded; // replaced by a resilver, or no trace left
        var clean = done.Groups[1].Value == "0" && (errors.Length == 0 || errors.StartsWith("No known data errors", StringComparison.OrdinalIgnoreCase));
        return clean ? ScrubState.Succeeded : ScrubState.Errors;
    }

    /// <summary>Starts a script. Its identifier is saved before the call: a web interface stopped at that moment will find it again.</summary>
    async Task StartScriptAsync(ScheduledTaskRow t, ScheduledRunRow run, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        run.Pending = ScriptPrefix + id;
        run.Message = ScriptInProgress;
        await tasks.UpdateRunAsync(run);
        try { await agent.StartScriptAsync(new ScriptRunRequest(id, t.Script, t.RunAs, t.TimeoutMinutes), ct); }
        catch (AgentException e) { NotStarted(run, e.Message); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // The answer was lost: in case the script did start, it is stopped.
            try { await agent.CancelScriptAsync(id, CancellationToken.None); }
            catch (Exception x) when (x is AgentException or HttpRequestException or TaskCanceledException) { }
            NotStarted(run, "Agent unreachable.");
        }
    }

    // No detail line: for a script, the details are its output.
    static void NotStarted(ScheduledRunRow run, string error)
    {
        run.Pending = "";
        run.HasError = true;
        run.Message = error;
    }

    static string? ScriptId(ScheduledRunRow run) => run.Pending.StartsWith(ScriptPrefix, StringComparison.Ordinal) ? run.Pending[ScriptPrefix.Length..] : null;

    /// <summary>Called inside the lock: concludes finished operations and marks as interrupted any run that nothing will move forward.</summary>
    async Task SyncCoreAsync(CancellationToken ct)
    {
        var open = await tasks.GetOpenRunsAsync();
        foreach (var run in open.Where(r => r.Pending.Length == 0).ToList()) // an immediate run cut short by a stop of the web interface
        {
            run.Status = TaskRunStatus.Interrupted;
            run.Message = "Interrupted.";
            await FinishAsync(run);
            open.Remove(run);
        }
        // Kept apart: a failing ZFS must not hold back script results.
        await SyncScriptsAsync(open.Where(r => ScriptId(r) is not null).ToList(), ct);
        await SyncScrubsAsync(open.Where(r => ScriptId(r) is null).ToList(), ct);
    }

    async Task SyncScriptsAsync(List<ScheduledRunRow> open, CancellationToken ct)
    {
        if (open.Count == 0) return;
        IReadOnlyList<ScriptRunStatus> statuses;
        try { statuses = await agent.GetScriptRunsAsync(ct); }
        catch (AgentException) { return; } // unknown state: we will try again
        foreach (var run in open)
        {
            var status = statuses.FirstOrDefault(s => s.Id == ScriptId(run));
            if (status is null) // runs live in the agent's memory
            {
                run.Pending = "";
                run.Status = TaskRunStatus.Interrupted;
                run.Message = "The agent was restarted while the script was running.";
                await FinishAsync(run);
            }
            else if (status.State != ScriptState.Running)
            {
                ReadScript(run, status);
                await FinishAsync(run);
            }
        }
        // Results are recorded: the agent can forget finished scripts, including those no run is waiting for anymore.
        foreach (var done in statuses.Where(s => s.State != ScriptState.Running))
        {
            try { await agent.AcknowledgeScriptAsync(done.Id, ct); }
            catch (AgentException) { }
        }
    }

    static void ReadScript(ScheduledRunRow run, ScriptRunStatus s)
    {
        run.Pending = "";
        run.EndedUtc = s.EndedUtc;
        run.Details = s.Output.Length == 0 ? null : s.Output;
        (run.Status, run.Message) = s.State switch
        {
            ScriptState.Succeeded => (TaskRunStatus.Success, "Script completed."),
            ScriptState.Stopped => (TaskRunStatus.Cancelled, "Stopped by an administrator."),
            ScriptState.TimedOut => (TaskRunStatus.Failed, Msg.F("The script exceeded its time limit of {0} minute(s).", s.TimeoutMinutes)),
            _ => (TaskRunStatus.Failed, s.Error is null
                ? Msg.F("The script ended with code {0}.", s.ExitCode)
                : Msg.F("The script could not be followed: {0}", s.Error)),
        };
    }

    async Task SyncScrubsAsync(List<ScheduledRunRow> open, CancellationToken ct)
    {
        IReadOnlyList<PoolInfo>? pools = null;
        foreach (var run in open)
        {
            var pending = run.Pending.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (pools is null)
            {
                try { pools = await agent.GetPoolsAsync(ct); }
                catch (AgentException) { return; } // unknown state: we will try again
            }

            var lines = run.Details is null ? new List<string>() : run.Details.Split('\n').ToList();
            var before = pending.Count;
            if (run.Message == InProgress) run.Message = null; // the first error, if any, must be able to take its place
            foreach (var name in pending.ToList())
            {
                var pool = pools.FirstOrDefault(p => p.Name == name);
                if (pool is null) Fail(run, lines, Msg.F("Pool “{0}” does not exist.", name));
                else switch (ReadScrub(pool.Scan, pool.Errors))
                {
                    case ScrubState.Running: continue;
                    case ScrubState.Succeeded: lines.Add(Msg.F("Pool {0}: scrub completed without errors.", name)); break;
                    case ScrubState.Errors: Fail(run, lines, Msg.F("Pool {0}: the scrub found errors. {1}", name, pool.Scan)); break;
                    case ScrubState.Cancelled: run.HasCancel = true; lines.Add(Msg.F("Pool {0}: the scrub was cancelled.", name)); break;
                    default: run.HasInterrupt = true; lines.Add(Msg.F("Pool {0}: the end of the scrub could not be observed.", name)); break;
                }
                pending.Remove(name);
            }
            if (pending.Count == before) continue;
            run.Pending = string.Join('\n', pending);
            run.Details = string.Join('\n', lines);
            if (pending.Count > 0)
            {
                run.Message ??= InProgress;
                await tasks.UpdateRunAsync(run);
            }
            else
            {
                run.Message ??= run.HasCancel ? "Cancelled." : run.HasInterrupt ? "Interrupted." : "Scrub completed without errors.";
                await FinishAsync(run);
            }
        }
    }

    // ---- Results

    async Task RecordAsync(ScheduledTaskRow t, DateTime atUtc, string status, string reason, string message)
    {
        var run = new ScheduledRunRow { TaskId = t.Id, TaskName = t.Name, StartedUtc = atUtc, EndedUtc = atUtc, Status = status, Reason = reason, Message = message };
        await tasks.AddRunAsync(run);
        await ConcludeAsync(run);
    }

    async Task FinishAsync(ScheduledRunRow run)
    {
        run.EndedUtc ??= DateTime.UtcNow;
        if (run.Status == TaskRunStatus.Running)
            run.Status = run.HasError ? TaskRunStatus.Failed
                : run.HasCancel ? TaskRunStatus.Cancelled
                : run.HasInterrupt ? TaskRunStatus.Interrupted
                : TaskRunStatus.Success;
        await tasks.UpdateRunAsync(run);
        await ConcludeAsync(run);
    }

    async Task ConcludeAsync(ScheduledRunRow run)
    {
        await tasks.PruneRunsAsync(run.TaskId);
        await repo.AuditAsync("system", "Scheduled task finished", Msg.F(run.Status switch
        {
            TaskRunStatus.Success => "Task “{0}” succeeded.",
            TaskRunStatus.Failed => "Task “{0}” failed.",
            TaskRunStatus.Skipped => "Task “{0}” was skipped.",
            TaskRunStatus.Missed => "Task “{0}” was missed.",
            TaskRunStatus.Cancelled => "Task “{0}” was cancelled.",
            _ => "Task “{0}” was interrupted.",
        }, run.TaskName));
        // The alert is opened right away: the periodic check would miss a failure immediately followed by a success.
        if (run.Status == TaskRunStatus.Failed && await tasks.GetTaskAsync(run.TaskId) is { Enabled: true } task)
            await alerts.RaiseNowAsync(AlertRules.TaskFailed(task.Id, task.Name));
    }

    static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}

/// <summary>Runs every 5 seconds: follows operations in progress and starts due tasks (server time).</summary>
public sealed class ScheduledTaskMonitor(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<ScheduledTaskMonitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(cfg.GetValue("ScheduledTasks:StartDelaySeconds", 20)), ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(cfg.GetValue("ScheduledTasks:IntervalSeconds", 5)));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                TimeZoneInfo.ClearCachedData(); // the server's time zone can change while the web interface is running
                await scope.ServiceProvider.GetRequiredService<ScheduledTaskCoordinator>().TickAsync(DateTime.UtcNow, TimeZoneInfo.Local, ct);
            }
            catch (Exception e) when (e is AgentException or HttpRequestException) { log.LogDebug(e, "Scheduled task check skipped (agent unreachable)"); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Scheduled task check failed"); }
        }
        while (await WaitAsync(timer, ct));
    }

    static async Task<bool> WaitAsync(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
