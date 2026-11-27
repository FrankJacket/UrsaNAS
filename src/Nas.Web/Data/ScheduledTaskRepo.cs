using System.Text.Json;
using Dapper;
using Nas.Contracts;

namespace Nas.Web.Data;

public static class ScheduledTaskType
{
    public const string Snapshot = "snapshot", Scrub = "scrub", Script = "script", Trim = "trim", SelfTest = "selftest";
}

public static class TaskRunStatus
{
    public const string Running = "Running", Success = "Success", Failed = "Failed", Skipped = "Skipped",
        Missed = "Missed", Interrupted = "Interrupted", Cancelled = "Cancelled";
}

public sealed class ScheduledTaskRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = ScheduledTaskType.Snapshot;
    /// <summary>One target per line: datasets (snapshots), pools (scrub, TRIM) or disk serial numbers (self-tests).</summary>
    public string Targets { get; set; } = "";
    /// <summary>Snapshots: how many to keep for each dataset.</summary>
    public int Keep { get; set; } = 14;
    /// <summary>
    /// Mark specific to the task, included in the names of its snapshots: retention only deletes those.
    /// Random rather than the identifier, which starts again at 1 after a reinstallation.
    /// </summary>
    public string SnapshotTag { get; set; } = "";
    /// <summary>Scripts: the Bash text.</summary>
    public string Script { get; set; } = "";
    /// <summary>Scripts: “root” or a NAS user; nothing is selected by default.</summary>
    public string RunAs { get; set; } = "";
    public int TimeoutMinutes { get; set; } = ScriptRules.DefaultTimeoutMinutes;
    /// <summary>Self-tests: short or long (<see cref="SelfTestType"/>).</summary>
    public string TestType { get; set; } = SelfTestType.Short;
    /// <summary>JSON of <see cref="ScheduleRule"/>.</summary>
    public string Schedule { get; set; } = JsonSerializer.Serialize(new ScheduleRule());
    /// <summary>Run once on return after missed occurrences.</summary>
    public bool CatchUp { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Next occurrence; saved so that missed ones can be recognized after a restart. <c>null</c>: disabled or finished.</summary>
    public DateTime? NextRunUtc { get; set; }

    public string[] TargetList() => Targets.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public ScheduleRule Rule()
    {
        try { return JsonSerializer.Deserialize<ScheduleRule>(Schedule) ?? new ScheduleRule { Repeat = "" }; }
        catch (JsonException) { return new ScheduleRule { Repeat = "" }; } // unreadable: never run, rather than run on a made-up schedule
    }

    public void SetRule(ScheduleRule rule) => Schedule = JsonSerializer.Serialize(rule);
}

public sealed class ScheduledRunRow
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public string TaskName { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public string Status { get; set; } = TaskRunStatus.Running;
    /// <summary>Manual, Schedule or Catch-up.</summary>
    public string Reason { get; set; } = "";
    public string? Message { get; set; }
    /// <summary>One line per processed target.</summary>
    public string? Details { get; set; }
    /// <summary>
    /// Operation that goes on after being started, one line per target still awaited: a scrubbed pool, “trim:pool”,
    /// “smart:serial”, or the single “script:id”.
    /// </summary>
    public string Pending { get; set; } = "";
    /// <summary>Results already known while waiting: they count in the final result.</summary>
    public bool HasError { get; set; }
    public bool HasCancel { get; set; }
    public bool HasInterrupt { get; set; }
}

/// <summary>Summary of a task for alerts: its last conclusive result (success or failure).</summary>
public sealed record TaskHealth(int TaskId, string Name, bool Enabled, string? LastStatus);

public class ScheduledTaskRepo(Db db)
{
    const string TaskColumns = "Id,Name,Type,Targets,Keep,SnapshotTag,Schedule,CatchUp,Enabled,CreatedUtc,NextRunUtc,Script,RunAs,TimeoutMinutes,TestType";
    public const int KeptRuns = 50;

    public async Task<List<ScheduledTaskRow>> GetTasksAsync()
    {
        using var c = db.Open();
        return (await c.QueryAsync<ScheduledTaskRow>($"SELECT {TaskColumns} FROM ScheduledTasks ORDER BY Name COLLATE NOCASE")).ToList();
    }

    public async Task<ScheduledTaskRow?> GetTaskAsync(int id)
    {
        using var c = db.Open();
        return await c.QuerySingleOrDefaultAsync<ScheduledTaskRow>($"SELECT {TaskColumns} FROM ScheduledTasks WHERE Id=@id", new { id });
    }

    /// <summary>Creates (Id = 0) or updates a task; returns its identifier.</summary>
    public async Task<int> SaveTaskAsync(ScheduledTaskRow t)
    {
        using var c = db.Open();
        if (t.Id == 0)
            return await c.ExecuteScalarAsync<int>("""
                INSERT INTO ScheduledTasks(Name,Type,Targets,Keep,SnapshotTag,Schedule,CatchUp,Enabled,CreatedUtc,NextRunUtc,Script,RunAs,TimeoutMinutes,TestType)
                VALUES(@Name,@Type,@Targets,@Keep,@SnapshotTag,@Schedule,@CatchUp,@Enabled,@CreatedUtc,@NextRunUtc,@Script,@RunAs,@TimeoutMinutes,@TestType);
                SELECT last_insert_rowid();
                """, t);
        await c.ExecuteAsync("""
            UPDATE ScheduledTasks SET Name=@Name,Type=@Type,Targets=@Targets,Keep=@Keep,Schedule=@Schedule,CatchUp=@CatchUp,Enabled=@Enabled,NextRunUtc=@NextRunUtc,
              Script=@Script,RunAs=@RunAs,TimeoutMinutes=@TimeoutMinutes,TestType=@TestType WHERE Id=@Id
            """, t);
        return t.Id;
    }

    public async Task SetNextRunAsync(int id, DateTime? nextRunUtc)
    {
        using var c = db.Open();
        await c.ExecuteAsync("UPDATE ScheduledTasks SET NextRunUtc=@nextRunUtc WHERE Id=@id", new { id, nextRunUtc });
    }

    public async Task DeleteTaskAsync(int id)
    {
        using var c = db.Open();
        await c.ExecuteAsync("DELETE FROM ScheduledTaskRuns WHERE TaskId=@id; DELETE FROM ScheduledTasks WHERE Id=@id", new { id });
    }

    // ---- Runs

    /// <summary>Records a run (in progress, or already concluded such as a missed occurrence) and sets its identifier.</summary>
    public async Task AddRunAsync(ScheduledRunRow r)
    {
        using var c = db.Open();
        r.Id = await c.ExecuteScalarAsync<int>("""
            INSERT INTO ScheduledTaskRuns(TaskId,TaskName,StartedUtc,EndedUtc,Status,Reason,Message,Details,Pending,HasError,HasCancel,HasInterrupt)
            VALUES(@TaskId,@TaskName,@StartedUtc,@EndedUtc,@Status,@Reason,@Message,@Details,@Pending,@HasError,@HasCancel,@HasInterrupt);
            SELECT last_insert_rowid();
            """, r);
    }

    public async Task UpdateRunAsync(ScheduledRunRow r)
    {
        using var c = db.Open();
        await c.ExecuteAsync("""
            UPDATE ScheduledTaskRuns SET EndedUtc=@EndedUtc,Status=@Status,Message=@Message,Details=@Details,Pending=@Pending,
              HasError=@HasError,HasCancel=@HasCancel,HasInterrupt=@HasInterrupt WHERE Id=@Id
            """, r);
    }

    public async Task<List<ScheduledRunRow>> GetOpenRunsAsync()
    {
        using var c = db.Open();
        return (await c.QueryAsync<ScheduledRunRow>("SELECT * FROM ScheduledTaskRuns WHERE Status='Running' ORDER BY Id")).ToList();
    }

    public async Task<bool> IsRunningAsync(int taskId)
    {
        using var c = db.Open();
        return await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ScheduledTaskRuns WHERE TaskId=@taskId AND Status='Running'", new { taskId }) > 0;
    }

    public async Task<List<ScheduledRunRow>> GetRunsAsync(int taskId, int limit = KeptRuns)
    {
        using var c = db.Open();
        return (await c.QueryAsync<ScheduledRunRow>("SELECT * FROM ScheduledTaskRuns WHERE TaskId=@taskId ORDER BY Id DESC LIMIT @limit", new { taskId, limit })).ToList();
    }

    /// <summary>The state to show for each task: the run in progress if there is one, otherwise the most recent one.</summary>
    public async Task<Dictionary<int, ScheduledRunRow>> GetLatestRunsAsync()
    {
        using var c = db.Open();
        var rows = await c.QueryAsync<ScheduledRunRow>("""
            SELECT * FROM ScheduledTaskRuns WHERE Id IN (
              SELECT COALESCE(MAX(CASE WHEN Status='Running' THEN Id END), MAX(Id)) FROM ScheduledTaskRuns GROUP BY TaskId)
            """);
        return rows.ToDictionary(r => r.TaskId);
    }

    /// <summary>Keeps a task's latest runs; never deletes the one in progress, even if skipped occurrences have piled up after it.</summary>
    public async Task PruneRunsAsync(int taskId, int keep = KeptRuns)
    {
        using var c = db.Open();
        await c.ExecuteAsync("""
            DELETE FROM ScheduledTaskRuns WHERE TaskId=@taskId AND Status<>'Running'
              AND Id NOT IN (SELECT Id FROM ScheduledTaskRuns WHERE TaskId=@taskId ORDER BY Id DESC LIMIT @keep)
            """, new { taskId, keep });
    }

    /// <summary>For alerts: only a success or a failure changes a task's state (not a skipped, missed or cancelled occurrence).</summary>
    public async Task<List<TaskHealth>> GetHealthAsync()
    {
        using var c = db.Open();
        var rows = await c.QueryAsync<(int Id, string Name, bool Enabled, string? Last)>("""
            SELECT t.Id, t.Name, t.Enabled,
              (SELECT Status FROM ScheduledTaskRuns r WHERE r.TaskId=t.Id AND r.Status IN ('Success','Failed') ORDER BY r.Id DESC LIMIT 1)
            FROM ScheduledTasks t
            """);
        return rows.Select(r => new TaskHealth(r.Id, r.Name, r.Enabled, r.Last)).ToList();
    }
}
