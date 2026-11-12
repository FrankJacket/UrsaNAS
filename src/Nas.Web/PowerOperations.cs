using Microsoft.AspNetCore.Components.Authorization;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>Arrêt et redémarrage du NAS depuis l'interface : demande à l'agent, journalise avec le nom de l'administrateur.</summary>
public class PowerOperations(AgentClient agent, Repo repo, AuthenticationStateProvider auth)
{
    public async Task PowerAsync(bool reboot)
    {
        var actor = (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? "unknown";
        try { await agent.PowerAsync(reboot); }
        catch (AgentException ex)
        {
            await repo.AuditAsync(actor, "Power request rejected", $"{(reboot ? "restart" : "shutdown")} — {ex.Message}");
            throw;
        }
        await repo.AuditAsync(actor, reboot ? "Restart requested" : "Shutdown requested", "");
    }
}
