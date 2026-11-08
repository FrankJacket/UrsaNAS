using System.ComponentModel;
using System.Globalization;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>Lecture de la sortie de <c>virsh dominfo</c> (« Clé:   valeur », une par ligne).</summary>
public static class VmParser
{
    public static Dictionary<string, string> Fields(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var i = line.IndexOf(':');
            if (i > 0) d[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        return d;
    }

    static long Leading(string? s)
    {
        if (s is null) return 0;
        var n = new string(s.TakeWhile(char.IsDigit).ToArray());
        return long.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>Date de création d'un snapshot (heure locale du NAS) lue dans <c>snapshot-dumpxml</c> ; vide si absente.</summary>
    public static string SnapshotCreated(string xml)
    {
        try
        {
            var t = System.Xml.Linq.XDocument.Parse(xml).Root?.Element("creationTime")?.Value;
            return long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var s)
                ? DateTimeOffset.FromUnixTimeSeconds(s).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
        }
        catch (System.Xml.XmlException) { return ""; }
    }

    /// <summary>« :0 » (sortie de <c>virsh vncdisplay</c>) → port 5900.</summary>
    public static int? VncPort(string text)
    {
        var t = text.Trim();
        var i = t.LastIndexOf(':');
        return i >= 0 && int.TryParse(t[(i + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? 5900 + n : null;
    }

    public static VmInfo Info(string name, string dominfo, long diskBytes, int? vnc)
    {
        var f = Fields(dominfo);
        return new VmInfo(name, f.GetValueOrDefault("State") ?? "unknown", (int)Leading(f.GetValueOrDefault("CPU(s)")),
            Leading(f.GetValueOrDefault("Max memory")) / 1024, diskBytes / (1024L * 1024 * 1024), vnc);
    }
}

/// <summary>
/// Machines virtuelles par libvirt (commande <c>virsh</c> et <c>virt-install</c>, sans shell). Les disques sont des fichiers qcow2 dans
/// <c>Vm:DiskDir</c>. Rien de ce que l'utilisateur fournit n'atteint une commande sans validation, et aucun nom ne commence par « - ».
/// </summary>
public sealed partial class VmService(ICommandRunner run, AclService acl, BackupService backup, IConfiguration cfg)
{
    readonly SemaphoreSlim _gate = new(1, 1);

    string Virsh => cfg["Vm:Virsh"] ?? "virsh";
    string Install => cfg["Vm:VirtInstall"] ?? "virt-install";
    string Connect => cfg["Vm:Connect"] ?? "qemu:///system";
    string DiskDir => (cfg["Vm:DiskDir"] ?? "/var/lib/libvirt/images").TrimEnd('/', '\\');
    string NetDir => cfg["Vm:NetDir"] ?? "/sys/class/net";
    string KvmDevice => cfg["Vm:KvmDevice"] ?? "/dev/kvm";
    const string VmFolder = "ursanas-vm";
    string DiskFile(string name, string? dir = null) => Path.Combine(dir ?? DiskDir, name + ".qcow2");
    string IsoCopy(string name, string? dir = null) => Path.Combine(dir ?? DiskDir, name + "-install.iso"); // qemu ne peut pas lire dans les partages (dossiers réservés au groupe du NAS)

    // ---- État

    public async Task<VmStatus> StatusAsync(CancellationToken ct = default)
    {
        CommandResult v;
        try { v = await run.RunAsync(Virsh, ["-c", Connect, "version", "--daemon"], ct: ct); }
        catch (Win32Exception) { return new(VmHostState.NotInstalled, null, false, []); }
        if (v.ExitCode != 0) return new(VmHostState.Stopped, null, File.Exists(KvmDevice), Bridges());
        var line = v.StdOut.Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => l.Contains("libvirt", StringComparison.OrdinalIgnoreCase));
        return new(VmHostState.Ready, line?[(line.LastIndexOf(' ') + 1)..], File.Exists(KvmDevice), Bridges());
    }

    /// <summary>Ponts réseau de la machine, sauf ceux de Docker et le NAT interne de libvirt.</summary>
    public IReadOnlyList<string> Bridges()
    {
        try
        {
            return Directory.EnumerateDirectories(NetDir).Where(d => Directory.Exists(Path.Combine(d, "bridge"))).Select(Path.GetFileName)
                .Where(n => n is { Length: > 0 } && !n.StartsWith("virbr", StringComparison.Ordinal) && !n.StartsWith("docker", StringComparison.Ordinal)
                    && !n.StartsWith("br-", StringComparison.Ordinal)).Select(n => n!).Order().ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    async Task<string> Ok(IReadOnlyList<string> args, string what, CancellationToken ct, string? exe = null)
    {
        CommandResult r;
        try { r = await run.RunAsync(exe ?? Virsh, exe is null ? ["-c", Connect, .. args] : args, ct: ct); }
        catch (Win32Exception) { throw new OperationException(Msg.F("Virtual machine tools are not installed.")); }
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

    // ---- Machines

    public async Task<IReadOnlyList<VmInfo>> ListAsync(CancellationToken ct = default)
    {
        var names = (await Ok(["list", "--all", "--name"], "Listing machines", ct)).Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(n => Rules.ValidateVmName(n) is null);
        var list = new List<VmInfo>();
        foreach (var n in names)
        {
            var info = await Ok(["dominfo", n], "Reading the machine", ct);
            var running = VmParser.Fields(info).GetValueOrDefault("State") == "running";
            int? vnc = null;
            if (running)
            {
                var r = await run.RunAsync(Virsh, ["-c", Connect, "vncdisplay", n], ct: ct);
                if (r.ExitCode == 0) vnc = VmParser.VncPort(r.StdOut);
            }
            list.Add(VmParser.Info(n, info, DiskBytes(n), vnc));
        }
        return list;
    }

    /// <summary>Dossiers où nos disques peuvent se trouver : le dossier par défaut et le dossier « ursanas-vm » de chaque disque monté.</summary>
    List<string> StorageDirs()
    {
        var dirs = new List<string> { DiskDir };
        try { dirs.AddRange(backup.ListTargets().Select(t => t.Dir.TrimEnd('/') + "/" + VmFolder)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationException) { }
        return dirs;
    }

    long DiskBytes(string name)
    {
        try { return StorageDirs().Select(d => new FileInfo(DiskFile(name, d))).FirstOrDefault(f => f.Exists)?.Length ?? 0; }
        catch (IOException) { return 0; }
    }

    /// <summary>Port VNC (sur 127.0.0.1) de l'écran d'une machine en marche : la console du navigateur s'y branche par l'interface web.</summary>
    public async Task<int> VncPortAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidateVmName(name) is { } e) throw new OperationException(e);
        var port = VmParser.VncPort(await Ok(["vncdisplay", name], "Opening the console", ct));
        return port ?? throw new OperationException(Msg.F("The machine is not running."));
    }

    public Task ActionAsync(string name, string action, CancellationToken ct = default)
    {
        if (Rules.ValidateVmName(name) is { } e) throw new OperationException(e);
        return Mutate(async () =>
        {
            switch (action)
            {
                case "start": await Ok(["start", name], "Starting the machine", ct); break;
                case "shutdown": await Ok(["shutdown", name], "Shutting the machine down", ct); break; // demande d'arrêt à l'invité (ACPI)
                case "stop": await Ok(["destroy", name], "Powering the machine off", ct); break; // coupure immédiate
                case "remove":
                    var state = VmParser.Fields(await Ok(["dominfo", name], "Reading the machine", ct)).GetValueOrDefault("State");
                    if (state == "running") throw new OperationException(Msg.F("Stop the machine before deleting it."));
                    await Ok(["undefine", "--nvram", "--snapshots-metadata", name], "Deleting the machine", ct);
                    try { foreach (var d in StorageDirs()) { File.Delete(DiskFile(name, d)); File.Delete(IsoCopy(name, d)); } } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // le disque que nous avons créé
                    break;
                default: throw new OperationException(Msg.F("Unknown action."));
            }
        });
    }

    // ---- Création

    /// <summary>Les .iso des partages donnés et des disques montés (trois niveaux de dossiers au plus, 200 fichiers au plus).</summary>
    public async Task<IReadOnlyList<IsoFile>> IsosAsync(IReadOnlyList<string> shares, CancellationToken ct = default)
    {
        var found = new List<IsoFile>();
        var places = new List<(string Location, string Root)>();
        foreach (var s in shares.Where(s => Rules.ValidateShareName(s) is null).Distinct())
            places.Add((s, await acl.SharePathAsync(s, ct)));
        foreach (var d in backup.ListTargets()) places.Add((d.Dir, d.Dir));
        foreach (var (location, root) in places)
            Scan(location, root, "", 0, found);
        return found.OrderBy(f => f.Location, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Take(200).ToList();
    }

    static void Scan(string location, string root, string rel, int depth, List<IsoFile> found)
    {
        if (found.Count >= 200) return;
        try
        {
            var dir = new DirectoryInfo(rel.Length == 0 ? root : Path.Combine(root, rel));
            foreach (var f in dir.EnumerateFiles("*.iso"))
                if (f.LinkTarget is null) found.Add(new IsoFile(location, rel.Length == 0 ? f.Name : rel + "/" + f.Name, f.Length));
            if (depth >= 3) return;
            foreach (var d in dir.EnumerateDirectories())
                if (d.LinkTarget is null && !d.Name.StartsWith('.') && !d.Name.StartsWith('#') && !d.Name.StartsWith('@'))
                    Scan(location, root, rel.Length == 0 ? d.Name : rel + "/" + d.Name, depth + 1, found);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    async Task<string> IsoFullPathAsync(string location, string path, CancellationToken ct)
    {
        string root;
        if (location.Contains('/')) root = backup.ValidateTarget(location);
        else
        {
            if (Rules.ValidateShareName(location) is { } e) throw new OperationException(e);
            root = await acl.SharePathAsync(location, ct);
        }
        return AclService.Dir(root, path, file: true);
    }

    // Le réseau « default » de libvirt (NAT) est défini mais arrêté sur une installation neuve de Debian.
    async Task EnsureDefaultNetworkAsync(CancellationToken ct)
    {
        var info = await Ok(["net-info", "default"], "Reading the network", ct);
        if (VmParser.Fields(info).GetValueOrDefault("Active") == "yes") return;
        await Ok(["net-start", "default"], "Starting the network", ct);
        await run.RunAsync(Virsh, ["-c", Connect, "net-autostart", "default"], ct: ct);
    }

    public Task CreateAsync(CreateVmRequest r, CancellationToken ct = default)
    {
        if (Rules.ValidateCreateVm(r) is { } e) throw new OperationException(e);
        return Mutate(async () =>
        {
            var iso = await IsoFullPathAsync(r.IsoLocation, r.IsoPath, ct);
            var dir = r.StorageDir is null ? DiskDir : backup.ValidateTarget(r.StorageDir).TrimEnd('/') + "/" + VmFolder;
            var disk = DiskFile(r.Name, dir);
            if (File.Exists(disk)) throw new OperationException(Msg.F("A machine named “{0}” already exists.", r.Name));
            var names = (await Ok(["list", "--all", "--name"], "Listing machines", ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Contains(r.Name)) throw new OperationException(Msg.F("A machine named “{0}” already exists.", r.Name));
            var isoCopy = IsoCopy(r.Name, dir);
            Directory.CreateDirectory(dir);
            if (r.StorageDir is not null)
            {
                // qemu (compte libvirt) doit pouvoir traverser le disque : la racine reste fermée au public, seul le passage est ouvert.
                await Ok(["o+x", r.StorageDir], "Preparing the disk folder", ct, "chmod");
                await Ok(["0755", dir], "Preparing the disk folder", ct, "chmod");
            }
            try { File.Copy(iso, isoCopy, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new OperationException(Msg.F("{0} failed: {1}", "Copying the installation image", ex.Message)); }
            var args = new List<string>
            {
                "--connect", Connect, "--name", r.Name,
                "--memory", r.MemoryMb.ToString(CultureInfo.InvariantCulture), "--vcpus", r.Vcpus.ToString(CultureInfo.InvariantCulture),
                "--disk", $"path={disk},size={r.DiskGb.ToString(CultureInfo.InvariantCulture)},format=qcow2",
                "--cdrom", isoCopy,
                "--network", string.IsNullOrEmpty(r.Bridge) ? "network=default" : "bridge=" + r.Bridge,
                "--graphics", "vnc,listen=127.0.0.1",
                "--osinfo", "detect=on,require=off",
                "--noautoconsole",
            };
            var ovmf = Directory.Exists(cfg["Vm:OvmfDir"] ?? "/usr/share/OVMF");
            var uefi = r.Bios == "ovmf" || (r.Bios.Length == 0 && ovmf);
            if (uefi && !ovmf) throw new OperationException(Msg.F("UEFI firmware (OVMF) is not installed on the NAS."));
            if (uefi)
            {
                // Secure Boot refuse les systèmes non signés (Alpine, outils de dépannage…) : il est donc désactivé sauf demande contraire.
                var on = r.SecureBoot ? "yes" : "no";
                args.AddRange(["--machine", "q35", "--boot", $"uefi,firmware.feature0.name=enrolled-keys,firmware.feature0.enabled={on},firmware.feature1.name=secure-boot,firmware.feature1.enabled={on}"]);
                if (r.SecureBoot) args.AddRange(["--features", "smm.state=on"]);
            }
            if (string.IsNullOrEmpty(r.Bridge)) await EnsureDefaultNetworkAsync(ct);
            try { await Ok(args, "Creating the machine", ct, Install); }
            catch { try { File.Delete(isoCopy); File.Delete(disk); } catch (IOException) { } throw; }
        });
    }
}
