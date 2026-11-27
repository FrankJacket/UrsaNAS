using System.Globalization;
using System.Text.RegularExpressions;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>Lecture de la sortie des commandes zpool et zfs (options -H -p : sans en-tête, valeurs exactes séparées par des tabulations).</summary>
public static class ZfsParser
{
    public static readonly string[] PoolListArgs = ["list", "-Hp", "-o", "name,size,alloc,free,health"];
    public static readonly string[] DatasetListArgs = ["list", "-Hp", "-t", "filesystem", "-o", "name,used,avail,refer,mountpoint"];
    public static readonly string[] SnapshotListArgs = ["list", "-Hp", "-t", "snapshot", "-s", "creation", "-o", "name,used,creation"];

    static IEnumerable<string[]> Rows(string text, int columns) =>
        text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .Where(c => c.Length >= columns);

    static long L(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static IReadOnlyList<(string Name, long Size, long Alloc, long Free, string Health)> ParsePoolList(string text) =>
        Rows(text, 5).Select(c => (c[0], L(c[1]), L(c[2]), L(c[3]), c[4].Trim())).ToList();

    public static IReadOnlyList<DatasetInfo> ParseDatasets(string text) =>
        Rows(text, 5).Select(c => new DatasetInfo(c[0], L(c[1]), L(c[2]), L(c[3]), c[4].Trim())).ToList();

    public static IReadOnlyList<SnapshotInfo> ParseSnapshots(string text) =>
        Rows(text, 3).Where(c => c[0].Contains('@')).Select(c =>
        {
            var at = c[0].IndexOf('@');
            return new SnapshotInfo(c[0], c[0][..at], c[0][(at + 1)..], L(c[1]), DateTimeOffset.FromUnixTimeSeconds(L(c[2])));
        }).ToList();

    /// <summary>
    /// Extrait de <c>zpool status</c> la ligne d'analyse (scan), la ligne d'erreurs et l'arbre des périphériques.
    /// L'indentation de chaque ligne donne la profondeur : pool, groupe mirror/raidz, disque.
    /// </summary>
    public static (string Scan, string Errors, IReadOnlyList<PoolDevice> Devices) ParseStatus(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var scan = new List<string>();
        var devices = new List<PoolDevice>();
        var errors = "";
        var section = "";

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (raw.StartsWith("  scan:", StringComparison.Ordinal)) { section = "scan"; scan.Add(trimmed["scan:".Length..].Trim()); continue; }
            if (raw.StartsWith("config:", StringComparison.Ordinal)) { section = "config"; continue; }
            if (raw.StartsWith("errors:", StringComparison.Ordinal)) { section = "errors"; errors = trimmed["errors:".Length..].Trim(); continue; }
            if (raw.Length > 0 && raw[0] != '\t' && raw[0] != ' ') { section = ""; continue; } // "pool:", "state:", "status:"…
            if (raw.Length > 0 && raw[0] == ' ' && raw.TrimStart().Contains(':') && section != "scan") { section = ""; continue; }
            if (trimmed.Length == 0) continue;

            switch (section)
            {
                case "scan": scan.Add(trimmed); break;
                case "config":
                    if (trimmed.StartsWith("NAME", StringComparison.Ordinal)) break;
                    var depth = (raw.TrimStart('\t').Length - raw.TrimStart('\t').TrimStart(' ').Length) / 2;
                    var t = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    devices.Add(new PoolDevice(t[0], t.Length > 1 ? t[1] : "", t.Length > 2 ? t[2] : "", t.Length > 3 ? t[3] : "", t.Length > 4 ? t[4] : "", depth));
                    break;
            }
        }
        return (string.Join(" — ", scan.Where(s => s.Length > 0)), errors, devices);
    }

    /// <summary>
    /// Reads the TRIM state that <c>zpool status -t</c> writes at the end of each disk line, for example “(untrimmed)” or
    /// “(42% trimmed, started at …)”. Only disks have one. Those listed under “spares” and “cache” are skipped.
    /// </summary>
    public static IReadOnlyList<TrimDevice> ParseTrim(string text)
    {
        var devices = new List<TrimDevice>();
        var inConfig = false;
        var skipped = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("config:", StringComparison.Ordinal)) { inConfig = true; continue; }
            if (raw.StartsWith("errors:", StringComparison.Ordinal)) break;
            var trimmed = raw.Trim();
            if (!inConfig || trimmed.Length == 0) continue;
            var columns = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // A heading or the pool's own line is not indented after the leading tab.
            if (raw.TrimStart('\t').Length == raw.TrimStart('\t').TrimStart(' ').Length) skipped = columns[0] is "spares" or "cache";
            if (skipped) continue;

            var m = TrimNote.Match(trimmed);
            if (!m.Success) continue;
            var note = m.Groups["note"].Value;
            var state = note == "untrimmed" ? TrimState.Untrimmed
                : note == "trim unsupported" ? TrimState.Unsupported
                : note.Contains("suspended,", StringComparison.Ordinal) ? TrimState.Suspended
                : note.Contains("completed at", StringComparison.Ordinal) ? TrimState.Completed
                : TrimState.Running;
            var sign = note.IndexOf('%');
            int? percent = sign > 0 && int.TryParse(note.AsSpan(0, sign), NumberStyles.None, CultureInfo.InvariantCulture, out var done) ? done : null;
            devices.Add(new TrimDevice(columns[0], columns.Length > 1 && columns[1] is "ONLINE" or "DEGRADED", state, percent));
        }
        return devices;
    }

    static readonly Regex TrimNote = new(@"\((?<note>untrimmed|trim unsupported|trimming|\d+% trimmed[^)]*)\)", RegexOptions.CultureInvariant);
}

/// <summary>
/// Pools, jeux de données et instantanés ZFS. Chaque opération valide ses entrées puis lance zpool/zfs sans shell.
/// Jamais d'option -f ni -r : ZFS refuse lui-même d'écraser un disque utilisé ou de détruire un jeu qui a des enfants.
/// </summary>
public sealed class ZfsService(ICommandRunner run, IDiskProvider disks, IConfiguration cfg)
{
    string MountRoot => (cfg["Zfs:MountRoot"] ?? "/srv/zfs").TrimEnd('/');

    public async Task<IReadOnlyList<PoolInfo>> ListPoolsAsync(CancellationToken ct = default)
    {
        var list = Check(await run.RunAsync("zpool", ZfsParser.PoolListArgs, ct: ct), "Listing pools");
        var result = new List<PoolInfo>();
        foreach (var p in ZfsParser.ParsePoolList(list))
        {
            var status = await run.RunAsync("zpool", ["status", p.Name], ct: ct);
            var (scan, errors, devices) = status.ExitCode == 0 ? ZfsParser.ParseStatus(status.StdOut) : ("", "", []);
            result.Add(new PoolInfo(p.Name, p.Health, p.Size, p.Alloc, p.Free, scan, errors, devices));
        }
        return result;
    }

    public async Task CreatePoolAsync(CreatePoolRequest r, CancellationToken ct = default)
    {
        if (Rules.ValidatePoolName(r.Name) is { } e1) throw new OperationException(e1);
        var paths = r.Disks ?? [];
        var single = r.Layout == "" && paths.Count == 1; // un seul périphérique, sans redondance (partition)
        if (!single && Rules.ValidatePoolLayout(r.Layout, paths.Count) is { } e2) throw new OperationException(e2);
        if (paths.Distinct().Count() != paths.Count) throw new OperationException(Msg.F("A disk is selected twice."));

        // Seuls les disques que l'agent connaît et qui ne sont montés nulle part (système compris) sont acceptés.
        var known = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var d in await disks.ListAsync(ct))
        {
            known[d.Path] = d.MountPoints;
            foreach (var part in d.Partitions ?? []) known[part.Path] = part.MountPoints;
        }
        foreach (var p in paths)
        {
            if (!known.TryGetValue(p, out var mounts)) throw new OperationException($"Disque inconnu : {p}.");
            if (mounts.Count > 0) throw new OperationException(Msg.F("{0} is in use by the system ({1}).", p, string.Join(", ", mounts)));
        }

        var existing = ZfsParser.ParsePoolList(Check(await run.RunAsync("zpool", ZfsParser.PoolListArgs, ct: ct), "Listing pools"));
        if (existing.Any(x => x.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase))) throw new OperationException(Msg.F("Pool “{0}” already exists.", r.Name));

        // L'UMask 0007 de l'unité systemd rendrait le dossier racine inaccessible aux utilisateurs du NAS : on le remet en 0755.
        Check(await run.RunAsync("mkdir", ["-p", MountRoot], ct: ct), "Creating the pools folder");
        Check(await run.RunAsync("chmod", ["0755", MountRoot], ct: ct), "chmod");

        List<string> args =
        [
            "create", "-o", "ashift=12",
            "-O", "compression=lz4", "-O", "atime=off", "-O", "xattr=sa",
            "-m", $"{MountRoot}/{r.Name}",
            r.Name,
        ];
        if (r.Layout != "") args.Add(r.Layout);
        args.AddRange(paths);
        Check(await run.RunAsync("zpool", args, ct: ct), "Creating the pool");
    }

    public async Task DestroyPoolAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidatePoolName(name) is { } e) throw new OperationException(e);
        var pools = ZfsParser.ParsePoolList(Check(await run.RunAsync("zpool", ZfsParser.PoolListArgs, ct: ct), "Listing pools"));
        if (!pools.Any(p => p.Name == name)) throw new OperationException(Msg.F("Pool “{0}” does not exist.", name));
        Check(await run.RunAsync("zpool", ["destroy", name], ct: ct), "Destroying the pool");
    }

    public async Task ScrubAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidatePoolName(name) is { } e) throw new OperationException(e);
        Check(await run.RunAsync("zpool", ["scrub", name], ct: ct), "Scrubbing the pool");
    }

    public async Task<IReadOnlyList<PoolTrimStatus>> ListTrimAsync(CancellationToken ct = default)
    {
        var list = Check(await run.RunAsync("zpool", ZfsParser.PoolListArgs, ct: ct), "Listing pools");
        var result = new List<PoolTrimStatus>();
        foreach (var p in ZfsParser.ParsePoolList(list))
        {
            // A failure is reported: a pool left out would look as if it no longer existed.
            var status = Check(await run.RunAsync("zpool", ["status", "-t", p.Name], ct: ct), "Reading the TRIM status");
            result.Add(new PoolTrimStatus(p.Name, ZfsParser.ParseTrim(status)));
        }
        return result;
    }

    public async Task TrimAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidatePoolName(name) is { } e) throw new OperationException(e);
        Check(await run.RunAsync("zpool", ["trim", name], ct: ct), "Trimming the pool");
    }

    public async Task<IReadOnlyList<DatasetInfo>> ListDatasetsAsync(CancellationToken ct = default) =>
        ZfsParser.ParseDatasets(Check(await run.RunAsync("zfs", ZfsParser.DatasetListArgs, ct: ct), "Listing datasets"));

    public async Task CreateDatasetAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidateDatasetName(name) is { } e) throw new OperationException(e);
        Check(await run.RunAsync("zfs", ["create", name], ct: ct), "Creating the dataset");
    }

    public async Task DestroyDatasetAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidateDatasetName(name) is { } e) throw new OperationException(e);
        Check(await run.RunAsync("zfs", ["destroy", name], ct: ct), "Deleting the dataset");
    }

    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(CancellationToken ct = default) =>
        ZfsParser.ParseSnapshots(Check(await run.RunAsync("zfs", ZfsParser.SnapshotListArgs, ct: ct), "Listing snapshots"));

    public async Task CreateSnapshotAsync(CreateSnapshotRequest r, CancellationToken ct = default)
    {
        if (Rules.ValidateDatasetName(r.Dataset) is { } e1) throw new OperationException(e1);
        if (Rules.ValidateSnapshotName(r.Name) is { } e2) throw new OperationException(e2);
        Check(await run.RunAsync("zfs", ["snapshot", $"{r.Dataset}@{r.Name}"], ct: ct), "Creating the snapshot");
    }

    public async Task DestroySnapshotAsync(string fullName, CancellationToken ct = default)
    {
        var at = fullName?.IndexOf('@') ?? -1;
        if (at < 0) throw new OperationException(Msg.F("Invalid snapshot name (dataset@name expected)."));
        if (Rules.ValidateDatasetName(fullName![..at]) is { } e1) throw new OperationException(e1);
        if (Rules.ValidateSnapshotName(fullName[(at + 1)..]) is { } e2) throw new OperationException(e2);
        Check(await run.RunAsync("zfs", ["destroy", fullName], ct: ct), "Deleting the snapshot");
    }

    static string Check(CommandResult r, string what)
    {
        if (r.ExitCode == 0) return r.StdOut;
        var msg = (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim();
        throw new OperationException(Msg.F("{0} failed: {1}", what, msg));
    }
}
