using System.Globalization;
using System.Xml.Linq;

namespace Nas.Agent.Services;

/// <summary>
/// Libvirt simulé (URSANAS_FAKE=1) : quelques machines virtuelles en mémoire qui réagissent à <c>virsh</c>, <c>virt-install</c> et
/// <c>qemu-img</c>, pour développer l'interface des machines sans KVM. Les vraies définitions XML sont lues et réécrites par
/// <see cref="VmService"/> et <see cref="VmXml"/> ; seules les commandes sont simulées. Tout disparaît à l'arrêt de l'agent.
/// </summary>
sealed class FakeVirt
{
    sealed record Snap(string Name, string? Parent, long Created);

    sealed class Dom(XDocument def, string state, bool autostart)
    {
        public XDocument Def = def;
        public string State = state;
        public bool Autostart = autostart;
        public readonly List<Snap> Snaps = [];
        public string? Current;
    }

    const string Images = "/var/lib/libvirt/images";
    readonly Dictionary<string, Dom> _doms = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> _diskGb = new(StringComparer.Ordinal); // fichier → taille virtuelle
    int _mac = 0x10a001;

    static CommandResult Ok(string output = "") => new(0, output, "");
    static CommandResult Fail(string err) => new(1, "", err);

    public FakeVirt()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var web = Add(Build("debian-12", "Serveur web de test", 1, 4, "host-passthrough", 4096, uefi: true, secure: false, "q35", tpm: false,
            [("vda", "virtio", $"{Images}/debian-12.qcow2", 40)], cdrom: null, [("virtio", "br0")]), "running", autostart: true);
        web.Snaps.Add(new("installation-propre", null, now - 20 * 86400));
        web.Snaps.Add(new("avant-mise-a-jour", "installation-propre", now - 3 * 86400));
        web.Current = "avant-mise-a-jour";

        Add(Build("windows-11", "", 1, 4, "host-model", 8192, uefi: true, secure: true, "q35", tpm: true,
            [("vda", "sata", $"{Images}/windows-11.qcow2", 120)], cdrom: "/srv/iso/Win11.iso", [("e1000e", null)]), "shut off", autostart: false);

        Add(Build("alpine-test", "Essais rapides", 1, 1, "qemu64", 512, uefi: false, secure: false, "pc", tpm: false,
            [("vda", "virtio", $"{Images}/alpine-test.qcow2", 8)], cdrom: null, [("virtio", null)]), "shut off", autostart: false);
    }

    Dom Add(XDocument def, string state, bool autostart)
    {
        FillMacs(def);
        var d = new Dom(def, state, autostart);
        _doms[def.Root!.Element("name")!.Value] = d;
        return d;
    }

    // ---- Définition XML (même forme que celle de libvirt, pour que VmXml la lise et la modifie comme la vraie)

    XDocument Build(string name, string description, int sockets, int cores, string cpuType, int memoryMb, bool uefi, bool secure, string machine, bool tpm,
        IReadOnlyList<(string Dev, string Bus, string File, long Gb)> disks, string? cdrom, IReadOnlyList<(string Model, string? Bridge)> nics)
    {
        var memory = (memoryMb * 1024L).ToString(CultureInfo.InvariantCulture);
        var cpu = new XElement("cpu");
        if (cpuType is "host-passthrough" or "host-model") cpu.SetAttributeValue("mode", cpuType);
        else { cpu.SetAttributeValue("mode", "custom"); cpu.Add(new XElement("model", cpuType)); }
        cpu.Add(new XElement("topology", new XAttribute("sockets", sockets), new XAttribute("dies", 1), new XAttribute("cores", cores), new XAttribute("threads", 1)));

        var os = new XElement("os", new XElement("type", new XAttribute("arch", "x86_64"),
            new XAttribute("machine", machine == "q35" ? "pc-q35-8.2" : "pc-i440fx-8.2"), "hvm"));
        if (uefi)
        {
            var on = secure ? "yes" : "no";
            os.SetAttributeValue("firmware", "efi");
            os.Add(new XElement("firmware",
                new XElement("feature", new XAttribute("enabled", on), new XAttribute("name", "enrolled-keys")),
                new XElement("feature", new XAttribute("enabled", on), new XAttribute("name", "secure-boot"))));
        }

        var devices = new XElement("devices");
        foreach (var (dev, bus, file, gb) in disks)
        {
            _diskGb[file] = gb;
            devices.Add(new XElement("disk", new XAttribute("type", "file"), new XAttribute("device", "disk"),
                new XElement("driver", new XAttribute("name", "qemu"), new XAttribute("type", "qcow2")),
                new XElement("source", new XAttribute("file", file)),
                new XElement("target", new XAttribute("dev", dev), new XAttribute("bus", bus))));
        }
        var drive = new XElement("disk", new XAttribute("type", "file"), new XAttribute("device", "cdrom"),
            new XElement("driver", new XAttribute("name", "qemu"), new XAttribute("type", "raw")));
        if (cdrom is not null) drive.Add(new XElement("source", new XAttribute("file", cdrom)));
        drive.Add(new XElement("target", new XAttribute("dev", "sda"), new XAttribute("bus", "sata")), new XElement("readonly"));
        devices.Add(drive);
        foreach (var (model, bridge) in nics)
            devices.Add(new XElement("interface", new XAttribute("type", bridge is null ? "network" : "bridge"),
                bridge is null ? new XElement("source", new XAttribute("network", "default")) : new XElement("source", new XAttribute("bridge", bridge)),
                new XElement("model", new XAttribute("type", model))));
        if (tpm)
            devices.Add(new XElement("tpm", new XAttribute("model", machine == "q35" ? "tpm-crb" : "tpm-tis"),
                new XElement("backend", new XAttribute("type", "emulator"), new XAttribute("version", "2.0"))));

        var root = new XElement("domain", new XAttribute("type", "kvm"), new XElement("name", name));
        if (description.Length > 0) root.Add(new XElement("description", description));
        root.Add(new XElement("memory", new XAttribute("unit", "KiB"), memory), new XElement("currentMemory", new XAttribute("unit", "KiB"), memory),
            new XElement("vcpu", new XAttribute("placement", "static"), sockets * cores), os, cpu, devices);
        return new XDocument(root);
    }

    /// <summary>Libvirt attribue l'adresse MAC à l'enregistrement d'une carte qui n'en a pas.</summary>
    void FillMacs(XDocument d)
    {
        foreach (var nic in d.Root!.Element("devices")!.Elements("interface").Where(e => e.Element("mac") is null))
        {
            var n = _mac++;
            var mac = $"52:54:00:{(n >> 16) & 255:x2}:{(n >> 8) & 255:x2}:{n & 255:x2}";
            nic.AddFirst(new XElement("mac", new XAttribute("address", mac)));
        }
    }

    // ---- virsh

    public CommandResult Virsh(IReadOnlyList<string> args)
    {
        var a = args.Count >= 2 && args[0] == "-c" ? args.Skip(2).ToList() : args.ToList();
        if (a.Count == 0) return Ok();
        var rest = a.Skip(1).Where(x => !x.StartsWith('-')).ToList(); // sans les options : domaine, puis éventuellement snapshot
        var snapName = rest.ElementAtOrDefault(1) ?? "";
        string Name() => rest.ElementAtOrDefault(0) ?? "";
        Dom? Find() => _doms.TryGetValue(Name(), out var d) ? d : null;
        CommandResult NotFound() => Fail($"error: failed to get domain '{Name()}'\nerror: Domain not found: no domain with matching name '{Name()}'");

        switch (a[0])
        {
            case "version":
                return Ok("Compiled against library: libvirt 10.0.0\nUsing library: libvirt 10.0.0\nUsing API: QEMU 10.0.0\nRunning hypervisor: QEMU 8.2.0\n");
            case "list":
                return Ok(string.Concat(_doms.Keys.Order(StringComparer.Ordinal).Select(n => n + "\n")) + "\n");
            case "net-info":
                return Ok("Name:           default\nUUID:           00000000-0000-0000-0000-000000000001\nActive:         yes\nPersistent:     yes\nAutostart:      yes\nBridge:         virbr0\n");
            case "net-start" or "net-autostart":
                return Ok();
            case "dominfo" when Find() is { } d:
                var vcpu = d.Def.Root!.Element("vcpu")!.Value;
                var kib = d.Def.Root.Element("memory")!.Value;
                return Ok($"Id:             {(d.State == "running" ? RunningIndex(Name()) + 1 : "-")}\nName:           {Name()}\nState:          {d.State}\nCPU(s):         {vcpu}\nMax memory:     {kib} KiB\nUsed memory:    {kib} KiB\nAutostart:      {(d.Autostart ? "enable" : "disable")}\n");
            case "domstate" when Find() is { } d:
                return Ok(d.State + "\n");
            case "vncdisplay" when Find() is { } d:
                return d.State == "running" ? Ok($":{RunningIndex(Name())}\n") : Fail("error: Failed to get VNC display: domain is not running");
            case "dumpxml" when Find() is { } d:
                return Ok(d.Def.ToString());
            case "start" when Find() is { } d:
                if (d.State == "running") return Fail($"error: Failed to start domain '{Name()}'\nerror: Requested operation is not valid: domain is already running");
                d.State = "running";
                return Ok($"Domain '{Name()}' started\n");
            case "shutdown" or "destroy" when Find() is { } d:
                if (d.State != "running") return Fail($"error: Failed to {a[0]} domain '{Name()}'\nerror: Requested operation is not valid: domain is not running");
                d.State = "shut off";
                return Ok($"Domain '{Name()}' {(a[0] == "destroy" ? "destroyed" : "is being shutdown")}\n");
            case "autostart" when Find() is { } d:
                d.Autostart = !a.Contains("--disable");
                return Ok();
            case "undefine" when Find() is { } d:
                _doms.Remove(Name());
                return Ok($"Domain '{Name()}' has been undefined\n");
            case "define":
                return Define(a[1]);

            case "snapshot-list" when Find() is { } d:
                return Ok(string.Concat(d.Snaps.Select(s => s.Name + "\n")) + "\n");
            case "snapshot-info" when Find() is { } d:
                return d.Snaps.FirstOrDefault(s => s.Name == snapName) is { } found
                    ? Ok($"Name:           {found.Name}\nDomain:         {Name()}\nCurrent:        {(d.Current == found.Name ? "yes" : "no")}\nParent:         {found.Parent ?? "-"}\n")
                    : Fail($"error: Domain snapshot not found: no domain snapshot with matching name '{snapName}'");
            case "snapshot-dumpxml" when Find() is { } d:
                return d.Snaps.FirstOrDefault(s => s.Name == snapName) is { } found2
                    ? Ok($"<domainsnapshot><name>{found2.Name}</name><creationTime>{found2.Created}</creationTime></domainsnapshot>")
                    : Fail($"error: Domain snapshot not found: no domain snapshot with matching name '{snapName}'");
            case "snapshot-create-as" when Find() is { } d:
                var sn = a.IndexOf("--name") is var ni and >= 0 && ni + 1 < a.Count ? a[ni + 1] : snapName;
                if (d.Snaps.Any(s => s.Name == sn)) return Fail($"error: Failed to create snapshot\nerror: domain snapshot '{sn}' already exists");
                d.Snaps.Add(new Snap(sn, d.Current, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                d.Current = sn;
                return Ok($"Domain snapshot {sn} created\n");
            case "snapshot-revert" when Find() is { } d:
                if (d.Snaps.All(s => s.Name != snapName)) return Fail($"error: Domain snapshot not found: no domain snapshot with matching name '{snapName}'");
                d.Current = snapName;
                d.State = "shut off";
                return Ok();
            case "snapshot-delete" when Find() is { } d:
                var gone = d.Snaps.FirstOrDefault(s => s.Name == snapName);
                if (gone is null) return Fail($"error: Domain snapshot not found: no domain snapshot with matching name '{snapName}'");
                d.Snaps.Remove(gone);
                for (var i = 0; i < d.Snaps.Count; i++) if (d.Snaps[i].Parent == gone.Name) d.Snaps[i] = d.Snaps[i] with { Parent = gone.Parent };
                if (d.Current == gone.Name) d.Current = gone.Parent;
                return Ok($"Domain snapshot {snapName} deleted\n");

            case "dominfo" or "domstate" or "vncdisplay" or "dumpxml" or "start" or "shutdown" or "destroy" or "autostart" or "undefine"
                or "snapshot-list" or "snapshot-info" or "snapshot-dumpxml" or "snapshot-create-as" or "snapshot-revert" or "snapshot-delete":
                return NotFound();
            default:
                return Ok();
        }
    }

    int RunningIndex(string name) => _doms.Where(p => p.Value.State == "running").Select(p => p.Key).Order(StringComparer.Ordinal).ToList().IndexOf(name);

    CommandResult Define(string file)
    {
        XDocument doc;
        try { doc = XDocument.Parse(File.ReadAllText(file)); }
        catch (Exception e) when (e is IOException or System.Xml.XmlException) { return Fail("error: Failed to read the domain definition"); }
        var name = doc.Root?.Element("name")?.Value;
        if (string.IsNullOrEmpty(name)) return Fail("error: XML document failed to validate: missing name");
        FillMacs(doc);
        if (_doms.TryGetValue(name, out var d)) d.Def = doc;
        else _doms[name] = new Dom(doc, "shut off", autostart: false);
        return Ok($"Domain '{name}' defined from {file}\n");
    }

    // ---- virt-install : crée la machine et la démarre (installation depuis l'image)

    public CommandResult Install(IReadOnlyList<string> a)
    {
        string? Val(string key) => a.ToList().IndexOf(key) is var i and >= 0 && i + 1 < a.Count ? a[i + 1] : null;
        var name = Val("--name");
        if (name is null) return Fail("ERROR    --name is required");
        if (_doms.ContainsKey(name)) return Fail($"ERROR    Guest name '{name}' is already in use.");
        var disk = (Val("--disk") ?? "").Split(',').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        var path = disk.GetValueOrDefault("path") ?? $"{Images}/{name}.qcow2";
        var gb = long.TryParse(disk.GetValueOrDefault("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var g) ? g : 20;
        var net = Val("--network") ?? "network=default";
        string? bridge = net.StartsWith("bridge=", StringComparison.Ordinal) ? net["bridge=".Length..].Split(',')[0] : null;
        var boot = Val("--boot") ?? "";
        var uefi = boot.StartsWith("uefi", StringComparison.Ordinal);
        var secure = boot.Contains("secure-boot,firmware.feature1.enabled=yes", StringComparison.Ordinal);
        var memory = int.TryParse(Val("--memory"), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 1024;
        var vcpus = int.TryParse(Val("--vcpus"), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 1;
        Add(Build(name, "", 1, vcpus, "host-passthrough", memory, uefi, secure, uefi || Val("--machine") == "q35" ? "q35" : "pc", tpm: false,
            [("vda", "virtio", path, gb)], Val("--cdrom"), [("virtio", bridge)]), "running", autostart: false);
        return Ok($"Domain creation completed.\n");
    }

    // ---- qemu-img : la taille virtuelle des disques

    public CommandResult QemuImg(IReadOnlyList<string> a)
    {
        static long Gb(string s) => long.TryParse(s.TrimEnd('G'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        switch (a[0])
        {
            case "info":
                return Ok($"{{\"virtual-size\": {_diskGb.GetValueOrDefault(a[^1], 20) << 30}, \"filename\": \"{a[^1]}\", \"format\": \"qcow2\"}}");
            case "create":
                _diskGb[a[^2]] = Gb(a[^1]);
                return Ok();
            case "resize":
                _diskGb[a[^2]] = Gb(a[^1]);
                return Ok();
            default:
                return Ok();
        }
    }
}
