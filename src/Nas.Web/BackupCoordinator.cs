using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>
/// Fait le lien entre les travaux enregistrés en base et l'agent, qui exécute les copies : démarre, enregistre la fin des copies,
/// lance les travaux planifiés. Sans dépendance à l'utilisateur connecté (le planificateur s'en sert aussi) : l'auteur est passé en paramètre.
/// </summary>
public class BackupCoordinator(AgentClient agent, Repo repo, BackupRepo jobs)
{
    // Statique : l'interface et le planificateur ne doivent pas démarrer le même travail en même temps.
    static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<BackupJobDef> BuildDefAsync(BackupJobRow j)
    {
        var share = (await repo.GetSharesAsync()).FirstOrDefault(s => s.Name.Equals(j.Share, StringComparison.OrdinalIgnoreCase))
            ?? throw new AgentException(Msg.F("Share “{0}” does not exist.", j.Share));
        return new BackupJobDef(j.Id, j.Name, share.Name, share.Dataset, j.IncludeList(), j.TargetDir, j.TargetId, j.DestFolder,
            j.MirrorDeletes, j.RetentionDays, j.Verify, j.WindowsDisk, j.ExcludeList(), j.Snapshot);
    }

    public static string? Validate(BackupJobRow j)
    {
        if (BackupRules.ValidateName(j.Name) is { } e0) return e0;
        if (Rules.ValidateShareName(j.Share) is { } e1) return e1;
        if (BackupRules.ValidateDestFolder(j.DestFolder) is { } e2) return e2;
        if (string.IsNullOrWhiteSpace(j.TargetDir)) return Msg.F("Choose the backup disk.");
        if (j.ScheduleMode == BackupSchedule.Daily && !TimeSpan.TryParse(j.DailyTime, out _)) return Msg.F("Invalid time.");
        return null;
    }

    /// <summary>Démarre une copie sur l'agent et ouvre sa ligne d'historique.</summary>
    public async Task StartAsync(BackupJobRow job, string reason, string actor)
    {
        await Gate.WaitAsync();
        try
        {
            if (job.TargetId is null) throw new AgentException(Msg.F("The external disk has not been initialised for this job."));
            await agent.StartBackupAsync(await BuildDefAsync(job));
            await jobs.AddRunAsync(job, reason);
            await repo.AuditAsync(actor, "Backup started", job.Name);
        }
        finally { Gate.Release(); }
    }

    public Task CancelAsync(int jobId) => agent.CancelBackupAsync(jobId);

    /// <summary>
    /// Enregistre les copies que l'agent a terminées (puis les lui fait oublier) et marque « Interrompue » celles qu'il a perdues
    /// (redémarrage de l'agent en cours de copie : le journal du moteur reprendra au prochain passage).
    /// </summary>
    public async Task SyncAsync()
    {
        await Gate.WaitAsync(); // pas pendant StartAsync : la ligne d'historique doit exister avant qu'on lise la fin de la copie
        try { await SyncCoreAsync(); }
        finally { Gate.Release(); }
    }

    async Task SyncCoreAsync()
    {
        var statuses = await agent.GetBackupRunsAsync();
        foreach (var s in statuses.Where(s => s.Result is not null))
        {
            var run = await jobs.GetOpenRunAsync(s.JobId);
            if (run is not null)
            {
                var r = s.Result!;
                run.EndedUtc = DateTime.UtcNow;
                run.Status = r.Status;
                run.Message = r.Message;
                run.Details = r.ErrorSamples.Count == 0 ? null : string.Join('\n', r.ErrorSamples);
                (run.FilesSeen, run.Copied, run.BytesCopied, run.Unchanged, run.Deleted, run.Errors) = (s.FilesSeen, s.Copied, s.BytesCopied, s.Unchanged, s.Deleted, s.Errors);
                await jobs.FinishRunAsync(run);
                if (r.Status is "Success" or "CompletedWithErrors") await jobs.SetEstimateAsync(s.JobId, s.FilesSeen, s.BytesSeen);
                await jobs.PruneRunsAsync(s.JobId);
                await repo.AuditAsync("system", "Backup finished", $"{run.JobName}: {r.Status}");
            }
            await agent.AcknowledgeBackupAsync(s.JobId);
        }

        var active = statuses.Select(s => s.JobId).ToHashSet();
        foreach (var open in await jobs.GetOpenRunsAsync())
        {
            if (active.Contains(open.JobId)) continue;
            open.EndedUtc = DateTime.UtcNow;
            open.Status = "Interrupted";
            open.Message = "Interrupted.";
            await jobs.FinishRunAsync(open);
        }
    }

    /// <summary>
    /// Lance les travaux dont l'heure est venue ou dont le disque vient d'être branché.
    /// <paramref name="wasPresent"/> : le disque de chaque travail était-il là au passage précédent ? (garde la mémoire d'un passage à l'autre)
    /// </summary>
    public async Task ScheduleAsync(DateTime nowLocal, IDictionary<int, bool> wasPresent)
    {
        var running = (await agent.GetBackupRunsAsync()).Select(s => s.JobId).ToHashSet();
        foreach (var j in await jobs.GetJobsAsync())
        {
            var disk = await agent.CheckBackupDiskAsync(j.TargetDir, j.TargetId);
            var present = disk.State == BackupDiskState.Connected;
            wasPresent.TryGetValue(j.Id, out var was);
            wasPresent[j.Id] = present;
            if (!j.Enabled || !present || running.Contains(j.Id)) continue;

            string? reason = null;
            if (j.ScheduleMode == BackupSchedule.OnPlug && !was) reason = "Disk connected";
            else if (j.ScheduleMode == BackupSchedule.Daily && TimeSpan.TryParse(j.DailyTime, out var tod))
            {
                var slot = nowLocal.Date + tod;
                var last = await jobs.LastStartedUtcAsync(j.Id) ?? j.CreatedUtc; // un travail créé après l'heure du jour attend le lendemain
                if (nowLocal >= slot && DateTime.SpecifyKind(last, DateTimeKind.Utc).ToLocalTime() < slot) reason = "Schedule";
            }
            if (reason is null) continue;
            try { await StartAsync(j, reason, "system"); }
            catch (AgentException) { } // déjà en cours, ou refusé par l'agent : le prochain passage réessaiera
        }
    }
}

/// <summary>Passe toutes les 20 secondes : enregistre les copies terminées, puis lance celles qui sont dues.</summary>
public sealed class BackupMonitor(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<BackupMonitor> log) : BackgroundService
{
    readonly Dictionary<int, bool> _wasPresent = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(cfg.GetValue("Backups:StartDelaySeconds", 20)), ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(cfg.GetValue("Backups:IntervalSeconds", 20)));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<BackupCoordinator>();
                await coordinator.SyncAsync();
                await coordinator.ScheduleAsync(DateTime.Now, _wasPresent);
            }
            catch (Exception e) when (e is AgentException or HttpRequestException) { log.LogDebug(e, "Backup check skipped (agent unreachable)"); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Backup check failed"); }
        }
        while (await WaitAsync(timer, ct));
    }

    static async Task<bool> WaitAsync(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
