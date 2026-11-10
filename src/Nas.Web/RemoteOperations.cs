using Microsoft.AspNetCore.Components.Authorization;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>Les opérations sur les stockages distants faites depuis l'interface : valide, demande à l'agent d'agir, journalise (jamais le mot de passe).</summary>
public class RemoteOperations(AgentClient agent, Repo repo, AuthenticationStateProvider auth)
{
    async Task<string> ActorAsync() => (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? "unknown";

    static string Describe(AddRemoteRequest r) => Rules.IsCloud(r.Type)
        ? $"{r.Name} ({r.Type}{(r.Path.Length > 0 ? " /" + r.Path : "")})" // jamais le jeton
        : $"{r.Name} ({r.Type.ToUpperInvariant()} {r.Host}{(r.Type == "nfs" ? ":" : "/")}{r.Path})";

    public async Task TestAsync(AddRemoteRequest r)
    {
        if (Rules.ValidateRemote(r) is { } e) throw new AgentException(e);
        await agent.TestRemoteAsync(r);
    }

    public async Task AddAsync(AddRemoteRequest r)
    {
        if (Rules.ValidateRemote(r) is { } e) throw new AgentException(e);
        await agent.AddRemoteAsync(r);
        await repo.AuditAsync(await ActorAsync(), "Remote storage added", Describe(r));
    }

    public async Task MountAsync(string name)
    {
        await agent.MountRemoteAsync(name);
        await repo.AuditAsync(await ActorAsync(), "Remote storage mounted", name);
    }

    public async Task UnmountAsync(string name)
    {
        await agent.UnmountRemoteAsync(name);
        await repo.AuditAsync(await ActorAsync(), "Remote storage unmounted", name);
    }

    public async Task DeleteAsync(string name)
    {
        await agent.DeleteRemoteAsync(name);
        await repo.AuditAsync(await ActorAsync(), "Remote storage removed", name);
    }
}
