using Microsoft.AspNetCore.Components.Authorization;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>Les opérations de sauvegarde faites depuis l'interface : valide, agit, journalise avec le nom de l'administrateur connecté.</summary>
public class BackupOperations(BackupCoordinator coordinator, BackupRepo jobs, AgentClient agent, Repo repo, AuthenticationStateProvider auth)
{
    async Task<string> ActorAsync() => (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? "unknown";

    public async Task SaveJobAsync(BackupJobRow job)
    {
        job.Name = job.Name.Trim();
        job.DestFolder = job.DestFolder.Trim();
        if (BackupCoordinator.Validate(job) is { } e) throw new AgentException(e);
        if (BackupRules.Validate(await coordinator.BuildDefAsync(job)) is { } e2) throw new AgentException(e2);

        var isNew = job.Id == 0;
        if (!isNew)
        {
            var before = await jobs.GetJobAsync(job.Id) ?? throw new AgentException(Msg.F("Job not found."));
            // Un autre disque n'a pas le marqueur de celui-ci : il faut l'initialiser pour ce travail.
            job.TargetId = before.TargetDir == job.TargetDir ? before.TargetId : null;
            job.CreatedUtc = before.CreatedUtc;
        }
        else job.TargetId = (await jobs.GetJobsAsync()).FirstOrDefault(j => j.TargetDir == job.TargetDir && j.TargetId is not null)?.TargetId; // même disque qu'un autre travail
        await jobs.SaveJobAsync(job);
        await repo.AuditAsync(await ActorAsync(), isNew ? "Backup job created" : "Backup job modified", job.Name);
    }

    public async Task DeleteJobAsync(int id)
    {
        var job = await jobs.GetJobAsync(id) ?? throw new AgentException(Msg.F("Job not found."));
        if ((await agent.GetBackupRunsAsync()).Any(r => r.JobId == id && r.Result is null))
            throw new AgentException(Msg.F("This job is running: cancel it first."));
        await jobs.DeleteJobAsync(id);
        await repo.AuditAsync(await ActorAsync(), "Backup job deleted", job.Name);
    }

    public async Task RunNowAsync(int id)
    {
        var job = await jobs.GetJobAsync(id) ?? throw new AgentException(Msg.F("Job not found."));
        await coordinator.StartAsync(job, "Manual", await ActorAsync());
    }

    public async Task CancelAsync(int id)
    {
        await coordinator.CancelAsync(id);
        await repo.AuditAsync(await ActorAsync(), "Backup cancelled", (await jobs.GetJobAsync(id))?.Name);
    }

    /// <summary>Écrit le marqueur sur le disque du travail ; les autres travaux du même disque le reprennent.</summary>
    public async Task InitDiskAsync(int id)
    {
        var job = await jobs.GetJobAsync(id) ?? throw new AgentException(Msg.F("Job not found."));
        var markerId = await agent.InitBackupDiskAsync(job.TargetDir);
        await jobs.AdoptDiskAsync(job.TargetDir, markerId);
        job.TargetId = markerId;
        await jobs.SaveJobAsync(job); // ce travail-ci, même s'il avait un autre identifiant
        await repo.AuditAsync(await ActorAsync(), "Backup disk initialised", job.TargetDir);
    }
}
