using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Partitionner, formater, monter et effacer les disques, et agrandir le disque système. Les opérations destructrices ne touchent que des
/// disques libres (ni système, ni monté, ni swap en service, ni pool ZFS actif) : <see cref="DiskInfo.Busy"/> fait foi, relu à chaque appel.
/// Aucun chemin n'est accepté qui ne soit pas un disque ou une partition (/dev/sdX, vdX, nvmeXnY) ; rien ne passe par un shell.
/// </summary>
public sealed class DiskService(ICommandRunner run, IDiskProvider disks, IConfiguration cfg, ZfsService zfs)
{
    readonly SemaphoreSlim _gate = new(1, 1);

    string Fstab => cfg["Disks:Fstab"] ?? "/etc/fstab";
    string MountRoot => (cfg["Disks:MountRoot"] ?? "/mnt").TrimEnd('/');
    string[] BinDirs => cfg["Disks:BinDirs"]?.Split(':') ?? ["/usr/sbin", "/sbin", "/usr/bin", "/bin"];
    bool NoWait => cfg.GetValue("Disks:NoWait", false);
    const string Marker = "# ursanas";

    static readonly Dictionary<string, string> MkfsTool = new() { ["ext4"] = "mkfs.ext4", ["btrfs"] = "mkfs.btrfs", ["zfs"] = "zpool", ["exfat"] = "mkfs.exfat" };

    // ---- Outils disponibles

    bool HasTool(string tool) => BinDirs.Any(d => File.Exists(Path.Combine(d, tool)));

    public FsOptions Filesystems() => new(Rules.FileSystems.Where(f => HasTool(MkfsTool[f])).ToList());

    // ---- Recherche

    async Task<DiskInfo> DiskAsync(string path, CancellationToken ct)
    {
        if (!Rules.IsDiskPath(path)) throw new OperationException(Msg.F("Unknown disk."));
        return (await disks.ListAsync(ct)).FirstOrDefault(d => d.Path == path) ?? throw new OperationException(Msg.F("Unknown disk."));
    }

    async Task<(DiskInfo Disk, PartitionInfo Part)> PartitionAsync(string path, CancellationToken ct)
    {
        if (!Rules.IsPartitionPath(path)) throw new OperationException(Msg.F("Unknown partition."));
        foreach (var d in await disks.ListAsync(ct))
            if (d.Partitions?.FirstOrDefault(p => p.Path == path) is { } p) return (d, p);
        throw new OperationException(Msg.F("Unknown partition."));
    }

    static void RequireFree(DiskInfo d)
    {
        if (d.Busy is not null) throw new OperationException(d.Busy);
    }

    async Task<string> Ok(string file, IReadOnlyList<string> args, string what, CancellationToken ct, string? stdin = null)
    {
        CommandResult r;
        try { r = await run.RunAsync(file, args, stdin, ct); }
        catch (Win32Exception) { throw new OperationException(Msg.F("The program {0} is not installed on the NAS.", file)); }
        if (r.ExitCode == 0) return r.StdOut;
        var msg = (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim();
        throw new OperationException(Msg.F("{0} failed: {1}", what, msg));
    }

    async Task Mutate(Func<Task> action)
    {
        await _gate.WaitAsync();
        try { await action(); }
        finally { _gate.Release(); }
    }

    static string PartitionPath(string disk, int n) => disk + (char.IsDigit(disk[^1]) ? "p" : "") + n.ToString(CultureInfo.InvariantCulture);

    async Task WaitForNodeAsync(string path, CancellationToken ct)
    {
        await Ok("udevadm", ["settle"], "Waiting for the disk", ct);
        if (NoWait) return;
        for (var i = 0; i < 50 && !File.Exists(path); i++) await Task.Delay(100, ct);
        if (!File.Exists(path)) throw new OperationException(Msg.F("The new partition did not appear.", path));
    }

    // ---- Effacer, partitionner, formater

    async Task WipeAsync(DiskInfo d, CancellationToken ct)
    {
        foreach (var p in d.Partitions ?? []) await Ok("wipefs", ["--all", "--force", p.Path], "Erasing the disk", ct);
        await Ok("wipefs", ["--all", "--force", d.Path], "Erasing the disk", ct);
    }

    public Task WipeDiskAsync(string disk, CancellationToken ct = default) => Mutate(async () =>
    {
        var d = await DiskAsync(disk, ct);
        RequireFree(d);
        await WipeAsync(d, ct);
        await Ok("blockdev", ["--rereadpt", d.Path], "Erasing the disk", ct);
    });

    async Task MakeFsAsync(string part, string fs, string? label, CancellationToken ct)
    {
        if (!HasTool(MkfsTool[fs])) throw new OperationException(Msg.F("The program {0} is not installed on the NAS.", MkfsTool[fs]));
        if (fs == "zfs")
        {
            // Pas de formatage ZFS sur une partition : on y crée un pool (simple volume) nommé par l'étiquette.
            await zfs.CreatePoolAsync(new CreatePoolRequest(label ?? "", "", [part]), ct);
            return;
        }
        var l = string.IsNullOrEmpty(label) ? [] : new[] { "-L", label };
        IReadOnlyList<string> args = fs switch
        {
            "ext4" => ["-F", .. l, part],
            "btrfs" => ["-f", .. l, part],
            _ => [.. l, part], // exfat
        };
        await Ok(MkfsTool[fs], args, "Formatting", ct);
    }

    /// <summary>Efface un disque libre, y crée une table GPT avec une seule partition occupant tout l'espace, et la formate.</summary>
    public Task PrepareDiskAsync(PrepareDiskRequest r, CancellationToken ct = default)
    {
        CheckFs(r.Fs, r.Label);
        return Mutate(async () =>
        {
            var d = await DiskAsync(r.Disk, ct);
            RequireFree(d);
            if (!Filesystems().Available.Contains(r.Fs)) throw new OperationException(Msg.F("The program {0} is not installed on the NAS.", MkfsTool[r.Fs]));
            await WipeAsync(d, ct);
            await Ok("sfdisk", ["--wipe", "always", "--wipe-partitions", "always", d.Path], "Partitioning the disk", ct, stdin: "label: gpt\n,,L\n");
            var part = PartitionPath(d.Path, 1);
            await WaitForNodeAsync(part, ct);
            await MakeFsAsync(part, r.Fs, r.Label, ct);
        });
    }

    /// <summary>Crée une partition dans l'espace non alloué (même sur le disque système : rien n'est effacé) et la formate.</summary>
    public Task CreatePartitionAsync(CreatePartitionRequest r, CancellationToken ct = default)
    {
        CheckFs(r.Fs, r.Label);
        if (r.SizeMb is < 64) throw new OperationException(Msg.F("The partition must be at least 64 MB."));
        return Mutate(async () =>
        {
            var d = await DiskAsync(r.Disk, ct);
            if (!Filesystems().Available.Contains(r.Fs)) throw new OperationException(Msg.F("The program {0} is not installed on the NAS.", MkfsTool[r.Fs]));
            if (d.Partitions is not { Count: > 0 }) throw new OperationException(Msg.F("This disk has no partition table: use “Partition and format”."));
            var freeMb = d.UnallocatedBytes >> 20;
            if (freeMb < 64) throw new OperationException(Msg.F("There is no unallocated space on this disk."));
            if (r.SizeMb > freeMb) throw new OperationException(Msg.F("Only {0} MB are unallocated on this disk.", freeMb));
            var used = d.Partitions.Select(p => new string(p.Path.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray())).Where(s => s.Length > 0).Select(int.Parse).ToHashSet();
            var number = Enumerable.Range(1, 200).First(n => !used.Contains(n)); // sfdisk prend le premier numéro libre
            var size = r.SizeMb is { } mb ? mb.ToString(CultureInfo.InvariantCulture) + "M" : "";
            await Ok("sfdisk", ["--append", "--no-reread", d.Path], "Partitioning the disk", ct, stdin: $",{size},L\n");
            await Ok("partx", ["-u", d.Path], "Partitioning the disk", ct);
            var part = PartitionPath(d.Path, number);
            await WaitForNodeAsync(part, ct);
            await MakeFsAsync(part, r.Fs, r.Label, ct);
        });
    }

    static void CheckFs(string fs, string? label)
    {
        if (Rules.ValidateFormat(fs, label) is { } e) throw new OperationException(e);
    }

    public Task FormatPartitionAsync(FormatPartitionRequest r, CancellationToken ct = default)
    {
        CheckFs(r.Fs, r.Label);
        return Mutate(async () =>
        {
            var (disk, part) = await PartitionAsync(r.Partition, ct);
            if (part.MountPoints.Count > 0) throw new OperationException(Msg.F("A partition is mounted: {0}.", string.Join(", ", part.MountPoints)));
            if (part.Busy is not null) throw new OperationException(part.Busy);
            await Ok("wipefs", ["--all", "--force", part.Path], "Erasing the disk", ct);
            await MakeFsAsync(part.Path, r.Fs, r.Label, ct);
        });
    }

    // ---- Montage

    async Task<string?> NasGidAsync(CancellationToken ct)
    {
        try
        {
            var r = await run.RunAsync("getent", ["group", UserService.Group], ct: ct);
            var f = r.StdOut.Trim().Split(':');
            return r.ExitCode == 0 && f.Length > 2 && f[2].All(char.IsDigit) ? f[2] : null;
        }
        catch (Win32Exception) { return null; }
    }

    static string MountOptions(string fs, string? gid) =>
        fs is "exfat" or "ntfs" or "vfat" ? "uid=0," + (gid is null ? "" : $"gid={gid},") + "umask=0007" : "defaults";

    public Task MountAsync(MountPartitionRequest r, CancellationToken ct = default)
    {
        if (Rules.ValidateMountName(r.Name) is { } e) throw new OperationException(e);
        return Mutate(async () =>
        {
            var (_, part) = await PartitionAsync(r.Partition, ct);
            if (part.FsType is null or "swap" or "zfs_member" or "LVM2_member" or "linux_raid_member") throw new OperationException(Msg.F("This partition has no file system that can be mounted."));
            if (part.MountPoints.Count > 0) throw new OperationException(Msg.F("A partition is mounted: {0}.", string.Join(", ", part.MountPoints)));
            var target = $"{MountRoot}/{r.Name}";
            if ((await disks.ListAsync(ct)).Any(d => d.MountPoints.Contains(target))) throw new OperationException(Msg.F("“{0}” is already used as a mount folder.", r.Name));
            var gid = await NasGidAsync(ct);
            Directory.CreateDirectory(target);
            await Ok("mount", ["-o", MountOptions(part.FsType, gid), part.Path, target], "Mounting", ct);
            if (part.FsType is "ext4" or "xfs" or "btrfs")
            {
                await Ok("chgrp", [UserService.Group, target], "Mounting", ct);
                await Ok("chmod", ["2770", target], "Mounting", ct);
            }
            if (r.Persistent)
            {
                if (part.Uuid is null) throw new OperationException(Msg.F("This partition has no identifier: it cannot be mounted at every start."));
                var type = part.FsType == "ntfs" ? "ntfs3" : part.FsType;
                EditFstab(lines => [.. lines.Where(l => !IsOurs(l, target)), $"UUID={part.Uuid} {target} {type} {MountOptions(part.FsType, gid)},nofail 0 2 {Marker}"]);
            }
        });
    }

    public Task UnmountAsync(string mountPoint, CancellationToken ct = default)
    {
        if (!mountPoint.StartsWith(MountRoot + "/", StringComparison.Ordinal) || mountPoint.Split('/').Contains("..") || mountPoint.Any(char.IsControl))
            throw new OperationException(Msg.F("Only folders mounted under {0} can be unmounted here.", MountRoot));
        return Mutate(async () =>
        {
            if (!(await disks.ListAsync(ct)).Any(d => d.MountPoints.Contains(mountPoint))) throw new OperationException(Msg.F("Nothing is mounted there."));
            await Ok("umount", [mountPoint], "Unmounting", ct);
            EditFstab(lines => lines.Where(l => !IsOurs(l, mountPoint)).ToList());
            try { Directory.Delete(mountPoint); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // seulement s'il est vide
        });
    }

    static bool IsOurs(string line, string target)
    {
        var f = line.Split((char)32, (char)9, StringSplitOptions.RemoveEmptyEntries);
        return line.Contains(Marker, StringComparison.Ordinal) && f.Length > 1 && f[1] == target;
    }

    void EditFstab(Func<List<string>, IReadOnlyList<string>> edit)
    {
        var lines = File.Exists(Fstab) ? File.ReadAllLines(Fstab).ToList() : [];
        var backup = Fstab + ".ursanas-bak";
        if (File.Exists(Fstab) && !File.Exists(backup)) File.Copy(Fstab, backup);
        var tmp = Fstab + ".ursanas-tmp";
        File.WriteAllLines(tmp, edit(lines));
        File.Move(tmp, Fstab, overwrite: true);
    }

    // ---- Agrandir le disque système

    public sealed record Part(string Node, long Start, long Size, string Type);

    /// <summary>Lit <c>sfdisk --json</c> : partitions (secteurs) et dernier secteur utilisable.</summary>
    public static (List<Part> Parts, long LastLba) ParseTable(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var t = doc.RootElement.GetProperty("partitiontable");
        var parts = t.TryGetProperty("partitions", out var ps)
            ? ps.EnumerateArray().Select(p => new Part(p.GetProperty("node").GetString() ?? "", p.GetProperty("start").GetInt64(), p.GetProperty("size").GetInt64(),
                p.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "")).ToList()
            : [];
        return (parts, t.TryGetProperty("lastlba", out var l) ? l.GetInt64() : 0);
    }

    /// <summary>
    /// Étend la partition racine à tout l'espace libre du disque (après un agrandissement du disque virtuel, par exemple), puis son
    /// système de fichiers. Si une partition de swap la suit, elle est supprimée et remplacée par un fichier /swapfile de même taille.
    /// </summary>
    public Task GrowSystemAsync(CancellationToken ct = default) => Mutate(async () =>
    {
        var src = (await Ok("findmnt", ["-no", "SOURCE", "/"], "Reading the system disk", ct)).Trim();
        var fs = (await Ok("findmnt", ["-no", "FSTYPE", "/"], "Reading the system disk", ct)).Trim();
        if (fs is not ("ext4" or "xfs")) throw new OperationException(Msg.F("Only ext4 and XFS system partitions can be extended."));
        var (disk, root) = await PartitionAsync(src, ct);
        var nstr = new string(root.Path.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        var number = int.Parse(nstr, CultureInfo.InvariantCulture);

        var (parts, lastLba) = ParseTable(await Ok("sfdisk", ["--json", disk.Path], "Reading the partition table", ct));
        var self = parts.FirstOrDefault(p => p.Node == root.Path) ?? throw new OperationException(Msg.F("Unknown partition."));
        var after = parts.Where(p => p.Start > self.Start).OrderBy(p => p.Start).ToList();
        var swap = after.Count == 1 && disk.Partitions?.FirstOrDefault(p => p.Path == after[0].Node) is { FsType: "swap" } sw ? sw : null;
        if (after.Count > 0 && swap is null) throw new OperationException(Msg.F("Another partition follows the system partition: it cannot be extended automatically."));
        var gain = lastLba + 1 - (after.Count == 0 ? self.Start + self.Size : after[0].Start + after[0].Size) + (swap?.SizeBytes / 512 ?? 0);
        if (gain < 2 * 1024 * 1024 / 512L) throw new OperationException(Msg.F("There is no unallocated space after the system partition."));

        if (swap is not null)
        {
            // Le swap doit disparaître avant que la racine puisse grandir ; il revient plus bas sous forme de fichier.
            await Ok("swapoff", [swap.Path], "Extending the system partition", ct);
            EditFstab(lines => lines.Where(l => !MentionsSwap(l, swap)).ToList());
            await Ok("wipefs", ["--all", "--force", swap.Path], "Extending the system partition", ct);
            await Ok("sfdisk", ["--no-reread", "--delete", disk.Path, after[0].Node[disk.Path.Length..].TrimStart('p')], "Extending the system partition", ct);
        }
        await Ok("sfdisk", ["--no-reread", "--force", "-N", number.ToString(CultureInfo.InvariantCulture), disk.Path], "Extending the system partition", ct, stdin: ",+\n");
        await Ok("partx", ["-u", disk.Path], "Extending the system partition", ct);
        await Ok("udevadm", ["settle"], "Extending the system partition", ct);
        if (fs == "ext4") await Ok("resize2fs", [root.Path], "Extending the file system", ct);
        else await Ok("xfs_growfs", ["/"], "Extending the file system", ct);

        if (swap is not null)
        {
            var swapfile = "/swapfile";
            var size = Math.Max(swap.SizeBytes, 512L << 20).ToString(CultureInfo.InvariantCulture);
            await Ok("fallocate", ["-l", size, swapfile], "Recreating the swap space", ct);
            await Ok("chmod", ["600", swapfile], "Recreating the swap space", ct);
            await Ok("mkswap", [swapfile], "Recreating the swap space", ct);
            await Ok("swapon", [swapfile], "Recreating the swap space", ct);
            EditFstab(lines => [.. lines, $"{swapfile} none swap sw 0 0 {Marker}"]);
        }
    });

    static bool MentionsSwap(string line, PartitionInfo swap)
    {
        if (line.TrimStart().StartsWith('#')) return false;
        var f = line.Split((char)32, (char)9, StringSplitOptions.RemoveEmptyEntries);
        return f.Length > 2 && f[2] == "swap" && (f[0] == swap.Path || (swap.Uuid is not null && f[0] == "UUID=" + swap.Uuid) || (swap.Label is not null && f[0] == "LABEL=" + swap.Label));
    }
}
