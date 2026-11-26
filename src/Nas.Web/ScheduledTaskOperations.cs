using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Authorization;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>Les opérations sur les tâches planifiées faites depuis l'interface : valide, agit, journalise avec le nom de l'administrateur connecté.</summary>
public class ScheduledTaskOperations(ScheduledTaskCoordinator coordinator, ScheduledTaskRepo tasks, Repo repo, AuthenticationStateProvider auth)
{
    async Task<string> ActorAsync() => (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? "unknown";

    public async Task SaveAsync(ScheduledTaskRow task)
    {
        task.Name = task.Name.Trim();
        if (ScheduledTaskCoordinator.Validate(task) is { } e) throw new AgentException(e);

        var isNew = task.Id == 0;
        var before = isNew ? null : await tasks.GetTaskAsync(task.Id) ?? throw new AgentException(Msg.F("Task not found."));
        if (before is null)
        {
            task.SnapshotTag = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            task.CreatedUtc = DateTime.UtcNow;
        }
        else
        {
            task.Type = before.Type;
            task.SnapshotTag = before.SnapshotTag;
            task.CreatedUtc = before.CreatedUtc;
        }
        // Horaire inchangé : la prochaine occurrence reste (une exécution unique déjà passée ne bloque pas un changement de nom).
        task.NextRunUtc = before is not null && before.Schedule == task.Schedule && before.Enabled == task.Enabled
            ? before.NextRunUtc
            : NextRun(task);
        await tasks.SaveTaskAsync(task);
        await repo.AuditAsync(await ActorAsync(), isNew ? "Scheduled task created" : "Scheduled task modified", task.Name);
    }

    public async Task SetEnabledAsync(int id, bool enabled)
    {
        var task = await tasks.GetTaskAsync(id) ?? throw new AgentException(Msg.F("Task not found."));
        task.Enabled = enabled;
        task.NextRunUtc = NextRun(task);
        await tasks.SaveTaskAsync(task);
        await repo.AuditAsync(await ActorAsync(), enabled ? "Scheduled task enabled" : "Scheduled task disabled", task.Name);
    }

    static DateTime? NextRun(ScheduledTaskRow task)
    {
        if (!task.Enabled) return null;
        return task.Rule().NextAfter(DateTime.UtcNow, TimeZoneInfo.Local)
            ?? throw new AgentException(Msg.F("This date and time are in the past."));
    }

    /// <summary>Permis même pendant une exécution : un scrub déjà lancé continue sur le pool, les instantanés déjà créés restent.</summary>
    public async Task DeleteAsync(int id)
    {
        var task = await tasks.GetTaskAsync(id) ?? throw new AgentException(Msg.F("Task not found."));
        await tasks.DeleteTaskAsync(id);
        await repo.AuditAsync(await ActorAsync(), "Scheduled task deleted", task.Name);
    }

    public async Task RunNowAsync(int id) => await coordinator.RunNowAsync(id, await ActorAsync());
}
