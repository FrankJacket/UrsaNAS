using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>
/// Les deux actions planifiées qu'<see cref="UpdateMonitor"/> déclenche en leur temps : une vérification fraîche, et l'installation
/// automatique de ce qu'elle a trouvé. Toujours journalisées sous l'acteur « system » : aucun administrateur n'est à l'origine du geste.
/// </summary>
public sealed class UpdateScheduler(AgentClient agent, Repo repo, ILogger<UpdateScheduler> log)
{
    public Task<UpdateScheduleSettings> GetSettingsAsync() => repo.GetUpdateScheduleAsync();

    /// <summary>Relance la recherche de mises à jour (UrsaNAS et paquets) ; le prochain passage d'<see cref="UpdateMonitor"/> verra le résultat.</summary>
    public async Task RunCheckAsync(CancellationToken ct)
    {
        try
        {
            var app = await agent.CheckAppUpdateAsync(ct);
            var pkgs = await agent.CheckPackageUpdatesAsync(ct);
            var tracked = pkgs.Items.Count(p => p.Tracked);
            await repo.AuditAsync("system", "Scheduled update check",
                (app.Available ? Msg.F("A new version of UrsaNAS is available: {0}", app.Latest ?? "") : "UrsaNAS is up to date.")
                + " " + (tracked > 0 ? Msg.F("{0} package update(s) available", tracked) : "No package update available."));
        }
        catch (Exception e) when (e is AgentException or HttpRequestException)
        {
            log.LogWarning(e, "Scheduled update check failed");
        }
    }

    /// <summary>Installe ce que la dernière vérification a trouvé, selon les réglages ; redémarre ensuite si demandé et nécessaire.</summary>
    public async Task RunAutoInstallAsync(UpdateScheduleSettings s, CancellationToken ct)
    {
        UpdateStatus status;
        try { status = await agent.GetUpdateStatusAsync(ct); }
        catch (Exception e) when (e is AgentException or HttpRequestException) { log.LogWarning(e, "Scheduled auto-install skipped (agent unreachable)"); return; }

        if (s.AutoInstallApp && status.App is { Available: true, Latest: not null } app)
        {
            try
            {
                await agent.InstallAppUpdateAsync(ct);
                await repo.AuditAsync("system", "UrsaNAS update started", app.Latest);
            }
            catch (AgentException e) { await repo.AuditAsync("system", "UrsaNAS update rejected", $"{app.Latest} — {e.Message}"); }
        }

        if (s.AutoInstallPackages && status.Packages is { } pkgs)
        {
            var names = pkgs.Items.Where(p => p.Tracked && (s.AutoInstallManualPackages || !p.ManualOnly)).Select(p => p.Name).ToList();
            if (names.Count == 0) return;
            try
            {
                await agent.InstallPackagesAsync(names, ct);
                await repo.AuditAsync("system", "Packages updated", string.Join(", ", names));
            }
            catch (AgentException e)
            {
                await repo.AuditAsync("system", "Package update rejected", $"{string.Join(", ", names)} — {e.Message}");
                return;
            }
            if (!s.AutoReboot) return;
            try
            {
                var after = await agent.CheckPackageUpdatesAsync(ct); // l'installation peut avoir changé reboot-required
                if (!after.RebootRequired) return;
                await agent.PowerAsync(true, ct);
                await repo.AuditAsync("system", "Restart requested", "after the scheduled package updates");
            }
            catch (Exception e) when (e is AgentException or HttpRequestException) { log.LogWarning(e, "Scheduled restart after updates failed"); }
        }
    }
}
