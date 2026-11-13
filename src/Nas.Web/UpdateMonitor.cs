using Microsoft.Extensions.DependencyInjection;
using Nas.Contracts;

namespace Nas.Web;

/// <summary>
/// Passe toutes les quelques minutes : lance la vérification et l'installation automatique des mises à jour quand leur créneau planifié
/// arrive (heure du serveur). Le dernier passage reste en mémoire seulement (comme <see cref="BackupMonitor"/>) : un redémarrage de
/// l'interface peut au pire relancer un créneau déjà passé, sans gravité.
/// </summary>
public sealed class UpdateMonitor(IServiceScopeFactory scopes, IConfiguration cfg, ILogger<UpdateMonitor> log) : BackgroundService
{
    DateTime _lastCheckRunUtc = DateTime.MinValue;
    DateTime _lastAutoInstallRunUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(cfg.GetValue("Updates:StartDelaySeconds", 30)), ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(cfg.GetValue("Updates:IntervalSeconds", 300)));
        do
        {
            try { await TickAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Scheduled update tick failed"); }
        }
        while (await WaitAsync(timer, ct));
    }

    static async Task<bool> WaitAsync(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var scheduler = scope.ServiceProvider.GetRequiredService<UpdateScheduler>();
        var s = await scheduler.GetSettingsAsync();
        var now = DateTime.Now; // heure du serveur : celle affichée et réglée depuis l'interface

        if (IsCheckDue(s, now, _lastCheckRunUtc))
        {
            _lastCheckRunUtc = DateTime.UtcNow;
            await scheduler.RunCheckAsync(ct);
        }

        if (IsAutoInstallDue(s, now, _lastAutoInstallRunUtc))
        {
            _lastAutoInstallRunUtc = DateTime.UtcNow;
            await scheduler.RunAutoInstallAsync(s, ct);
        }
    }

    /// <summary>Le créneau de vérification est atteint aujourd'hui (selon la fréquence) et n'a pas encore été traité.</summary>
    public static bool IsCheckDue(UpdateScheduleSettings s, DateTime now, DateTime lastRunUtc)
    {
        if (s.CheckFrequency == "off" || !TimeSpan.TryParse(s.CheckTime, out var tod)) return false;
        if (s.CheckFrequency == "weekly" && now.DayOfWeek != s.CheckDay) return false;
        var slot = now.Date + tod;
        return now >= slot && lastRunUtc.ToLocalTime() < slot;
    }

    /// <summary>Le créneau d'installation automatique (hebdomadaire) est atteint et n'a pas encore été traité ; rien n'est planifié si les deux cases sont décochées.</summary>
    public static bool IsAutoInstallDue(UpdateScheduleSettings s, DateTime now, DateTime lastRunUtc)
    {
        if (!s.AutoInstallApp && !s.AutoInstallPackages) return false;
        if (now.DayOfWeek != s.AutoInstallDay || !TimeSpan.TryParse(s.AutoInstallTime, out var tod)) return false;
        var slot = now.Date + tod;
        return now >= slot && lastRunUtc.ToLocalTime() < slot;
    }
}
