using Microsoft.AspNetCore.Components.Authorization;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>Les changements d'adressage faits depuis l'interface : valide, demande à l'agent (qui applique puis vérifie), journalise avec le nom de l'administrateur.</summary>
public class NetworkOperations(AgentClient agent, Repo repo, AuthenticationStateProvider auth)
{
    async Task<string> ActorAsync() => (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? "unknown";

    static string Describe(NetIpConfig c) => c.Mode == "auto" ? "auto" : $"{c.Address}/{c.Netmask}" + (string.IsNullOrWhiteSpace(c.Gateway) ? "" : " via " + c.Gateway);

    public async Task EditAsync(NetEditRequest r)
    {
        if (NetRules.Validate(r) is { } e) throw new AgentException(e);
        var detail = $"{r.Iface}: IPv4 {Describe(r.Ipv4)}; IPv6 {Describe(r.Ipv6)}";
        var actor = await ActorAsync();
        try { await agent.EditNetworkAsync(r); }
        catch (AgentException ex)
        {
            await repo.AuditAsync(actor, "Network settings rejected", $"{detail} — {ex.Message}");
            throw;
        }
        await repo.AuditAsync(actor, "Network settings changed", detail);
    }
}
