namespace Nas.Agent.Services;

/// <summary>
/// Arrêt et redémarrage de la machine. La commande part après un court délai : la réponse HTTP a le temps de revenir à l'interface avant que le système ne tombe.
/// </summary>
public class PowerService(ICommandRunner runner, IConfiguration cfg, ILogger<PowerService> log)
{
    bool Fake => cfg["URSANAS_FAKE"] == "1";
    int DelayMs => cfg.GetValue("Power:DelayMs", 2000);

    public void Schedule(bool reboot)
    {
        log.LogWarning("System {Action} requested", reboot ? "restart" : "shutdown");
        if (Fake) return;
        var args = new[] { reboot ? "reboot" : "poweroff" };
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DelayMs);
                var r = await runner.RunAsync("systemctl", args);
                if (r.ExitCode != 0) log.LogError("systemctl {Action} failed: {Error}", args[0], r.StdErr.Trim());
            }
            catch (Exception e) { log.LogError(e, "systemctl {Action} failed", args[0]); }
        });
    }
}
