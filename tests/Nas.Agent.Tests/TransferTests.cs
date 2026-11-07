using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class TransferTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-transfer-" + Guid.NewGuid().ToString("N"));
    readonly string _docs, _photos, _usb;
    readonly FakeRunner _run = new();
    readonly TransferService _svc;

    string Usb => _usb.Replace((char)92, '/');

    public TransferTests()
    {
        var roots = Path.Combine(_dir, "mnt");
        _usb = Path.Combine(roots, "usb");
        _docs = Path.Combine(_dir, "srv", "docs");
        _photos = Path.Combine(_dir, "srv", "photos");
        Directory.CreateDirectory(Path.Combine(_docs, "2024"));
        Directory.CreateDirectory(_photos);
        Directory.CreateDirectory(_usb);
        File.WriteAllText(Path.Combine(_docs, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(_docs, "2024", "b.txt"), "beta");

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Roots:0"] = roots.Replace((char)92, '/'),
            ["Backup:RequireMountPoint"] = "false",
        }).Build();
        _run.Handler = c => c.File switch
        {
            "testparm" when c.Args.Contains("--parameter-name=vfs objects") => new(0, c.Args.Any(a => a.EndsWith("=photos")) ? "\n" : "acl_xattr\n", ""), // photos est un partage ordinaire
            "testparm" => new(0, (c.Args.Any(a => a.EndsWith("=photos")) ? _photos : _docs) + "\n", ""),
            "pdbedit" => new(0, "User SID:       S-1-5-21-1-2-3-1000\n", ""),
            _ => new(0, "", ""),
        };
        var users = new UserService(_run);
        var acl = new AclService(_run, users, new GroupService(_run, users), cfg);
        _svc = new TransferService(acl, new BackupService(_run, acl, cfg, NullLogger<BackupService>.Instance, new StubLifetime()));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task Copie_un_dossier_d_un_partage_vers_un_disque_sans_toucher_a_l_original()
    {
        await _svc.CopyAsync(new CopyFolderRequest("docs", "2024", "", "2024", false, Usb));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(_usb, "2024", "b.txt")));
        Assert.True(File.Exists(Path.Combine(_docs, "2024", "b.txt")));
        Assert.DoesNotContain(_run.Calls, c => c.File == "chgrp"); // un disque n'a ni groupe NAS ni ACL
    }

    [Fact]
    public async Task Deplace_un_fichier_d_un_disque_vers_un_partage_puis_donne_les_droits_du_partage()
    {
        File.WriteAllText(Path.Combine(_usb, "photo.jpg"), "pic");
        await _svc.MoveAsync(new MoveFolderRequest(Usb, "photo.jpg", "2024", "photo.jpg", true, "docs"));

        Assert.Equal("pic", File.ReadAllText(Path.Combine(_docs, "2024", "photo.jpg")));
        Assert.False(File.Exists(Path.Combine(_usb, "photo.jpg")));
        Assert.Contains(_run.Calls, c => c.File == "chgrp");
        Assert.Contains(_run.Calls, c => c.File == "smbcacls"); // ACL neutre avant l'héritage de la destination
    }

    [Fact]
    public async Task Un_partage_ordinaire_recoit_les_droits_unix_sans_acl()
    {
        await _svc.CopyAsync(new CopyFolderRequest("docs", "2024", "", "2024", false, "photos"));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(_photos, "2024", "b.txt")));
        Assert.Contains(_run.Calls, c => c.File == "chgrp");
        Assert.DoesNotContain(_run.Calls, c => c.File == "smbcacls"); // pas d'ACL à réinitialiser
    }

    [Fact]
    public async Task Cree_un_dossier_dans_un_partage_ordinaire_sans_samba()
    {
        var acl = new AclService(_run, new UserService(_run), new GroupService(_run, new UserService(_run)), new ConfigurationBuilder().Build());
        await acl.CreateFolderAsync(new CreateFolderRequest("photos", "", "vacances"));
        Assert.True(Directory.Exists(Path.Combine(_photos, "vacances")));
        Assert.Contains(_run.Calls, c => c.File == "chmod");
        Assert.DoesNotContain(_run.Calls, c => c.File == "smbclient");
        await Assert.ThrowsAsync<OperationException>(() => acl.CreateFolderAsync(new CreateFolderRequest("photos", "", "VACANCES")));
    }

    [Fact]
    public async Task Deplace_un_dossier_d_un_partage_a_un_autre()
    {
        await _svc.MoveAsync(new MoveFolderRequest("docs", "2024", "", "archive", false, "photos"));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(_photos, "archive", "b.txt")));
        Assert.False(Directory.Exists(Path.Combine(_docs, "2024")));
    }

    [Fact]
    public async Task Refuse_un_nom_deja_pris_sans_rien_supprimer()
    {
        File.WriteAllText(Path.Combine(_usb, "A.TXT"), "x");
        await Assert.ThrowsAsync<OperationException>(() => _svc.MoveAsync(new MoveFolderRequest("docs", "a.txt", "", "a.txt", true, Usb)));
        Assert.True(File.Exists(Path.Combine(_docs, "a.txt")));
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("../x")]
    [InlineData("pas un partage")]
    public async Task Refuse_une_destination_qui_n_est_ni_un_partage_ni_un_disque_monte(string dest) =>
        await Assert.ThrowsAsync<OperationException>(() => _svc.CopyAsync(new CopyFolderRequest("docs", "a.txt", "", "a.txt", true, dest)));

    [Fact]
    public async Task Refuse_un_chemin_qui_sort_de_l_emplacement()
    {
        await Assert.ThrowsAsync<OperationException>(() => _svc.CopyAsync(new CopyFolderRequest("docs", "../a.txt", "", "a.txt", true, Usb)));
        await Assert.ThrowsAsync<OperationException>(() => _svc.CopyAsync(new CopyFolderRequest("docs", "a.txt", "../../x", "a.txt", true, Usb)));
        await Assert.ThrowsAsync<OperationException>(() => _svc.CopyAsync(new CopyFolderRequest("docs", "", "", "x", false, Usb))); // la racine
    }
}
