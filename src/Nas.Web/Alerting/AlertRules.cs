using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web.Alerting;

/// <summary>Ce que l'interface a pu observer lors d'un contrôle. <c>null</c> = on n'a pas pu le savoir (agent muet, ZFS absent).</summary>
public sealed record HealthSnapshot(bool AgentReachable, IReadOnlyList<PoolInfo>? Pools, IReadOnlyList<SmartInfo>? Smart, IReadOnlyList<BackupHealth>? Backups = null, IReadOnlyList<RemoteStorage>? Remotes = null);

/// <summary>Un problème constaté. <c>Code</c> l'identifie de façon stable d'un contrôle à l'autre (ex. <c>pool:tank:health</c>).</summary>
public sealed record Candidate(string Code, string Severity, string Title, string Detail);

public static class AlertRules
{
    public const string Critical = "critical";
    public const string Warning = "warning";

    static readonly HashSet<string> BadDeviceStates = new(StringComparer.Ordinal)
        { "UNAVAIL", "FAULTED", "OFFLINE", "REMOVED", "DEGRADED" };

    /// <summary>Jours sans copie réussie au-delà desquels une sauvegarde planifiée est signalée.</summary>
    public const int BackupStaleDays = 3;

    public static List<Candidate> Evaluate(HealthSnapshot s, DateTime? nowUtc = null)
    {
        var result = new List<Candidate>();
        if (!s.AgentReachable)
            result.Add(new("agent", Critical, "The UrsaNAS agent is not responding",
                "The interface can no longer read the NAS state. Check the ursanas-agent service."));

        foreach (var p in s.Pools ?? [])
        {
            if (p.Health != "ONLINE")
            {
                var bad = p.Devices.Where(d => d.Depth >= 1 && BadDeviceStates.Contains(d.State)).Select(d => $"{d.Name} ({d.State})").ToList();
                var sentence = p.Health == "DEGRADED"
                    ? "The pool still works but is no longer redundant: replace the failed disk as soon as possible."
                    : "The pool is down or inaccessible.";
                var detail = bad.Count > 0 ? Msg.F("{0} Affected devices: {1}.", sentence, string.Join(", ", bad)) : sentence;
                result.Add(new($"pool:{p.Name}:health", Critical, Msg.F("Pool {0}: {1}", p.Name, p.Health), detail));
            }

            if (!string.IsNullOrWhiteSpace(p.Errors) && !p.Errors.StartsWith("No known data errors", StringComparison.OrdinalIgnoreCase))
                result.Add(new($"pool:{p.Name}:errors", Critical, Msg.F("Pool {0}: data errors", p.Name), p.Errors));

            foreach (var d in p.Devices.Where(d => d.Depth >= 1))
            {
                if (IsNonZero(d.Read) || IsNonZero(d.Write) || IsNonZero(d.Cksum))
                    result.Add(new($"pool:{p.Name}:dev:{d.Name}", Warning, Msg.F("Pool {0}: errors on {1}", p.Name, d.Name),
                        Msg.F("ZFS counters — read {0}, write {1}, checksum {2}.", d.Read, d.Write, d.Cksum)));
            }

            if (p.SizeBytes > 0)
            {
                var pct = 100.0 * p.AllocBytes / p.SizeBytes;
                if (pct >= 90)
                    result.Add(new($"pool:{p.Name}:space", pct >= 95 ? Critical : Warning, Msg.F("Pool {0}: {1}% full", p.Name, pct.ToString("0")),
                        "A nearly full pool slows down and may refuse writes: free up space or add disks."));
            }
        }

        foreach (var b in (s.Backups ?? []).Where(b => b.Enabled))
        {
            if (b.LastStatus is "Failed")
                result.Add(new($"backup:{b.JobId}:failed", Critical, Msg.F("Backup {0}: the last copy failed", b.Name), "Open the Backups page to read the error."));
            else if (b.LastStatus is "CompletedWithErrors")
                result.Add(new($"backup:{b.JobId}:failed", Warning, Msg.F("Backup {0}: some files were not copied", b.Name), "Open the Backups page to see which files."));

            var since = b.LastSuccessUtc ?? b.CreatedUtc;
            if (b.ScheduleMode != BackupSchedule.Manual && (nowUtc ?? DateTime.UtcNow) - since > TimeSpan.FromDays(BackupStaleDays))
                result.Add(new($"backup:{b.JobId}:stale", Warning, Msg.F("Backup {0}: no successful copy for {1} days", b.Name, BackupStaleDays),
                    "Check that the backup disk is connected."));
        }

        // Une connexion enregistrée mais non montée : serveur éteint, réseau coupé, jeton refusé… (l'alerte est fermée dès que le montage revient)
        foreach (var r in (s.Remotes ?? []).Where(r => !r.Mounted))
            result.Add(new($"remote:{r.Name}:down", Warning, Msg.F("Remote storage {0}: not connected", r.Name),
                "The connection is saved but not mounted. Check the server and the network, then mount it again from the Remote storage page."));

        foreach (var d in s.Smart ?? [])
        {
            if (d.Status is "failing" or "warning")
                result.Add(new($"smart:{d.Disk}", d.Status == "failing" ? Critical : Warning,
                    d.Status == "failing" ? Msg.F("Disk {0}: SMART failing", d.Disk) : Msg.F("Disk {0}: SMART needs attention", d.Disk),
                    d.Detail ?? ""));
        }
        return result;
    }

    /// <summary>Les compteurs de zpool status sont du texte (« 0 », « 12 », « 1.2K »).</summary>
    static bool IsNonZero(string counter) => !string.IsNullOrEmpty(counter) && counter != "0";
}

/// <summary>Décide, d'un contrôle au suivant, quelles alertes ouvrir et lesquelles fermer.</summary>
public static class AlertEngine
{
    /// <param name="previousKeys">Codes constatés au contrôle précédent : une alerte n'est ouverte qu'après deux constats de suite,
    /// ce qui évite un courriel pour un redémarrage de service ou une lecture ratée.</param>
    /// <param name="unknownPrefixes">Familles de codes qu'on n'a pas pu observer cette fois : leurs alertes ouvertes ne sont pas fermées.</param>
    public static (List<Candidate> Raise, List<AlertRow> Resolve) Step(
        IReadOnlyList<Candidate> current, IReadOnlyCollection<AlertRow> active,
        IReadOnlySet<string> previousKeys, IReadOnlyCollection<string> unknownPrefixes, bool immediate = false)
    {
        var activeCodes = active.Select(a => a.Code).ToHashSet(StringComparer.Ordinal);
        var currentCodes = current.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);

        var raise = current.Where(c => !activeCodes.Contains(c.Code) && (immediate || previousKeys.Contains(c.Code))).ToList();
        var resolve = active.Where(a => !currentCodes.Contains(a.Code)
                                        && !unknownPrefixes.Any(p => a.Code.StartsWith(p, StringComparison.Ordinal))).ToList();
        return (raise, resolve);
    }
}
