using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

/// <summary>Le libvirt simulé (mode fake) doit se comporter assez bien pour que le vrai <see cref="VmService"/> tourne dessus.</summary>
public class FakeVirtTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-fakevirt-" + Guid.NewGuid().ToString("N"));
    readonly VmService _svc;

    public FakeVirtTests()
    {
        Directory.CreateDirectory(_dir);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vm:DiskDir"] = Path.Combine(_dir, "images").Replace((char)92, '/'),
            ["Vm:NetDir"] = Path.Combine(_dir, "net"),
            ["Vm:KvmDevice"] = Path.Combine(_dir, "kvm"),
            ["Backup:Roots:0"] = Path.Combine(_dir, "mnt").Replace((char)92, '/'),
            ["Backup:RequireMountPoint"] = "false",
        }).Build();
        var run = new FakeCommandRunner();
        var users = new UserService(run);
        var acl = new AclService(run, users, new GroupService(run, users), cfg);
        _svc = new VmService(run, acl, new BackupService(run, acl, cfg, NullLogger<BackupService>.Instance, new StubLifetime()), cfg);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task Liste_trois_machines_dont_une_en_marche()
    {
        Assert.Equal(VmHostState.Ready, (await _svc.StatusAsync()).State);
        var list = await _svc.ListAsync();
        Assert.Equal(["alpine-test", "debian-12", "windows-11"], list.Select(v => v.Name).Order().ToArray());
        var web = list.Single(v => v.Name == "debian-12");
        Assert.Equal(("running", 4, 4096L), (web.State, web.Vcpus, web.MemoryMb));
        Assert.NotNull(web.VncPort);
        Assert.Null(list.Single(v => v.Name == "windows-11").VncPort);
    }

    [Fact]
    public async Task Lit_le_materiel_et_les_instantanes()
    {
        var cfg = await _svc.ConfigAsync("windows-11");
        Assert.Equal(("ovmf", "q35", true, true, 8192), (cfg.Bios, cfg.Machine, cfg.SecureBoot, cfg.Tpm, cfg.MemoryMb));
        Assert.Equal(120, cfg.Disks.Single(d => !d.Cdrom).SizeGb);
        Assert.Equal("/srv/iso/Win11.iso", cfg.Disks.Single(d => d.Cdrom).Source);
        var snaps = await _svc.SnapshotsAsync("debian-12");
        Assert.Equal(["installation-propre", "avant-mise-a-jour"], snaps.Select(s => s.Name).ToArray());
        Assert.True(snaps[1].Current);
        Assert.Equal("installation-propre", snaps[1].Parent);
    }

    [Fact]
    public async Task Modifie_les_reglages_d_une_machine_arretee_et_refuse_une_machine_en_marche()
    {
        var cfg = await _svc.ConfigAsync("alpine-test");
        await _svc.SaveSettingsAsync("alpine-test", new VmSettings("Nouvelle description", 1, 2, "qemu64", 1024, "seabios", "pc", false, false, true, cfg.BootOrder));
        var after = await _svc.ConfigAsync("alpine-test");
        Assert.Equal((1024, 2, "Nouvelle description", true), (after.MemoryMb, after.Cores, after.Description, after.Autostart));
        await Assert.ThrowsAsync<OperationException>(() => _svc.SaveSettingsAsync("debian-12", new VmSettings("", 1, 1, "qemu64", 512, "seabios", "pc", false, false, false, [])));
    }

    [Fact]
    public async Task Ajoute_une_carte_reseau_avec_une_adresse_mac_et_la_retire()
    {
        await _svc.AddNicAsync("alpine-test", new AddVmNicRequest("e1000", null));
        var nics = (await _svc.ConfigAsync("alpine-test")).Nics;
        Assert.Equal(2, nics.Count);
        Assert.StartsWith("52:54:00:", nics[1].Mac);
        await _svc.RemoveNicAsync("alpine-test", nics[1].Mac);
        Assert.Single((await _svc.ConfigAsync("alpine-test")).Nics);
    }

    [Fact]
    public async Task Demarre_arrete_et_supprime()
    {
        await _svc.ActionAsync("alpine-test", "start");
        Assert.Equal("running", (await _svc.ConfigAsync("alpine-test")).State);
        await Assert.ThrowsAsync<OperationException>(() => _svc.ActionAsync("alpine-test", "remove"));
        await _svc.ActionAsync("alpine-test", "stop");
        await _svc.ActionAsync("alpine-test", "remove");
        Assert.DoesNotContain(await _svc.ListAsync(), v => v.Name == "alpine-test");
    }

    [Fact]
    public async Task Cree_et_supprime_un_instantane()
    {
        await _svc.CreateSnapshotAsync("alpine-test", new CreateVmSnapshotRequest("avant-essai", null));
        Assert.Equal("avant-essai", Assert.Single(await _svc.SnapshotsAsync("alpine-test")).Name);
        await _svc.SnapshotActionAsync("alpine-test", "avant-essai", "delete");
        Assert.Empty(await _svc.SnapshotsAsync("alpine-test"));
    }
}
