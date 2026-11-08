using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class VmTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-vm-" + Guid.NewGuid().ToString("N"));
    readonly string _docs, _disks;
    readonly FakeRunner _run = new();
    readonly VmService _svc;

    const string DomInfo = "Id:             -\nName:           web\nState:          shut off\nCPU(s):         2\nMax memory:     2097152 KiB\n";

    public VmTests()
    {
        _docs = Path.Combine(_dir, "srv", "docs");
        _disks = Path.Combine(_dir, "images");
        Directory.CreateDirectory(Path.Combine(_docs, "iso"));
        Directory.CreateDirectory(Path.Combine(_dir, "net", "br0", "bridge"));
        Directory.CreateDirectory(Path.Combine(_dir, "net", "docker0", "bridge"));
        Directory.CreateDirectory(Path.Combine(_dir, "net", "eth0"));
        File.WriteAllText(Path.Combine(_docs, "iso", "debian.iso"), "x");
        File.WriteAllText(Path.Combine(_docs, "note.txt"), "x");

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vm:DiskDir"] = _disks.Replace((char)92, '/'),
            ["Vm:NetDir"] = Path.Combine(_dir, "net"),
            ["Vm:KvmDevice"] = Path.Combine(_dir, "kvm"),
            ["Vm:OvmfDir"] = Path.Combine(_dir, "no-ovmf"),
            ["Backup:Roots:0"] = Path.Combine(_dir, "mnt").Replace((char)92, '/'),
            ["Backup:RequireMountPoint"] = "false",
        }).Build();
        _run.Handler = c => (c.File, c.Args.FirstOrDefault(a => !a.StartsWith('-') && a != "qemu:///system")) switch
        {
            ("testparm", _) => new(0, _docs + "\n", ""),
            ("virsh", "list") => new(0, "web\n\ndb\n", ""),
            ("virsh", "dominfo") => new(0, DomInfo, ""),
            ("virsh", "vncdisplay") => new(0, ":2\n", ""),
            _ => new(0, "", ""),
        };
        var users = new UserService(_run);
        var acl = new AclService(_run, users, new GroupService(_run, users), cfg);
        _svc = new VmService(_run, acl, new BackupService(_run, acl, cfg, NullLogger<BackupService>.Instance, new StubLifetime()), cfg);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Lit_les_informations_et_le_port_vnc()
    {
        var info = VmParser.Info("web", DomInfo, 5L << 30, 5902);
        Assert.Equal(("shut off", 2, 2048L, 5L), (info.State, info.Vcpus, info.MemoryMb, info.DiskGb));
        Assert.Equal(5900, VmParser.VncPort(":0"));
        Assert.Equal(5902, VmParser.VncPort("127.0.0.1:2\n"));
        Assert.Null(VmParser.VncPort("erreur"));
    }

    [Fact]
    public async Task Liste_les_machines_avec_l_ecran_de_celles_qui_tournent()
    {
        var list = await _svc.ListAsync();
        Assert.Equal(["web", "db"], list.Select(m => m.Name));
        Assert.All(list, m => Assert.Null(m.VncPort)); // elles sont toutes arrêtées
    }

    [Fact]
    public async Task L_etat_signale_les_ponts_sans_ceux_de_docker()
    {
        var s = await _svc.StatusAsync();
        Assert.Equal(["br0"], s.Bridges);
        Assert.False(s.Kvm);
    }

    [Theory]
    [InlineData("start", "start")]
    [InlineData("shutdown", "shutdown")]
    [InlineData("stop", "destroy")]
    public async Task Les_actions_utilisent_la_bonne_commande_virsh(string action, string verb)
    {
        await _svc.ActionAsync("web", action);
        Assert.Contains(_run.Calls, c => c.File == "virsh" && c.Args.Contains(verb) && c.Args.Contains("web"));
    }

    [Theory]
    [InlineData("--evil")]
    [InlineData("a b")]
    [InlineData("")]
    public async Task Refuse_un_nom_de_machine_invalide(string name) =>
        await Assert.ThrowsAsync<OperationException>(() => _svc.ActionAsync(name, "start"));

    [Fact]
    public async Task Refuse_une_action_inconnue()
    {
        await Assert.ThrowsAsync<OperationException>(() => _svc.ActionAsync("web", "format"));
        Assert.DoesNotContain(_run.Calls, c => c.File == "virsh");
    }

    [Fact]
    public async Task Supprime_une_machine_arretee_et_son_disque_sans_toucher_a_l_image()
    {
        Directory.CreateDirectory(_disks);
        var disk = Path.Combine(_disks, "web.qcow2");
        File.WriteAllText(disk, "d");
        await _svc.ActionAsync("web", "remove");
        Assert.Contains(_run.Calls, c => c.File == "virsh" && c.Args.Contains("undefine") && c.Args.Contains("--nvram"));
        Assert.False(File.Exists(disk));
        Assert.True(File.Exists(Path.Combine(_docs, "iso", "debian.iso")));
    }

    [Fact]
    public async Task Refuse_de_supprimer_une_machine_en_marche()
    {
        _run.Handler = c => c.Args.Contains("dominfo") ? new(0, DomInfo.Replace("shut off", "running"), "") : new(0, "", "");
        await Assert.ThrowsAsync<OperationException>(() => _svc.ActionAsync("web", "remove"));
        Assert.DoesNotContain(_run.Calls, c => c.Args.Contains("undefine"));
    }

    [Fact]
    public async Task Trouve_les_fichiers_iso_des_partages()
    {
        var isos = await _svc.IsosAsync(["docs"]);
        var iso = Assert.Single(isos);
        Assert.Equal(("docs", "iso/debian.iso"), (iso.Location, iso.Path));
    }

    [Fact]
    public async Task Cree_une_machine_avec_un_disque_qcow2_le_pont_et_l_image()
    {
        await _svc.CreateAsync(new CreateVmRequest("test", 2, 2048, 20, "docs", "iso/debian.iso", "br0"));
        var call = Assert.Single(_run.Calls, c => c.File == "virt-install");
        Assert.Contains("bridge=br0", call.Args);
        Assert.Contains("2048", call.Args);
        Assert.Contains(call.Args, a => a.StartsWith("path=") && a.EndsWith("test.qcow2,size=20,format=qcow2"));
        Assert.Contains(call.Args, a => a.EndsWith("test-install.iso")); // copie lisible par qemu
        Assert.Contains("--noautoconsole", call.Args);
    }

    [Fact]
    public async Task Sans_pont_la_machine_utilise_le_reseau_nat_de_libvirt()
    {
        await _svc.CreateAsync(new CreateVmRequest("test", 1, 512, 8, "docs", "iso/debian.iso", null));
        Assert.Contains("network=default", Assert.Single(_run.Calls, c => c.File == "virt-install").Args);
    }

    [Theory]
    [InlineData("web", 1, 512, 8, "docs", "iso/debian.iso")]        // nom déjà pris
    [InlineData("x", 0, 512, 8, "docs", "iso/debian.iso")]          // processeurs
    [InlineData("x", 1, 10, 8, "docs", "iso/debian.iso")]           // mémoire
    [InlineData("x", 1, 512, 0, "docs", "iso/debian.iso")]          // disque
    [InlineData("x", 1, 512, 8, "docs", "iso/note.txt")]            // pas une ISO
    [InlineData("x", 1, 512, 8, "docs", "../../etc/passwd.iso")]    // sort du partage
    [InlineData("x", 1, 512, 8, "/etc", "debian.iso")]              // pas un disque monté
    [InlineData("x", 1, 512, 8, "docs", "iso/absent.iso")]          // introuvable
    public async Task Refuse_une_creation_invalide(string name, int cpus, int mem, int disk, string location, string path)
    {
        await Assert.ThrowsAsync<OperationException>(() => _svc.CreateAsync(new CreateVmRequest(name, cpus, mem, disk, location, path, null)));
        Assert.DoesNotContain(_run.Calls, c => c.File == "virt-install");
    }
}
