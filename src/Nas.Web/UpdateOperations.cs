using Microsoft.AspNetCore.Components.Authorization;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>Réglages et recherche de mises à jour depuis l'interface : demande à l'agent, journalise avec le nom de l'administrateur (jamais le jeton).</summary>
public class UpdateOperations(AgentClient agent, Repo repo, AuthenticationStateProvider auth)
{
    async Task<string> ActorAsync() => (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? "unknown";

    public async Task SaveSettingsAsync(string channel, string? token, bool clearToken)
    {
        var actor = await ActorAsync();
        var tokenValue = clearToken ? "" : string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        try { await agent.SetUpdateSettingsAsync(new SetUpdateSettingsRequest(channel, tokenValue)); }
        catch (AgentException ex)
        {
            await repo.AuditAsync(actor, "Update settings rejected", ex.Message);
            throw;
        }
        await repo.AuditAsync(actor, "Update settings changed", $"channel {channel}" + (clearToken ? ", access token removed" : tokenValue is null ? "" : ", access token replaced"));
    }

    public async Task InstallAppAsync(string version)
    {
        var actor = await ActorAsync();
        try { await agent.InstallAppUpdateAsync(); }
        catch (AgentException ex)
        {
            await repo.AuditAsync(actor, "UrsaNAS update rejected", $"{version} — {ex.Message}");
            throw;
        }
        await repo.AuditAsync(actor, "UrsaNAS update started", version);
    }

    public async Task InstallPackagesAsync(IReadOnlyList<string> names)
    {
        var actor = await ActorAsync();
        var list = string.Join(", ", names);
        try { await agent.InstallPackagesAsync(names); }
        catch (AgentException ex)
        {
            await repo.AuditAsync(actor, "Package update rejected", $"{list} — {ex.Message}");
            throw;
        }
        await repo.AuditAsync(actor, "Packages updated", list);
    }

    public Task<UpdateScheduleSettings> GetScheduleAsync() => repo.GetUpdateScheduleAsync();

    public async Task SaveScheduleAsync(UpdateScheduleSettings s)
    {
        if (!Rules.UpdateCheckFrequencies.Contains(s.CheckFrequency)) throw new AgentException(Msg.F("Unknown check frequency “{0}”.", s.CheckFrequency));
        if (!Rules.IsTimeOfDay(s.CheckTime)) throw new AgentException(Msg.F("“{0}” is not a time (HH:mm).", s.CheckTime));
        if (!Rules.IsTimeOfDay(s.AutoInstallTime)) throw new AgentException(Msg.F("“{0}” is not a time (HH:mm).", s.AutoInstallTime));
        var actor = await ActorAsync();
        await repo.SaveUpdateScheduleAsync(s);
        await repo.AuditAsync(actor, "Update schedule changed",
            $"check {s.CheckFrequency}" + (s.CheckFrequency == "weekly" ? $" {s.CheckDay}" : "") + $" {s.CheckTime}; " +
            $"auto-install: UrsaNAS {(s.AutoInstallApp ? "on" : "off")}, packages {(s.AutoInstallPackages ? "on" : "off")}" +
            (s.AutoInstallApp || s.AutoInstallPackages ? $" ({s.AutoInstallDay} {s.AutoInstallTime})" : "") +
            $", reboot {(s.AutoReboot ? "on" : "off")}");
    }
}
