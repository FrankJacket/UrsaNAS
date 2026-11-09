using System.Text.Json;
using Nas.Contracts;

namespace Nas.Agent.Services;

public interface IDiskProvider
{
    Task<IReadOnlyList<DiskInfo>> ListAsync(CancellationToken ct = default);
}

public static class LsblkParser
{
    public static readonly string[] Args =
        ["--json", "--bytes", "--output", "NAME,PATH,SIZE,TYPE,MODEL,SERIAL,ROTA,MOUNTPOINTS,FSTYPE,LABEL,UUID"];

    /// <summary>Partitions utilisées par les pools ZFS actifs, lues dans <c>zpool status -LP</c> (chemins /dev/sdb1…).</summary>
    public static IReadOnlySet<string> PoolDevices(string zpoolStatus) =>
        zpoolStatus.Split((char)10).Select(l => l.Trim().Split((char)32, (char)9)[0]).Where(t => t.StartsWith("/dev/", StringComparison.Ordinal)).ToHashSet();

    /// <summary>Extrait les disques (type "disk") de la sortie JSON de lsblk, y compris les points de montage des partitions.</summary>
    public static IReadOnlyList<DiskInfo> Parse(string json, IReadOnlySet<string>? poolDevices = null)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<DiskInfo>();
        if (!doc.RootElement.TryGetProperty("blockdevices", out var devices)) return result;

        foreach (var d in devices.EnumerateArray())
        {
            if (Str(d, "type") != "disk") continue;
            var mounts = new List<string>();
            CollectMounts(d, mounts);
            var size = Long(d, "size");
            var parts = new List<PartitionInfo>();
            if (d.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
                foreach (var c in kids.EnumerateArray().Where(c => Str(c, "type") == "part"))
                {
                    var pm = new List<string>();
                    CollectMounts(c, pm);
                    parts.Add(new PartitionInfo(Str(c, "name") ?? "", Str(c, "path") ?? "", Long(c, "size"), Str(c, "fstype"), Str(c, "label"), Str(c, "uuid"), pm, poolDevices is not null && poolDevices.Contains(Str(c, "path") ?? "") ? Msg.F("This partition belongs to a ZFS pool.") : null));
                }
            var busy = BusyReason(Str(d, "path") ?? "", mounts, parts, poolDevices);
            var unallocated = Math.Max(0, size - parts.Sum(p => p.SizeBytes) - (2L << 20)); // 2 Mio : tables GPT et alignement
            result.Add(new DiskInfo(
                Name: Str(d, "name") ?? "",
                Path: Str(d, "path") ?? "",
                SizeBytes: size,
                Model: Str(d, "model")?.Trim(),
                Serial: Str(d, "serial")?.Trim(),
                Rotational: Bool(d, "rota"),
                MountPoints: mounts,
                Partitions: parts,
                Busy: busy,
                UnallocatedBytes: unallocated));
        }
        return result;
    }

    /// <summary>Pourquoi un disque n'est pas libre (système, monté, swap, pool ZFS actif) ; null s'il l'est.</summary>
    public static string? BusyReason(string path, IReadOnlyList<string> mounts, IReadOnlyList<PartitionInfo> parts, IReadOnlySet<string>? poolDevices)
    {
        if (mounts.Contains("/")) return Msg.F("The system is on this disk.");
        if (mounts.Any(m => m == "[SWAP]")) return Msg.F("This disk holds the swap space in use.");
        if (mounts.Count > 0) return Msg.F("A partition is mounted: {0}.", string.Join(", ", mounts));
        if (poolDevices is not null && (poolDevices.Contains(path) || parts.Any(p => poolDevices.Contains(p.Path)))) return Msg.F("This disk belongs to a ZFS pool.");
        return null;
    }

    private static void CollectMounts(JsonElement dev, List<string> into)
    {
        if (dev.TryGetProperty("mountpoints", out var mp) && mp.ValueKind == JsonValueKind.Array)
            foreach (var m in mp.EnumerateArray())
                if (m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } s) into.Add(s);
        if (dev.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
            foreach (var c in ch.EnumerateArray()) CollectMounts(c, into);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // lsblk renvoie selon la version un nombre, une chaîne ou un booléen.
    private static long Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetInt64(),
            JsonValueKind.String when long.TryParse(v.GetString(), out var n) => n,
            _ => 0,
        };
    }

    private static bool? Bool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetInt32() != 0,
            JsonValueKind.String => v.GetString() is "1" or "true",
            _ => null,
        };
    }
}

public sealed class LsblkDiskProvider(ICommandRunner runner) : IDiskProvider
{
    public async Task<IReadOnlyList<DiskInfo>> ListAsync(CancellationToken ct = default)
    {
        var r = await runner.RunAsync("lsblk", LsblkParser.Args, ct: ct);
        if (r.ExitCode != 0) throw new InvalidOperationException(Msg.F("lsblk failed ({0}): {1}", r.ExitCode, r.StdErr));
        IReadOnlySet<string> pools = new HashSet<string>();
        try
        {
            var z = await runner.RunAsync("zpool", ["status", "-LP"], ct: ct);
            if (z.ExitCode == 0) pools = LsblkParser.PoolDevices(z.StdOut);
        }
        catch (System.ComponentModel.Win32Exception) { } // pas de ZFS sur cette machine
        return LsblkParser.Parse(r.StdOut, pools);
    }
}

/// <summary>Données fictives pour développer sans disque réel (URSANAS_FAKE=1).</summary>
public sealed class FakeDiskProvider : IDiskProvider
{
    public Task<IReadOnlyList<DiskInfo>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DiskInfo>>(
        [
            new("sda", "/dev/sda", 40L << 30, "Virtual Disk", "SYS001", false, ["/", "/boot/efi", "[SWAP]"],
                [
                    new("sda1", "/dev/sda1", 512L << 20, "vfat", null, "AAAA-0001", ["/boot/efi"]),
                    new("sda2", "/dev/sda2", 20L << 30, "ext4", null, "uuid-sda2", ["/"]),
                    new("sda3", "/dev/sda3", 2L << 30, "swap", null, "uuid-sda3", ["[SWAP]"], Msg.F("A partition is mounted: {0}.", "[SWAP]")),
                ], Msg.F("The system is on this disk."), 17L << 30),
            new("sdb", "/dev/sdb", 10L << 30, "Virtual Disk", "DAT001", false, [],
                [new("sdb1", "/dev/sdb1", (10L << 30) - (2L << 20), "ext4", "data", "uuid-sdb1", [])], null, 0),
            new("sdc", "/dev/sdc", 10L << 30, "Virtual Disk", "DAT002", false, []),
            new("nvme0n1", "/dev/nvme0n1", 500L << 30, "Fake NVMe", "NVME001", false, [],
                [
                    new("nvme0n1p1", "/dev/nvme0n1p1", 200L << 30, "zfs_member", null, null, [], Msg.F("This partition belongs to a ZFS pool.")),
                    new("nvme0n1p2", "/dev/nvme0n1p2", 100L << 30, null, null, null, []),
                ], Msg.F("This disk belongs to a ZFS pool."), 200L << 30),
        ]);
}
