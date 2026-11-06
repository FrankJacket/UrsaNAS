namespace Nas.Contracts;

/// <summary>Compteurs cumulés d'un disque entier (depuis le démarrage) : l'interface en déduit les débits entre deux lectures.</summary>
public sealed record DiskIo(string Name, long ReadBytes, long WriteBytes, long BusyMs);

/// <summary>
/// Instantané de l'état de la machine. Tout est cumulé depuis le démarrage (CPU, réseau, disques) :
/// les pourcentages et débits se calculent entre deux instantanés. <c>SampleMs</c> est une horloge monotone de l'agent.
/// </summary>
public sealed record SystemStats(string Hostname, double UptimeSeconds, long CpuTotal, long CpuIdle, double? CpuTempC,
    long MemTotalBytes, long MemAvailableBytes, long NetRxBytes, long NetTxBytes, IReadOnlyList<DiskIo> Disks, long SampleMs);

/// <summary>Valeurs instantanées calculées entre deux <see cref="SystemStats"/>.</summary>
public sealed record SystemRates(double CpuPercent, double NetTxPerSec, double NetRxPerSec,
    double DiskReadPerSec, double DiskWritePerSec, IReadOnlyDictionary<string, DiskRate> Disks);

public sealed record DiskRate(double ReadPerSec, double WritePerSec, double BusyPercent);

public static class StatsMath
{
    /// <summary>Débits et pourcentages entre deux instantanés ; <c>null</c> tant qu'il n'y en a pas deux ou si l'agent a redémarré entre-temps.</summary>
    public static SystemRates? Rates(SystemStats? previous, SystemStats current)
    {
        if (previous is null) return null;
        var seconds = (current.SampleMs - previous.SampleMs) / 1000.0;
        if (seconds <= 0 || current.CpuTotal < previous.CpuTotal) return null;

        double PerSec(long now, long before) => Math.Max(0, now - before) / seconds;

        var cpuTotal = current.CpuTotal - previous.CpuTotal;
        var cpuIdle = current.CpuIdle - previous.CpuIdle;
        var cpu = cpuTotal <= 0 ? 0 : Math.Clamp(100.0 * (cpuTotal - cpuIdle) / cpuTotal, 0, 100);

        var before = previous.Disks.ToDictionary(d => d.Name);
        var disks = new Dictionary<string, DiskRate>();
        foreach (var d in current.Disks)
        {
            if (!before.TryGetValue(d.Name, out var p)) continue;
            disks[d.Name] = new DiskRate(PerSec(d.ReadBytes, p.ReadBytes), PerSec(d.WriteBytes, p.WriteBytes),
                Math.Clamp(Math.Max(0, d.BusyMs - p.BusyMs) / (seconds * 10.0), 0, 100));
        }

        return new SystemRates(cpu, PerSec(current.NetTxBytes, previous.NetTxBytes), PerSec(current.NetRxBytes, previous.NetRxBytes),
            disks.Values.Sum(d => d.ReadPerSec), disks.Values.Sum(d => d.WritePerSec), disks);
    }
}
