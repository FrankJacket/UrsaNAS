using System.Text.RegularExpressions;

namespace Nas.Contracts;

/// <summary>Une ligne de l'arbre des périphériques d'un pool (le pool lui-même, un groupe mirror/raidz, puis chaque disque).</summary>
public sealed record PoolDevice(string Name, string State, string Read, string Write, string Cksum, int Depth);

public sealed record PoolInfo(string Name, string Health, long SizeBytes, long AllocBytes, long FreeBytes,
    string Scan, string Errors, IReadOnlyList<PoolDevice> Devices);

public sealed record DatasetInfo(string Name, long UsedBytes, long AvailBytes, long ReferBytes, string Mountpoint);

public sealed record SnapshotInfo(string Name, string Dataset, string Snapshot, long UsedBytes, DateTimeOffset Created);

/// <summary><c>Layout</c> : mirror, raidz1 ou raidz2. <c>Disks</c> : chemins <c>/dev/sdX</c> fournis par la liste des disques.</summary>
public sealed record CreatePoolRequest(string Name, string Layout, IReadOnlyList<string> Disks);

/// <summary>Nom complet : <c>pool/jeu</c> ou <c>pool/jeu/sous-jeu</c>.</summary>
public sealed record CreateDatasetRequest(string Name);

public sealed record CreateSnapshotRequest(string Dataset, string Name);

public static partial class Rules
{
    public static readonly IReadOnlyList<string> PoolLayouts = ["mirror", "raidz1", "raidz2"];

    static readonly HashSet<string> ReservedPools = new(StringComparer.OrdinalIgnoreCase)
        { "mirror", "raidz", "raidz1", "raidz2", "raidz3", "draid", "spare", "log", "cache", "special", "dedup" };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,31}$")]
    private static partial Regex PoolNameRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex DatasetSegmentRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$")]
    private static partial Regex SnapshotNameRegex();

    public static string? ValidatePoolName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !PoolNameRegex().IsMatch(name))
            return Msg.F("Invalid pool name: 1 to 32 characters (letters, digits, _ and -), starting with a letter.");
        if (ReservedPools.Contains(name) || (name.Length > 1 && name[0] is 'c' or 'C' && char.IsDigit(name[1])))
            return Msg.F("“{0}” is a name reserved by ZFS.", name);
        return null;
    }

    public static int MinDisks(string layout) => layout switch { "mirror" => 2, "raidz1" => 3, "raidz2" => 4, _ => int.MaxValue };

    public static string? ValidatePoolLayout(string? layout, int diskCount)
    {
        if (layout is null || !PoolLayouts.Contains(layout)) return Msg.F("Unknown pool type (mirror, raidz1 or raidz2).");
        if (diskCount < MinDisks(layout)) return Msg.F("Type {0} needs at least {1} disks.", layout, MinDisks(layout));
        if (diskCount > 24) return Msg.F("A pool is limited to 24 disks here.");
        return null;
    }

    /// <summary>Nom complet d'un jeu de données : pool/segment[/segment…]. Le pool seul n'est pas un jeu de données qu'on crée ou détruit.</summary>
    public static string? ValidateDatasetName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 200) return Msg.F("Invalid dataset name.");
        var parts = name.Split('/');
        if (parts.Length < 2) return Msg.F("A dataset is written pool/name.");
        if (ValidatePoolName(parts[0]) is { } e) return e;
        foreach (var p in parts.Skip(1))
            if (!DatasetSegmentRegex().IsMatch(p))
                return Msg.F("Invalid dataset name: 1 to 64 characters (letters, digits, _ and -) per level.");
        return null;
    }

    public static string? ValidateSnapshotName(string? name) =>
        string.IsNullOrEmpty(name) || !SnapshotNameRegex().IsMatch(name)
            ? Msg.F("Invalid snapshot name: 1 to 64 characters (letters, digits, . _ : and -), no spaces.")
            : null;
}
