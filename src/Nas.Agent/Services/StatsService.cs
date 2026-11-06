using System.Globalization;
using System.Text.RegularExpressions;
using Nas.Contracts;

namespace Nas.Agent.Services;

public interface IStatsProvider
{
    SystemStats Read();
}

/// <summary>Lecture de /proc et /sys (lecture seule, aucune commande lancée). Les analyseurs sont statiques pour être testés sur du texte.</summary>
public static partial class StatsParser
{
    /// <summary>Première ligne de /proc/stat : « cpu user nice system idle iowait irq softirq steal … » (en ticks).</summary>
    public static (long Total, long Idle) ParseCpu(string procStat)
    {
        var line = procStat.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null) return (0, 0);
        var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(s => long.TryParse(s, out var n) ? n : 0).ToArray();
        if (v.Length < 4) return (0, 0);
        // user nice system idle iowait irq softirq steal : l'inactivité compte idle et iowait.
        return (v.Sum(), v[3] + (v.Length > 4 ? v[4] : 0));
    }

    public static (long Total, long Available) ParseMemory(string meminfo)
    {
        long total = 0, available = 0, free = 0, buffers = 0, cached = 0;
        foreach (var l in meminfo.Split('\n'))
        {
            var parts = l.Split(':', 2);
            if (parts.Length != 2) continue;
            var digits = new string(parts[1].Where(char.IsDigit).ToArray());
            if (!long.TryParse(digits, out var kb)) continue;
            switch (parts[0])
            {
                case "MemTotal": total = kb * 1024; break;
                case "MemAvailable": available = kb * 1024; break;
                case "MemFree": free = kb * 1024; break;
                case "Buffers": buffers = kb * 1024; break;
                case "Cached": cached = kb * 1024; break;
            }
        }
        return (total, available > 0 ? available : free + buffers + cached);
    }

    /// <summary>Somme des octets reçus/émis par les interfaces physiques (hors lo, docker, veth, ponts…).</summary>
    public static (long Rx, long Tx) ParseNet(string procNetDev)
    {
        long rx = 0, tx = 0;
        foreach (var l in procNetDev.Split('\n').Skip(2))
        {
            var colon = l.IndexOf(':');
            if (colon < 0) continue;
            var name = l[..colon].Trim();
            if (IgnoredInterface().IsMatch(name)) continue;
            var f = l[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 9) continue;
            if (long.TryParse(f[0], out var r)) rx += r;
            if (long.TryParse(f[8], out var t)) tx += t;
        }
        return (rx, tx);
    }

    [GeneratedRegex("^(lo|docker\\d*|veth.*|br-.*|virbr.*|tun\\d*|tap.*)$")]
    private static partial Regex IgnoredInterface();

    [GeneratedRegex("^(sd[a-z]+|vd[a-z]+|hd[a-z]+|xvd[a-z]+|nvme\\d+n\\d+|mmcblk\\d+)$")]
    private static partial Regex WholeDisk();

    /// <summary>/proc/diskstats : champs 6 (secteurs lus), 10 (secteurs écrits) et 13 (ms passées à travailler), secteurs de 512 octets.</summary>
    public static IReadOnlyList<DiskIo> ParseDisks(string diskstats)
    {
        var result = new List<DiskIo>();
        foreach (var l in diskstats.Split('\n'))
        {
            var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 13 || !WholeDisk().IsMatch(f[2])) continue;
            if (long.TryParse(f[5], out var rs) && long.TryParse(f[9], out var ws) && long.TryParse(f[12], out var busy))
                result.Add(new DiskIo(f[2], rs * 512, ws * 512, busy));
        }
        return result;
    }

    /// <summary>Premier nombre du fichier de température (millidegrés) converti en °C ; <c>null</c> si absent ou absurde.</summary>
    public static double? ParseMilliDegrees(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m is > 0 and < 150_000 ? m / 1000.0 : null;
}

public sealed class ProcStatsProvider(string procRoot = "/proc", string sysRoot = "/sys") : IStatsProvider
{
    // Capteurs CPU connus ; à défaut, la première zone thermique du noyau.
    static readonly string[] CpuSensors = ["coretemp", "k10temp", "zenpower", "cpu_thermal", "cpu-thermal", "x86_pkg_temp"];

    public SystemStats Read()
    {
        var (cpuTotal, cpuIdle) = StatsParser.ParseCpu(Text("stat"));
        var (memTotal, memAvailable) = StatsParser.ParseMemory(Text("meminfo"));
        var (rx, tx) = StatsParser.ParseNet(Text("net/dev"));
        var uptime = double.TryParse(Text("uptime").Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var u) ? u : 0;
        var host = Text("sys/kernel/hostname").Trim();
        if (host.Length == 0) host = Environment.MachineName;
        return new SystemStats(host, uptime, cpuTotal, cpuIdle, CpuTemperature(), memTotal, memAvailable, rx, tx,
            StatsParser.ParseDisks(Text("diskstats")), Environment.TickCount64);
    }

    string Text(string rel)
    {
        try { return File.ReadAllText(Path.Combine(procRoot, rel)); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    double? CpuTemperature()
    {
        try
        {
            var hwmon = Path.Combine(sysRoot, "class/hwmon");
            if (Directory.Exists(hwmon))
                foreach (var dir in Directory.EnumerateDirectories(hwmon))
                {
                    var name = File.Exists(Path.Combine(dir, "name")) ? File.ReadAllText(Path.Combine(dir, "name")).Trim() : "";
                    if (!CpuSensors.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    var values = Directory.EnumerateFiles(dir, "temp*_input").Select(f => StatsParser.ParseMilliDegrees(File.ReadAllText(f))).OfType<double>().ToList();
                    if (values.Count > 0) return values.Max();
                }
            var zones = Path.Combine(sysRoot, "class/thermal");
            if (Directory.Exists(zones))
                foreach (var dir in Directory.EnumerateDirectories(zones, "thermal_zone*"))
                    if (StatsParser.ParseMilliDegrees(File.ReadAllText(Path.Combine(dir, "temp"))) is { } t) return t;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}

/// <summary>Valeurs fictives qui bougent (URSANAS_FAKE=1), pour développer le tableau de bord sans Linux.</summary>
public sealed class FakeStatsProvider : IStatsProvider
{
    static readonly long Started = Environment.TickCount64;

    public SystemStats Read()
    {
        var t = Environment.TickCount64;
        var s = (t - Started) / 1000.0; // depuis le lancement : des compteurs raisonnables
        long Grow(double perSec) => (long)(s * perSec);
        var wave = 0.5 + 0.5 * Math.Sin(s / 4);
        return new SystemStats("papageno", 86_412 + s, Grow(400), Grow(260),
            41 + 6 * wave, 16L << 30, (long)((16L << 30) * (0.7 - 0.05 * wave)), Grow(250_000), Grow(60_000),
            [new DiskIo("sda", Grow(1_000), Grow(40_000), (long)(s * 30)), new DiskIo("sdb", Grow(2_000_000), Grow(90_000), (long)(s * 300))], t);
    }
}
