using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Authorization;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>Scheduled task operations made from the interface: validates, acts, and logs with the name of the signed-in administrator.</summary>
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
        // Unchanged schedule: the next occurrence stays (a past one-time run does not block a name change).
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

    /// <summary>Allowed during a scrub, which goes on in the pool; a running script must be stopped first.</summary>
    public async Task DeleteAsync(int id)
    {
        var name = await coordinator.DeleteAsync(id);
        await repo.AuditAsync(await ActorAsync(), "Scheduled task deleted", name);
    }

    public async Task RunNowAsync(int id) => await coordinator.RunNowAsync(id, await ActorAsync());

    public async Task StopAsync(int id) => await coordinator.StopAsync(id, await ActorAsync());
}
