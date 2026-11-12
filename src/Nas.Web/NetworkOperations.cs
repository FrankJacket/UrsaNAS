using Microsoft.AspNetCore.Components.Authorization;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>Les changements d'adressage faits depuis l'interface : valide, demande à l'agent (qui applique puis vérifie), journalise avec le nom de l'administrateur.</summary>
public class NetworkOperations(AgentClient agent, Repo repo, AuthenticationStateProvider auth)
{
    async Task<string> ActorAsync() => (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? "unknown";

    static string Describe(NetIpConfig c) => c.Mode == "auto" ? "auto" : $"{c.Address}/{c.Netmask}" + (string.IsNullOrWhiteSpace(c.Gateway) ? "" : " via " + c.Gateway);

    public async Task SetOvsAsync(bool enabled)
    {
        var actor = await ActorAsync();
        try { await agent.SetOvsAsync(enabled); }
        catch (AgentException ex)
        {
            await repo.AuditAsync(actor, "Open vSwitch change rejected", $"{(enabled ? "enable" : "disable")} — {ex.Message}");
            throw;
        }
        await repo.AuditAsync(actor, enabled ? "Open vSwitch enabled" : "Open vSwitch disabled", "");
    }

    /// <summary>Met une carte en sortie d'un pont Open vSwitch (<c>Enable</c>), ou retire ce pont (<c>Name</c> est alors le pont).</summary>
    public async Task UplinkAsync(bool enable, string name)
    {
        var actor = await ActorAsync();
        try { await agent.OvsUplinkAsync(new OvsUplinkRequest(enable, name)); }
        catch (AgentException ex)
        {
            await repo.AuditAsync(actor, "Open vSwitch change rejected", $"{(enable ? "uplink" : "remove bridge")} {name} — {ex.Message}");
            throw;
        }
        await repo.AuditAsync(actor, enable ? "Network card moved to Open vSwitch" : "Open vSwitch bridge removed", name);
    }

    /// <summary>Crée une agrégation de liens avec ces cartes, ou la retire (<c>Name</c> : l'agrégation).</summary>
    public async Task BondAsync(BondRequest r)
    {
        if (BondRequest.Validate(r) is { } e) throw new AgentException(e);
        var actor = await ActorAsync();
        var detail = r.Enable ? $"{string.Join(", ", r.Members)} ({r.Mode})" : r.Name;
        try { await agent.BondAsync(r); }
        catch (AgentException ex)
        {
            await repo.AuditAsync(actor, "Link aggregation change rejected", $"{detail} — {ex.Message}");
            throw;
        }
        await repo.AuditAsync(actor, r.Enable ? "Link aggregation created" : "Link aggregation removed", detail);
    }

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
