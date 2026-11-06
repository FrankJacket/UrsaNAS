using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web.Alerting;

/// <summary>Ce que l'interface a pu observer lors d'un contrôle. <c>null</c> = on n'a pas pu le savoir (agent muet, ZFS absent).</summary>
public sealed record HealthSnapshot(bool AgentReachable, IReadOnlyList<PoolInfo>? Pools, IReadOnlyList<SmartInfo>? Smart);

/// <summary>Un problème constaté. <c>Code</c> l'identifie de façon stable d'un contrôle à l'autre (ex. <c>pool:tank:health</c>).</summary>
public sealed record Candidate(string Code, string Severity, string Title, string Detail);

public static class AlertRules
{
    public const string Critical = "critical";
    public const string Warning = "warning";

    static readonly HashSet<string> BadDeviceStates = new(StringComparer.Ordinal)
        { "UNAVAIL", "FAULTED", "OFFLINE", "REMOVED", "DEGRADED" };

    public static List<Candidate> Evaluate(HealthSnapshot s)
    {
        var result = new List<Candidate>();
        if (!s.AgentReachable)
            result.Add(new("agent", Critical, "L'agent UrsaNAS ne répond pas",
                "L'interface ne peut plus lire l'état du NAS. Vérifie le service ursanas-agent."));

        foreach (var p in s.Pools ?? [])
        {
            if (p.Health != "ONLINE")
            {
                var bad = p.Devices.Where(d => d.Depth >= 1 && BadDeviceStates.Contains(d.State)).Select(d => $"{d.Name} ({d.State})").ToList();
                var cause = bad.Count > 0 ? $" Périphérique(s) concerné(s) : {string.Join(", ", bad)}." : "";
                result.Add(new($"pool:{p.Name}:health", Critical, $"Pool {p.Name} : {p.Health}",
                    (p.Health == "DEGRADED"
                        ? "Le pool fonctionne encore mais n'est plus redondant : remplace le disque défaillant au plus vite."
                        : "Le pool est en panne ou inaccessible.") + cause));
            }

            if (!string.IsNullOrWhiteSpace(p.Errors) && !p.Errors.StartsWith("No known data errors", StringComparison.OrdinalIgnoreCase))
                result.Add(new($"pool:{p.Name}:errors", Critical, $"Pool {p.Name} : erreurs de données", p.Errors));

            foreach (var d in p.Devices.Where(d => d.Depth >= 1))
            {
                if (IsNonZero(d.Read) || IsNonZero(d.Write) || IsNonZero(d.Cksum))
                    result.Add(new($"pool:{p.Name}:dev:{d.Name}", Warning, $"Pool {p.Name} : erreurs sur {d.Name}",
                        $"Compteurs ZFS — lecture {d.Read}, écriture {d.Write}, somme de contrôle {d.Cksum}."));
            }

            if (p.SizeBytes > 0)
            {
                var pct = 100.0 * p.AllocBytes / p.SizeBytes;
                if (pct >= 90)
                    result.Add(new($"pool:{p.Name}:space", pct >= 95 ? Critical : Warning, $"Pool {p.Name} : {pct:0}% plein",
                        "Un pool presque plein ralentit et risque de refuser les écritures : libère de l'espace ou ajoute des disques."));
            }
        }

        foreach (var d in s.Smart ?? [])
        {
            if (d.Status is "failing" or "warning")
                result.Add(new($"smart:{d.Disk}", d.Status == "failing" ? Critical : Warning,
                    $"Disque {d.Disk} : SMART {(d.Status == "failing" ? "en échec" : "à surveiller")}", d.Detail ?? ""));
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
