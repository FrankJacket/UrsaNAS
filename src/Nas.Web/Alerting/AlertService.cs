using Nas.Contracts;
using Nas.Web.Data;
using Nas.Web.Localization;

namespace Nas.Web.Alerting;

/// <summary>Nombre d'alertes ouvertes, partagé avec l'interface (pastille du menu).</summary>
public sealed class AlertState
{
    public int Count { get; private set; }
    public int CriticalCount { get; private set; }
    public event Action? Changed;

    public void Set(int count, int critical)
    {
        if (count == Count && critical == CriticalCount) return;
        Count = count;
        CriticalCount = critical;
        Changed?.Invoke();
    }
}

/// <summary>
/// Compare l'état observé aux alertes enregistrées : ouvre, ferme, journalise et envoie les courriels.
/// Un seul traitement à la fois ; l'état du contrôle précédent sert à n'ouvrir une alerte qu'au deuxième constat.
/// </summary>
public sealed class AlertService(Repo repo, IMailSender mail, AlertState state)
{
    /// <summary>Durée pendant laquelle la réouverture d'une alerte fermée n'envoie pas de nouveau courriel.</summary>
    public static readonly TimeSpan RepeatQuiet = TimeSpan.FromHours(24);

    readonly SemaphoreSlim _gate = new(1, 1);
    IReadOnlySet<string> _previous = new HashSet<string>();

    public async Task ProcessAsync(HealthSnapshot snapshot, bool immediate = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = AlertRules.Evaluate(snapshot);
            var active = await repo.GetActiveAlertsAsync();

            var unknown = new List<string>();
            if (snapshot.Pools is null) unknown.Add("pool:");
            if (snapshot.Smart is null) unknown.Add("smart:");
            if (snapshot.Backups is null) unknown.Add("backup:");
            if (snapshot.Tasks is null) unknown.Add("task:");
            if (snapshot.Remotes is null) unknown.Add("remote:");
            // Mises à jour : une lecture ratée ou un contrôle pas encore fait n'est pas « plus de mise à jour disponible ».
            if (snapshot.Updates?.App is null or { Error: not null }) unknown.Add("update:app");
            if (snapshot.Updates?.Packages is null or { Error: not null }) unknown.Add("update:packages");

            var (raise, resolve) = AlertEngine.Step(current, active, _previous, unknown, immediate);
            _previous = current.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
            // Une alerte ouverte directement pendant la collecte n'est pas dans cet état déjà périmé : on ne la ferme pas.
            if (snapshot.CollectedUtc is { } collected) resolve.RemoveAll(a => a.RaisedAt > collected);

            foreach (var c in raise) await RaiseCoreAsync(c, ct);
            foreach (var a in resolve)
            {
                var repeat = await repo.WasResolvedRecentlyAsync(a.Code, RepeatQuiet, beforeId: a.Id);
                await repo.ResolveAlertAsync(a.Id);
                await repo.AuditAsync("system", "Alert resolved", a.Title);
                if (!repeat)
                    await NotifyAsync(Msg.F("Recovered: {0}", a.Title), "The problem is no longer observed.", ct);
            }

            var now = await repo.GetActiveAlertsAsync();
            state.Set(now.Count, now.Count(a => a.Severity == AlertRules.Critical));
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Ouvre tout de suite l'alerte d'un événement certain (l'échec d'une tâche), sans attendre deux constats ; sans effet si elle est déjà ouverte.
    /// Les contrôles périodiques la ferment ensuite quand le problème n'est plus constaté.
    /// </summary>
    public async Task RaiseNowAsync(Candidate c, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if ((await repo.GetActiveAlertsAsync()).Any(a => a.Code == c.Code)) return;
            await RaiseCoreAsync(c, ct);
            var now = await repo.GetActiveAlertsAsync();
            state.Set(now.Count, now.Count(a => a.Severity == AlertRules.Critical));
        }
        finally { _gate.Release(); }
    }

    // Une alerte qui clignote (fermée puis rouverte en moins de RepeatQuiet) n'est avisée qu'une fois : sa première ouverture
    // a déjà été envoyée, et le retour à la normale de ses ouvertures suivantes ne l'est pas non plus.
    async Task RaiseCoreAsync(Candidate c, CancellationToken ct)
    {
        var repeat = await repo.WasResolvedRecentlyAsync(c.Code, RepeatQuiet);
        await repo.RaiseAlertAsync(c);
        await repo.AuditAsync("system", "Alert", c.Title);
        if (!repeat)
            await NotifyAsync(Msg.F(c.Severity == AlertRules.Critical ? "CRITICAL: {0}" : "Warning: {0}", c.Title), c.Detail, ct);
    }

    /// <summary>Un échec d'envoi est journalisé mais ne bloque ni le contrôle ni l'alerte.</summary>
    async Task NotifyAsync(string subject, string body, CancellationToken ct)
    {
        var settings = await repo.GetMailSettingsAsync();
        if (!settings.Enabled || MailRules.Validate(settings) is not null) return;
        try
        {
            var lang = settings.Language;
            await mail.SendAsync(settings, $"[UrsaNAS] {Translator.Translate(subject, lang)}",
                $"{Translator.Translate(body, lang)}\n\n— UrsaNAS, {DateTime.Now:yyyy-MM-dd HH:mm:ss}", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await repo.AuditAsync("system", "Email not sent", $"{subject}: {e.Message}");
        }
    }

    public async Task RefreshStateAsync()
    {
        var active = await repo.GetActiveAlertsAsync();
        state.Set(active.Count, active.Count(a => a.Severity == AlertRules.Critical));
    }
}

/// <summary>Contrôle périodique de l'état du NAS (pools toutes les minutes, SMART toutes les dix minutes).</summary>
public sealed class AlertMonitor(IServiceScopeFactory scopes, AlertService alerts, IConfiguration cfg, ILogger<AlertMonitor> log) : BackgroundService
{
    readonly SemaphoreSlim _collect = new(1, 1);
    IReadOnlyList<Nas.Contracts.SmartInfo>? _smart;
    DateTime _smartAt = DateTime.MinValue;

    TimeSpan Interval => TimeSpan.FromSeconds(cfg.GetValue("Alerts:IntervalSeconds", 60));
    TimeSpan SmartEvery => TimeSpan.FromMinutes(cfg.GetValue("Alerts:SmartMinutes", 10));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await alerts.RefreshStateAsync();
        try { await Task.Delay(TimeSpan.FromSeconds(cfg.GetValue("Alerts:StartDelaySeconds", 15)), ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await CheckAsync(immediate: false, ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Alert check failed"); }
        }
        while (await WaitAsync(timer, ct));
    }

    static async Task<bool> WaitAsync(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>Contrôle immédiat demandé depuis l'interface : les problèmes constatés sont signalés tout de suite.</summary>
    public Task CheckNowAsync(CancellationToken ct = default) => CheckAsync(immediate: true, ct, forceSmart: true);

    async Task CheckAsync(bool immediate, CancellationToken ct, bool forceSmart = false)
    {
        HealthSnapshot snapshot;
        await _collect.WaitAsync(ct);
        try { snapshot = await CollectAsync(forceSmart, ct); }
        finally { _collect.Release(); }
        await alerts.ProcessAsync(snapshot, immediate, ct);
    }

    async Task<HealthSnapshot> CollectAsync(bool forceSmart, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var collectedUtc = DateTime.UtcNow;
        var agent = scope.ServiceProvider.GetRequiredService<AgentClient>();
        IReadOnlyList<Nas.Contracts.PoolInfo>? pools = null;
        try
        {
            try { pools = await agent.GetPoolsAsync(ct); }
            catch (AgentException) { } // ZFS absent ou en échec : on ne sait pas, on ne ferme rien

            if (forceSmart || DateTime.UtcNow - _smartAt >= SmartEvery)
            {
                try { _smart = await agent.GetSmartAsync(ct); }
                catch (AgentException) { _smart = null; }
                _smartAt = DateTime.UtcNow;
            }
            IReadOnlyList<BackupHealth>? backups = null;
            try { backups = await scope.ServiceProvider.GetRequiredService<BackupRepo>().GetHealthAsync(); }
            catch (Microsoft.Data.Sqlite.SqliteException) { } // base illisible : on ne sait pas, on ne ferme rien
            IReadOnlyList<RemoteStorage>? remotes = null;
            try { remotes = await agent.GetRemotesAsync(ct); }
            catch (AgentException) { } // illisibles : on ne sait pas, on ne ferme rien
            UpdateStatus? updates = null;
            try { updates = await agent.GetUpdateStatusAsync(ct); } // relu depuis le cache de l'agent : pas de nouvel appel à GitHub ou apt ici
            catch (AgentException) { }
            IReadOnlyList<TaskHealth>? scheduled = null;
            try { scheduled = await scope.ServiceProvider.GetRequiredService<ScheduledTaskRepo>().GetHealthAsync(); }
            catch (Microsoft.Data.Sqlite.SqliteException) { } // base illisible : on ne sait pas, on ne ferme rien
            return new HealthSnapshot(true, pools, _smart, backups, remotes, updates, scheduled, collectedUtc);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new HealthSnapshot(false, null, null);
        }
    }
}
