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
        if (task.Type == ScheduledTaskType.Script)
        {
            task.Script = ScriptRules.Normalize(task.Script);
            task.Targets = "";
        }
        else (task.Script, task.RunAs) = ("", "");
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

    /// <summary>Permis pendant un scrub, qui continue sur le pool ; un script en cours doit d'abord être arrêté.</summary>
    public async Task DeleteAsync(int id)
    {
        var name = await coordinator.DeleteAsync(id);
        await repo.AuditAsync(await ActorAsync(), "Scheduled task deleted", name);
    }

    public async Task RunNowAsync(int id) => await coordinator.RunNowAsync(id, await ActorAsync());

    public async Task StopAsync(int id) => await coordinator.StopAsync(id, await ActorAsync());
}
