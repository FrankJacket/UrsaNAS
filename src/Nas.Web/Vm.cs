using Microsoft.AspNetCore.Components.Authorization;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>Les opérations sur les machines virtuelles faites depuis l'interface : valide, demande à l'agent d'agir, journalise.</summary>
public class VmOperations(AgentClient agent, Repo repo, AuthenticationStateProvider auth)
{
    async Task<string> ActorAsync() => (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? "unknown";

    static readonly Dictionary<string, string> Audit = new()
    {
        ["start"] = "Virtual machine started", ["shutdown"] = "Virtual machine shut down",
        ["stop"] = "Virtual machine powered off", ["remove"] = "Virtual machine deleted",
    };

    public async Task ActionAsync(string name, string action)
    {
        if (Rules.ValidateVmName(name) is { } e) throw new AgentException(e);
        if (!Audit.TryGetValue(action, out var label)) throw new AgentException(Msg.F("Unknown action."));
        await agent.VmActionAsync(name, action);
        await repo.AuditAsync(await ActorAsync(), label, name);
    }

    public async Task CreateAsync(CreateVmRequest r)
    {
        if (Rules.ValidateCreateVm(r) is { } e) throw new AgentException(e);
        await agent.CreateVmAsync(r);
        await repo.AuditAsync(await ActorAsync(), "Virtual machine created", $"{r.Name} ({r.Vcpus} CPU, {r.MemoryMb} MB, {r.DiskGb} GB)");
    }
}
