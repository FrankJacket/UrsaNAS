using System.Xml.Linq;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class VmXmlTests
{
    const string Sample = """
        <domain type='kvm'>
          <name>web</name>
          <memory unit='KiB'>2097152</memory>
          <currentMemory unit='KiB'>2097152</currentMemory>
          <vcpu placement='static'>4</vcpu>
          <os>
            <type arch='x86_64' machine='pc-q35-9.2'>hvm</type>
            <boot dev='cdrom'/>
          </os>
          <features><acpi/></features>
          <cpu mode='host-passthrough' check='none'><topology sockets='2' dies='1' cores='2' threads='1'/></cpu>
          <devices>
            <disk type='file' device='disk'>
              <driver name='qemu' type='qcow2'/>
              <source file='/var/lib/libvirt/images/web.qcow2'/>
              <target dev='vda' bus='virtio'/>
              <address type='pci' domain='0x0000' bus='0x04' slot='0x00' function='0x0'/>
            </disk>
            <disk type='file' device='cdrom'>
              <driver name='qemu' type='raw'/>
              <source file='/var/lib/libvirt/images/web-install.iso'/>
              <target dev='sda' bus='sata'/>
              <readonly/>
            </disk>
            <controller type='pci' index='0' model='pcie-root'/>
            <interface type='bridge'>
              <mac address='52:54:00:aa:bb:cc'/>
              <source bridge='br0'/>
              <model type='virtio'/>
            </interface>
          </devices>
        </domain>
        """;

    static XDocument Doc() => XDocument.Parse(Sample);
    static VmConfig Read(XDocument d) => VmXml.Read(d.ToString(), "shut off", false, _ => 20);

    static VmSettings Settings(VmConfig c) => new(c.Description, c.Sockets, c.Cores, c.CpuType, c.MemoryMb, c.Bios, c.Machine, c.SecureBoot, c.Tpm, c.Autostart, c.BootOrder);

    [Fact]
    public void Lit_le_materiel()
    {
        var c = Read(Doc());
        Assert.Equal(("web", 2, 2, "host-passthrough", 2048, "seabios", "q35", false), (c.Name, c.Sockets, c.Cores, c.CpuType, c.MemoryMb, c.Bios, c.Machine, c.Tpm));
        Assert.Equal(2, c.Disks.Count);
        Assert.Equal(new VmDisk("vda", "virtio", "/var/lib/libvirt/images/web.qcow2", 20, false), c.Disks[0]);
        Assert.True(c.Disks[1].Cdrom);
        Assert.Equal(0, c.Disks[1].SizeGb);
        Assert.Equal(new VmNic("52:54:00:aa:bb:cc", "virtio", "br0"), Assert.Single(c.Nics));
    }

    [Fact]
    public void Modifie_processeurs_memoire_uefi_et_tpm()
    {
        var c = Read(Doc());
        var d = VmXml.ApplySettings(Doc(), Settings(c) with { Sockets = 1, Cores = 8, MemoryMb = 4096, CpuType = "qemu64", Bios = "ovmf", Tpm = true, Description = "Serveur web" });
        var n = Read(d);
        Assert.Equal((1, 8, "qemu64", 4096, "ovmf", true, "Serveur web"), (n.Sockets, n.Cores, n.CpuType, n.MemoryMb, n.Bios, n.Tpm, n.Description));
        Assert.Equal("8", d.Root!.Element("vcpu")!.Value);
        Assert.Equal("efi", d.Root.Element("os")!.Attribute("firmware")!.Value);
        Assert.Equal("tpm-crb", d.Root.Element("devices")!.Element("tpm")!.Attribute("model")!.Value);
    }

    [Fact]
    public void Revenir_a_seabios_retire_le_microprogramme_uefi_et_le_tpm()
    {
        var uefi = VmXml.ApplySettings(Doc(), Settings(Read(Doc())) with { Bios = "ovmf", Tpm = true });
        var back = VmXml.ApplySettings(uefi, Settings(Read(uefi)) with { Bios = "seabios", Tpm = false });
        Assert.Equal("seabios", Read(back).Bios);
        Assert.Null(back.Root!.Element("os")!.Attribute("firmware"));
        Assert.Null(back.Root.Element("devices")!.Element("tpm"));
    }

    [Fact]
    public void Revenir_a_seabios_apres_uefi_retire_aussi_l_element_firmware()
    {
        // Libvirt refuse « firmware autoselection » désactivée si des fonctions de microprogramme restent dans la définition.
        var uefi = VmXml.ApplySettings(Doc(), Settings(Read(Doc())) with { Bios = "ovmf" });
        Assert.NotNull(uefi.Root!.Element("os")!.Element("firmware"));
        var back = VmXml.ApplySettings(uefi, Settings(Read(uefi)) with { Bios = "seabios" });
        Assert.Null(back.Root!.Element("os")!.Element("firmware"));
        Assert.Null(back.Root.Element("os")!.Element("loader"));
    }

    [Fact]
    public void Secure_Boot_s_active_et_se_desactive_par_les_fonctions_du_microprogramme()
    {
        var on = VmXml.ApplySettings(Doc(), Settings(Read(Doc())) with { Bios = "ovmf", SecureBoot = true });
        Assert.True(Read(on).SecureBoot);
        Assert.NotNull(on.Root!.Element("features")!.Element("smm"));
        var off = VmXml.ApplySettings(on, Settings(Read(on)) with { SecureBoot = false });
        Assert.False(Read(off).SecureBoot);
        Assert.All(off.Root!.Element("os")!.Element("firmware")!.Elements("feature"), f => Assert.Equal("no", f.Attribute("enabled")!.Value));
        Assert.NotNull(Rules.ValidateVmSettings(Settings(Read(Doc())) with { Bios = "seabios", SecureBoot = true })); // exige UEFI
        Assert.NotNull(Rules.ValidateVmSettings(Settings(Read(Doc())) with { Bios = "ovmf", Machine = "pc", SecureBoot = true })); // exige q35
    }

    [Fact]
    public void Changer_de_type_de_machine_oublie_les_adresses_pci()
    {
        var d = VmXml.ApplySettings(Doc(), Settings(Read(Doc())) with { Machine = "pc" });
        Assert.Equal("pc", Read(d).Machine);
        Assert.Empty(d.Descendants("address"));
        Assert.Empty(d.Descendants("controller"));
        var same = VmXml.ApplySettings(Doc(), Settings(Read(Doc())));
        Assert.NotEmpty(same.Descendants("address")); // rien ne change si le type reste le même
    }

    [Fact]
    public void L_ordre_de_demarrage_remplace_l_ancien()
    {
        var d = VmXml.ApplySettings(Doc(), Settings(Read(Doc())) with { BootOrder = ["sda", "vda", "52:54:00:aa:bb:cc", "inconnu"] });
        Assert.Equal(["sda", "vda", "52:54:00:aa:bb:cc"], Read(d).BootOrder);
        Assert.Empty(d.Root!.Element("os")!.Elements("boot")); // l'ancienne règle « dev=cdrom » disparaît
    }

    [Fact]
    public void Ajoute_un_disque_sur_la_prochaine_cible_libre()
    {
        var (d, target) = VmXml.AddDisk(Doc(), "virtio", "/img/web-2.qcow2");
        Assert.Equal("vdb", target);
        Assert.Equal("sdb", VmXml.AddDisk(d, "sata", "/img/x.qcow2").Target);
        Assert.Equal("/img/web-2.qcow2", Read(d).Disks.Single(x => x.Target == "vdb").Source);
        Assert.Equal(2, Read(Doc()).Disks.Count); // l'original ne change pas
    }

    [Fact]
    public void Retire_un_disque_et_dit_quel_fichier_il_utilisait()
    {
        var (d, file, cdrom) = VmXml.RemoveDisk(Doc(), "vda");
        Assert.Equal(("/var/lib/libvirt/images/web.qcow2", false), (file, cdrom));
        Assert.DoesNotContain(Read(d).Disks, x => x.Target == "vda");
        Assert.Throws<OperationException>(() => VmXml.RemoveDisk(Doc(), "vdz"));
    }

    [Fact]
    public void Change_ou_ejecte_l_image_d_un_lecteur()
    {
        var d = VmXml.SetCdrom(Doc(), "sda", "/img/new.iso");
        Assert.Equal("/img/new.iso", Read(d).Disks.Single(x => x.Cdrom).Source);
        var empty = VmXml.SetCdrom(d, "sda", null);
        Assert.Null(Read(empty).Disks.Single(x => x.Cdrom).Source);
        Assert.Throws<OperationException>(() => VmXml.SetCdrom(Doc(), "vda", "/img/new.iso")); // pas un lecteur
    }

    [Fact]
    public void Ajoute_modifie_et_retire_des_cartes_reseau()
    {
        var d = VmXml.AddNic(Doc(), "e1000", null);
        var nics = Read(d).Nics;
        Assert.Equal(2, nics.Count);
        Assert.Equal(new VmNic("", "e1000", null), nics[1]); // l'adresse MAC est attribuée par libvirt à l'enregistrement

        var changed = VmXml.UpdateNic(d, "52:54:00:AA:BB:CC", "e1000e", null);
        Assert.Equal(new VmNic("52:54:00:aa:bb:cc", "e1000e", null), Read(changed).Nics[0]); // la MAC est conservée

        var removed = VmXml.RemoveNic(Doc(), "52:54:00:aa:bb:cc");
        Assert.Empty(Read(removed).Nics);
        Assert.Throws<OperationException>(() => VmXml.RemoveNic(Doc(), "52:54:00:00:00:01"));
    }

    [Fact]
    public void Une_carte_sur_un_pont_open_vswitch_porte_un_virtualport()
    {
        var d = VmXml.AddNic(Doc(), "virtio", "ovsbr0", ovs: true);
        var nic = d.Descendants("interface").Last();
        Assert.Equal("bridge", (string?)nic.Attribute("type"));
        Assert.Equal("ovsbr0", (string?)nic.Element("source")!.Attribute("bridge"));
        Assert.Equal("openvswitch", (string?)nic.Element("virtualport")!.Attribute("type"));
        Assert.Equal(new VmNic("", "virtio", "ovsbr0"), Read(d).Nics[1]);

        var plain = VmXml.AddNic(Doc(), "virtio", "br0").Descendants("interface").Last();
        Assert.Null(plain.Element("virtualport"));

        var changed = VmXml.UpdateNic(Doc(), "52:54:00:AA:BB:CC", "virtio", "ovsbr0", ovs: true);
        Assert.Equal("openvswitch", (string?)changed.Descendants("interface").First().Element("virtualport")!.Attribute("type"));
    }

    [Theory]
    [InlineData(0, 2, 2048, "host-passthrough", "seabios", "q35")]
    [InlineData(1, 0, 2048, "host-passthrough", "seabios", "q35")]
    [InlineData(1, 2, 8, "host-passthrough", "seabios", "q35")]
    [InlineData(1, 2, 2048, "pentium", "seabios", "q35")]
    [InlineData(1, 2, 2048, "host-passthrough", "coreboot", "q35")]
    [InlineData(1, 2, 2048, "host-passthrough", "seabios", "isapc")]
    [InlineData(8, 16, 2048, "host-passthrough", "seabios", "q35")]
    public void Refuse_des_reglages_invalides(int sockets, int cores, int mem, string cpu, string bios, string machine) =>
        Assert.NotNull(Rules.ValidateVmSettings(new VmSettings("", sockets, cores, cpu, mem, bios, machine, false, false, false, [])));

    [Fact]
    public void Un_ordre_de_demarrage_ne_peut_contenir_que_des_disques_et_des_adresses_mac() =>
        Assert.NotNull(Rules.ValidateVmSettings(new VmSettings("", 1, 1, "qemu64", 512, "seabios", "q35", false, false, false, ["--evil"])));
}

public class VmSnapshotParserTests
{
    [Fact]
    public void Lit_la_date_de_creation_d_un_snapshot()
    {
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", VmParser.SnapshotCreated("<domainsnapshot><name>s1</name><creationTime>1788884707</creationTime></domainsnapshot>"));
        Assert.Equal("", VmParser.SnapshotCreated("<domainsnapshot/>"));
        Assert.Equal("", VmParser.SnapshotCreated("pas du xml"));
    }
}
