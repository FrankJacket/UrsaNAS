using System.Globalization;
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
        var list = Check(await run.RunAsync("zpool", ZfsParser.PoolListArgs, ct: ct), "Liste des pools");
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
        if (Rules.ValidatePoolLayout(r.Layout, paths.Count) is { } e2) throw new OperationException(e2);
        if (paths.Distinct().Count() != paths.Count) throw new OperationException("Un disque est sélectionné deux fois.");

        // Seuls les disques que l'agent connaît et qui ne sont montés nulle part (système compris) sont acceptés.
        var known = (await disks.ListAsync(ct)).ToDictionary(d => d.Path);
        foreach (var p in paths)
        {
            if (!known.TryGetValue(p, out var d)) throw new OperationException($"Disque inconnu : {p}.");
            if (d.MountPoints.Count > 0) throw new OperationException($"{p} est utilisé par le système ({string.Join(", ", d.MountPoints)}).");
        }

        var existing = ZfsParser.ParsePoolList(Check(await run.RunAsync("zpool", ZfsParser.PoolListArgs, ct: ct), "Liste des pools"));
        if (existing.Any(x => x.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase))) throw new OperationException($"Le pool « {r.Name} » existe déjà.");

        List<string> args =
        [
            "create", "-o", "ashift=12",
            "-O", "compression=lz4", "-O", "atime=off", "-O", "xattr=sa",
            "-m", $"{MountRoot}/{r.Name}",
            r.Name, r.Layout,
        ];
        args.AddRange(paths);
        Check(await run.RunAsync("zpool", args, ct: ct), "Création du pool");
    }

    public async Task DestroyPoolAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidatePoolName(name) is { } e) throw new OperationException(e);
        var pools = ZfsParser.ParsePoolList(Check(await run.RunAsync("zpool", ZfsParser.PoolListArgs, ct: ct), "Liste des pools"));
        if (!pools.Any(p => p.Name == name)) throw new OperationException($"Le pool « {name} » n'existe pas.");
        Check(await run.RunAsync("zpool", ["destroy", name], ct: ct), "Destruction du pool");
    }

    public async Task ScrubAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidatePoolName(name) is { } e) throw new OperationException(e);
        Check(await run.RunAsync("zpool", ["scrub", name], ct: ct), "Vérification du pool");
    }

    public async Task<IReadOnlyList<DatasetInfo>> ListDatasetsAsync(CancellationToken ct = default) =>
        ZfsParser.ParseDatasets(Check(await run.RunAsync("zfs", ZfsParser.DatasetListArgs, ct: ct), "Liste des jeux de données"));

    public async Task CreateDatasetAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidateDatasetName(name) is { } e) throw new OperationException(e);
        Check(await run.RunAsync("zfs", ["create", name], ct: ct), "Création du jeu de données");
    }

    public async Task DestroyDatasetAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidateDatasetName(name) is { } e) throw new OperationException(e);
        Check(await run.RunAsync("zfs", ["destroy", name], ct: ct), "Suppression du jeu de données");
    }

    public async Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(CancellationToken ct = default) =>
        ZfsParser.ParseSnapshots(Check(await run.RunAsync("zfs", ZfsParser.SnapshotListArgs, ct: ct), "Liste des instantanés"));

    public async Task CreateSnapshotAsync(CreateSnapshotRequest r, CancellationToken ct = default)
    {
        if (Rules.ValidateDatasetName(r.Dataset) is { } e1) throw new OperationException(e1);
        if (Rules.ValidateSnapshotName(r.Name) is { } e2) throw new OperationException(e2);
        Check(await run.RunAsync("zfs", ["snapshot", $"{r.Dataset}@{r.Name}"], ct: ct), "Création de l'instantané");
    }

    public async Task DestroySnapshotAsync(string fullName, CancellationToken ct = default)
    {
        var at = fullName?.IndexOf('@') ?? -1;
        if (at < 0) throw new OperationException("Nom d'instantané invalide (jeu@nom attendu).");
        if (Rules.ValidateDatasetName(fullName![..at]) is { } e1) throw new OperationException(e1);
        if (Rules.ValidateSnapshotName(fullName[(at + 1)..]) is { } e2) throw new OperationException(e2);
        Check(await run.RunAsync("zfs", ["destroy", fullName], ct: ct), "Suppression de l'instantané");
    }

    static string Check(CommandResult r, string what)
    {
        if (r.ExitCode == 0) return r.StdOut;
        var msg = (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim();
        throw new OperationException($"{what} a échoué : {msg}");
    }
}
