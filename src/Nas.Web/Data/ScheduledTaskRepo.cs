using System.Text.Json;
using Dapper;
using Nas.Contracts;

namespace Nas.Web.Data;

public static class ScheduledTaskType
{
    public const string Snapshot = "snapshot", Scrub = "scrub", Script = "script";
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
    /// <summary>Une cible par ligne : des jeux de données (instantanés) ou des pools (scrub).</summary>
    public string Targets { get; set; } = "";
    /// <summary>Instantanés : combien en garder par jeu de données.</summary>
    public int Keep { get; set; } = 14;
    /// <summary>
    /// Marque propre à la tâche, reprise dans le nom de ses instantanés : la rétention ne supprime que ceux-là.
    /// Aléatoire plutôt que l'identifiant, qui recommence à 1 après une réinstallation.
    /// </summary>
    public string SnapshotTag { get; set; } = "";
    /// <summary>Scripts : le texte Bash.</summary>
    public string Script { get; set; } = "";
    /// <summary>Scripts : « root » ou un utilisateur du NAS ; aucun choix par défaut.</summary>
    public string RunAs { get; set; } = "";
    public int TimeoutMinutes { get; set; } = ScriptRules.DefaultTimeoutMinutes;
    /// <summary>JSON de <see cref="ScheduleRule"/>.</summary>
    public string Schedule { get; set; } = JsonSerializer.Serialize(new ScheduleRule());
    /// <summary>Exécuter une fois au retour après des occurrences manquées.</summary>
    public bool CatchUp { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Prochaine occurrence ; enregistrée pour reconnaître, après un redémarrage, celles qui ont été manquées. <c>null</c> : désactivée ou terminée.</summary>
    public DateTime? NextRunUtc { get; set; }

    public string[] TargetList() => Targets.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public ScheduleRule Rule()
    {
        try { return JsonSerializer.Deserialize<ScheduleRule>(Schedule) ?? new ScheduleRule { Repeat = "" }; }
        catch (JsonException) { return new ScheduleRule { Repeat = "" }; } // illisible : jamais exécutée plutôt qu'à un horaire inventé
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
    /// <summary>Manual, Schedule ou Catch-up.</summary>
    public string Reason { get; set; } = "";
    public string? Message { get; set; }
    /// <summary>Une ligne par cible traitée.</summary>
    public string? Details { get; set; }
    /// <summary>Opération qui continue après son lancement : les pools dont le scrub est encore attendu (un par ligne), ou « script:identifiant ».</summary>
    public string Pending { get; set; } = "";
    /// <summary>Résultats déjà connus pendant l'attente : ils comptent dans le résultat final.</summary>
    public bool HasError { get; set; }
    public bool HasCancel { get; set; }
    public bool HasInterrupt { get; set; }
}

/// <summary>Résumé d'une tâche pour les alertes : son dernier résultat concluant (réussite ou échec).</summary>
public sealed record TaskHealth(int TaskId, string Name, bool Enabled, string? LastStatus);

public class ScheduledTaskRepo(Db db)
{
    const string TaskColumns = "Id,Name,Type,Targets,Keep,SnapshotTag,Schedule,CatchUp,Enabled,CreatedUtc,NextRunUtc,Script,RunAs,TimeoutMinutes";
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

    /// <summary>Crée (Id = 0) ou met à jour une tâche ; renvoie son identifiant.</summary>
    public async Task<int> SaveTaskAsync(ScheduledTaskRow t)
    {
        using var c = db.Open();
        if (t.Id == 0)
            return await c.ExecuteScalarAsync<int>("""
                INSERT INTO ScheduledTasks(Name,Type,Targets,Keep,SnapshotTag,Schedule,CatchUp,Enabled,CreatedUtc,NextRunUtc,Script,RunAs,TimeoutMinutes)
                VALUES(@Name,@Type,@Targets,@Keep,@SnapshotTag,@Schedule,@CatchUp,@Enabled,@CreatedUtc,@NextRunUtc,@Script,@RunAs,@TimeoutMinutes);
                SELECT last_insert_rowid();
                """, t);
        await c.ExecuteAsync("""
            UPDATE ScheduledTasks SET Name=@Name,Type=@Type,Targets=@Targets,Keep=@Keep,Schedule=@Schedule,CatchUp=@CatchUp,Enabled=@Enabled,NextRunUtc=@NextRunUtc,
              Script=@Script,RunAs=@RunAs,TimeoutMinutes=@TimeoutMinutes WHERE Id=@Id
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

    // ---- Exécutions

    /// <summary>Enregistre une exécution (en cours, ou déjà conclue comme une occurrence manquée) et remplit son identifiant.</summary>
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

    /// <summary>L'état à montrer pour chaque tâche : l'exécution en cours s'il y en a une, sinon la plus récente.</summary>
    public async Task<Dictionary<int, ScheduledRunRow>> GetLatestRunsAsync()
    {
        using var c = db.Open();
        var rows = await c.QueryAsync<ScheduledRunRow>("""
            SELECT * FROM ScheduledTaskRuns WHERE Id IN (
              SELECT COALESCE(MAX(CASE WHEN Status='Running' THEN Id END), MAX(Id)) FROM ScheduledTaskRuns GROUP BY TaskId)
            """);
        return rows.ToDictionary(r => r.TaskId);
    }

    /// <summary>Garde les dernières exécutions d'une tâche ; jamais celle en cours, même si des occurrences ignorées se sont accumulées après elle.</summary>
    public async Task PruneRunsAsync(int taskId, int keep = KeptRuns)
    {
        using var c = db.Open();
        await c.ExecuteAsync("""
            DELETE FROM ScheduledTaskRuns WHERE TaskId=@taskId AND Status<>'Running'
              AND Id NOT IN (SELECT Id FROM ScheduledTaskRuns WHERE TaskId=@taskId ORDER BY Id DESC LIMIT @keep)
            """, new { taskId, keep });
    }

    /// <summary>Pour les alertes : seules une réussite ou un échec changent l'état d'une tâche (pas une occurrence ignorée, manquée ou annulée).</summary>
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
