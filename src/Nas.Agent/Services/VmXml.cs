using System.Globalization;
using System.Xml.Linq;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Lecture et modification de la définition libvirt (XML) d'une machine : le matériel de la page de réglages. Aucune commande ici,
/// seulement des transformations du XML, pour pouvoir les tester. Les modifications renvoient un nouveau document ; l'original ne change pas.
/// </summary>
public static class VmXml
{
    static XElement Devices(XDocument d) => d.Root!.Element("devices") ?? throw new OperationException(Msg.F("Unreadable machine definition."));
    static string Attr(XElement? e, string name) => e?.Attribute(name)?.Value ?? "";

    static IEnumerable<XElement> DiskElements(XDocument d) => Devices(d).Elements("disk");
    static IEnumerable<XElement> NicElements(XDocument d) => Devices(d).Elements("interface");

    static XDocument Copy(XDocument d) => new(d);

    // ---- Lecture

    public static VmConfig Read(string xml, string state, bool autostart, Func<string, long>? diskSizeGb = null)
    {
        XDocument d;
        try { d = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { throw new OperationException(Msg.F("Unreadable machine definition.")); }
        var root = d.Root!;
        var vcpu = Int(root.Element("vcpu")?.Value, 1);
        var topo = root.Element("cpu")?.Element("topology");
        var sockets = topo is null ? 1 : Int(Attr(topo, "sockets"), 1);
        var cores = topo is null ? vcpu : Int(Attr(topo, "cores"), 1) * Int(Attr(topo, "threads"), 1);
        var os = root.Element("os");
        var bios = Attr(os, "firmware") == "efi" || os?.Element("loader") is not null ? "ovmf" : "seabios";
        var machine = Attr(os?.Element("type"), "machine").Contains("q35", StringComparison.Ordinal) ? "q35" : "pc";

        var disks = DiskElements(d).Where(e => Attr(e, "device") is "disk" or "cdrom" && Attr(e.Element("target"), "dev").Length > 0).Select(e =>
        {
            var source = e.Element("source");
            var file = source?.Attribute("file")?.Value ?? source?.Attribute("dev")?.Value;
            var cdrom = Attr(e, "device") == "cdrom";
            return new VmDisk(Attr(e.Element("target"), "dev"), Attr(e.Element("target"), "bus"), file, cdrom || file is null ? 0 : diskSizeGb?.Invoke(file) ?? 0, cdrom);
        }).ToList();

        var nics = NicElements(d).Select(e => new VmNic(Attr(e.Element("mac"), "address"), Attr(e.Element("model"), "type"),
            Attr(e, "type") == "bridge" ? Attr(e.Element("source"), "bridge") : null)).ToList();

        var boot = DiskElements(d).Select(e => (Order: Int(Attr(e.Element("boot"), "order"), 0), Id: Attr(e.Element("target"), "dev")))
            .Concat(NicElements(d).Select(e => (Order: Int(Attr(e.Element("boot"), "order"), 0), Id: Attr(e.Element("mac"), "address"))))
            .Where(x => x.Order > 0 && x.Id.Length > 0).OrderBy(x => x.Order).Select(x => x.Id).ToList();

        return new VmConfig(root.Element("name")?.Value ?? "", state, root.Element("description")?.Value ?? "", sockets, cores, CpuType(root.Element("cpu")),
            MemoryMb(root.Element("memory")), bios, machine, SecureBoot(os), Devices(d).Element("tpm") is not null, autostart, boot, disks, nics);
    }

    static bool SecureBoot(XElement? os) =>
        Attr(os?.Element("loader"), "secure") == "yes"
        || os?.Element("firmware")?.Elements("feature").Any(f => Attr(f, "name") == "secure-boot" && Attr(f, "enabled") == "yes") == true;

    /// <summary>Fichier des variables UEFI de la machine (clés Secure Boot comprises), s'il y en a un.</summary>
    public static string? NvramPath(XDocument d) => d.Root?.Element("os")?.Element("nvram")?.Value is { Length: > 0 } p ? p : null;

    static int Int(string? s, int fallback) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    static string CpuType(XElement? cpu)
    {
        var mode = Attr(cpu, "mode");
        return mode switch
        {
            "host-passthrough" or "host-model" => mode,
            _ => cpu?.Element("model")?.Value is { Length: > 0 } m ? m : "qemu64",
        };
    }

    static int MemoryMb(XElement? memory)
    {
        if (memory is null || !long.TryParse(memory.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return 0;
        var kib = Attr(memory, "unit") switch { "" or "KiB" or "k" => n, "MiB" or "M" => n * 1024, "GiB" or "G" => n * 1024 * 1024, "B" or "bytes" => n / 1024, _ => n };
        return (int)(kib / 1024);
    }

    // ---- Réglages généraux

    public static XDocument ApplySettings(XDocument source, VmSettings s)
    {
        var d = Copy(source);
        var root = d.Root!;
        SetText(root, "description", string.IsNullOrWhiteSpace(s.Description) ? null : s.Description.Trim(), after: "name");

        var memory = (s.MemoryMb * 1024L).ToString(CultureInfo.InvariantCulture);
        foreach (var n in new[] { "memory", "currentMemory" })
            if (root.Element(n) is { } m) { m.SetAttributeValue("unit", "KiB"); m.Value = memory; }
        root.Element("maxMemory")?.Remove();

        var vcpu = root.Element("vcpu");
        vcpu?.SetAttributeValue("current", null);
        if (vcpu is not null) vcpu.Value = (s.Sockets * s.Cores).ToString(CultureInfo.InvariantCulture);
        SetCpu(root, s);

        var os = root.Element("os")!;
        var typeEl = os.Element("type")!;
        var machineChanged = (Attr(typeEl, "machine").Contains("q35", StringComparison.Ordinal) ? "q35" : "pc") != s.Machine;
        typeEl.SetAttributeValue("machine", s.Machine);
        os.Element("loader")?.Remove();
        os.Element("nvram")?.Remove();
        os.Element("firmware")?.Remove(); // sans cet élément, libvirt refuse « firmware autoselection » désactivée
        os.SetAttributeValue("firmware", s.Bios == "ovmf" ? "efi" : null); // libvirt choisit lui-même le microprogramme UEFI
        if (s.Bios == "ovmf")
        {
            var on = s.SecureBoot ? "yes" : "no";
            typeEl.AddAfterSelf(new XElement("firmware",
                new XElement("feature", new XAttribute("enabled", on), new XAttribute("name", "enrolled-keys")),
                new XElement("feature", new XAttribute("enabled", on), new XAttribute("name", "secure-boot"))));
            if (s.SecureBoot) EnsureSmm(root);
        }
        if (machineChanged) ForgetPciAddresses(d);

        var devices = Devices(d);
        devices.Element("tpm")?.Remove();
        if (s.Tpm)
            devices.Add(new XElement("tpm", new XAttribute("model", s.Machine == "q35" ? "tpm-crb" : "tpm-tis"),
                new XElement("backend", new XAttribute("type", "emulator"), new XAttribute("version", "2.0"))));

        SetBootOrder(d, s.BootOrder ?? []);
        return d;
    }

    static void SetText(XElement root, string name, string? value, string after)
    {
        root.Element(name)?.Remove();
        if (value is null) return;
        var el = new XElement(name, value);
        if (root.Element(after) is { } a) a.AddAfterSelf(el); else root.AddFirst(el);
    }

    static void SetCpu(XElement root, VmSettings s)
    {
        var cpu = new XElement("cpu");
        switch (s.CpuType)
        {
            case "host-passthrough": cpu.SetAttributeValue("mode", "host-passthrough"); cpu.SetAttributeValue("check", "none"); break;
            case "host-model": cpu.SetAttributeValue("mode", "host-model"); break;
            default: cpu.SetAttributeValue("mode", "custom"); cpu.SetAttributeValue("match", "exact"); cpu.Add(new XElement("model", s.CpuType)); break;
        }
        cpu.Add(new XElement("topology", new XAttribute("sockets", s.Sockets), new XAttribute("dies", 1), new XAttribute("cores", s.Cores), new XAttribute("threads", 1)));
        var old = root.Element("cpu");
        if (old is not null) old.ReplaceWith(cpu);
        else (root.Element("vcpu") ?? throw new OperationException(Msg.F("Unreadable machine definition."))).AddAfterSelf(cpu);
    }

    // L'UEFI de Debian (OVMF) veut SMM sur q35 ; on le garantit dans <features>.
    static void EnsureSmm(XElement root)
    {
        var features = root.Element("features");
        if (features is null) { features = new XElement("features"); (root.Element("os") ?? throw new OperationException(Msg.F("Unreadable machine definition."))).AddAfterSelf(features); }
        if (features.Element("smm") is null) features.Add(new XElement("smm", new XAttribute("state", "on")));
    }

    // Changer de type de machine : les adresses PCI de l'ancien type ne conviennent pas, libvirt les attribue à nouveau.
    static void ForgetPciAddresses(XDocument d)
    {
        var devices = Devices(d);
        foreach (var a in devices.Descendants("address").Where(a => Attr(a, "type") == "pci").ToList()) a.Remove();
        foreach (var c in devices.Elements("controller").Where(c => Attr(c, "type") is "pci" or "usb" or "sata" or "ide").ToList()) c.Remove();
    }

    static void SetBootOrder(XDocument d, IReadOnlyList<string> order)
    {
        d.Root!.Element("os")!.Elements("boot").Remove();
        foreach (var b in Devices(d).Descendants("boot").ToList()) b.Remove();
        var n = 1;
        foreach (var id in order.Distinct())
        {
            var dev = DiskElements(d).FirstOrDefault(e => Attr(e.Element("target"), "dev") == id)
                ?? NicElements(d).FirstOrDefault(e => string.Equals(Attr(e.Element("mac"), "address"), id, StringComparison.OrdinalIgnoreCase));
            if (dev is null) continue;
            dev.Add(new XElement("boot", new XAttribute("order", n++)));
        }
    }

    // ---- Disques

    /// <summary>Prochaine cible libre pour ce type de bus : vda, vdb… (virtio) ou sda, sdb… (sata, scsi).</summary>
    public static string NextTarget(XDocument d, string bus)
    {
        var prefix = bus == "virtio" ? "vd" : "sd";
        var used = DiskElements(d).Select(e => Attr(e.Element("target"), "dev")).ToHashSet();
        for (var c = 'a'; c <= 'z'; c++)
            if (!used.Contains(prefix + c)) return prefix + c;
        throw new OperationException(Msg.F("Too many disks."));
    }

    public static (XDocument Doc, string Target) AddDisk(XDocument source, string bus, string file)
    {
        var d = Copy(source);
        var target = NextTarget(d, bus);
        Devices(d).Add(new XElement("disk", new XAttribute("type", "file"), new XAttribute("device", "disk"),
            new XElement("driver", new XAttribute("name", "qemu"), new XAttribute("type", "qcow2")),
            new XElement("source", new XAttribute("file", file)),
            new XElement("target", new XAttribute("dev", target), new XAttribute("bus", bus))));
        return (d, target);
    }

    static XElement Disk(XDocument d, string target) =>
        DiskElements(d).FirstOrDefault(e => Attr(e.Element("target"), "dev") == target) ?? throw new OperationException(Msg.F("Unknown disk."));

    /// <summary>Retire un disque ou un lecteur et renvoie le fichier qu'il utilisait.</summary>
    public static (XDocument Doc, string? File, bool Cdrom) RemoveDisk(XDocument source, string target)
    {
        var d = Copy(source);
        var e = Disk(d, target);
        var file = e.Element("source")?.Attribute("file")?.Value;
        var cdrom = Attr(e, "device") == "cdrom";
        e.Remove();
        return (d, file, cdrom);
    }

    public static XDocument SetCdrom(XDocument source, string target, string? file)
    {
        var d = Copy(source);
        var e = Disk(d, target);
        if (Attr(e, "device") != "cdrom") throw new OperationException(Msg.F("This drive is not a CD/DVD drive."));
        e.Element("source")?.Remove();
        if (file is not null) e.Element("driver")!.AddAfterSelf(new XElement("source", new XAttribute("file", file)));
        e.SetAttributeValue("type", "file");
        return d;
    }

    public static string? DiskFile(XDocument d, string target)
    {
        var e = Disk(d, target);
        return e.Element("source")?.Attribute("file")?.Value;
    }

    public static bool IsDisk(XDocument d, string target) => Attr(Disk(d, target), "device") == "disk";

    // ---- Cartes réseau

    /// <summary><paramref name="ovs"/> : le pont est un pont Open vSwitch, que libvirt branche avec un <c>virtualport</c> du même nom.</summary>
    static XElement NicXml(string model, string? bridge, bool ovs = false)
    {
        var nic = new XElement("interface", new XAttribute("type", string.IsNullOrEmpty(bridge) ? "network" : "bridge"),
            string.IsNullOrEmpty(bridge) ? new XElement("source", new XAttribute("network", "default")) : new XElement("source", new XAttribute("bridge", bridge)));
        if (ovs && !string.IsNullOrEmpty(bridge)) nic.Add(new XElement("virtualport", new XAttribute("type", "openvswitch")));
        nic.Add(new XElement("model", new XAttribute("type", model)));
        return nic;
    }

    public static XDocument AddNic(XDocument source, string model, string? bridge, bool ovs = false)
    {
        var d = Copy(source);
        Devices(d).Add(NicXml(model, bridge, ovs));
        return d;
    }

    static XElement Nic(XDocument d, string mac) =>
        NicElements(d).FirstOrDefault(e => string.Equals(Attr(e.Element("mac"), "address"), mac, StringComparison.OrdinalIgnoreCase))
            ?? throw new OperationException(Msg.F("Unknown network card."));

    public static XDocument RemoveNic(XDocument source, string mac)
    {
        var d = Copy(source);
        Nic(d, mac).Remove();
        return d;
    }

    public static XDocument UpdateNic(XDocument source, string mac, string model, string? bridge, bool ovs = false)
    {
        var d = Copy(source);
        var old = Nic(d, mac);
        var fresh = NicXml(model, bridge, ovs);
        fresh.AddFirst(new XElement("mac", new XAttribute("address", Attr(old.Element("mac"), "address")))); // la machine garde son adresse
        if (old.Element("boot") is { } boot) fresh.Add(new XElement(boot));
        old.ReplaceWith(fresh);
        return d;
    }
}
