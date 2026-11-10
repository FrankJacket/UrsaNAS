using Microsoft.Extensions.Configuration;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class RemoteRulesTests
{
    static AddRemoteRequest Smb(string path = "photos", string? user = null, string? pass = null) =>
        new("nas1", "smb", "192.168.1.10", path, user, pass, null, null, false);

    static AddRemoteRequest Nfs(string path = "/volume1/photos") => new("nas1", "nfs", "srv.local", path, null, null, null, null, false);

    [Fact]
    public void Une_connexion_correcte_est_acceptee()
    {
        Assert.Null(Rules.ValidateRemote(Smb()));
        Assert.Null(Rules.ValidateRemote(Smb("photos/2024", "jean", "secret")));
        Assert.Null(Rules.ValidateRemote(Nfs()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/photos")]          // un partage SMB n'a pas de / au début
    [InlineData("a/../b")]
    [InlineData("a,b")]               // une virgule ajouterait une option de montage
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    public void Un_partage_smb_invalide_est_refuse(string path) => Assert.NotNull(Rules.ValidateRemote(Smb(path)));

    [Theory]
    [InlineData("volume1")]           // un chemin NFS commence par /
    [InlineData("/a/../b")]
    [InlineData("/a,b")]
    public void Un_chemin_nfs_invalide_est_refuse(string path) => Assert.NotNull(Rules.ValidateRemote(Nfs(path)));

    [Theory]
    [InlineData("a b")]
    [InlineData("srv;rm")]
    [InlineData("-o")]
    public void Un_nom_de_serveur_invalide_est_refuse(string host) => Assert.NotNull(Rules.ValidateRemote(Smb() with { Host = host }));

    [Fact]
    public void Un_utilisateur_smb_exige_un_mot_de_passe_sans_saut_de_ligne()
    {
        Assert.NotNull(Rules.ValidateRemote(Smb(user: "jean")));
        Assert.NotNull(Rules.ValidateRemote(Smb(user: "jean", pass: "a\nb")));
        Assert.NotNull(Rules.ValidateRemote(Smb(user: "je,an", pass: "x")));
    }

    [Fact]
    public void Le_nom_la_version_et_le_type_sont_controles()
    {
        Assert.NotNull(Rules.ValidateRemote(Smb() with { Name = "../x" }));
        Assert.NotNull(Rules.ValidateRemote(Smb() with { Type = "ftp" }));
        Assert.NotNull(Rules.ValidateRemote(Smb() with { Version = "9.9" }));
        Assert.Null(Rules.ValidateRemote(Smb() with { Version = "3.0" }));
        Assert.NotNull(Rules.ValidateRemote(Nfs() with { Version = "3.0" })); // « 3.0 » est une version SMB
    }
}

public class RemoteServiceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-remote-" + Guid.NewGuid().ToString("N"));
    readonly string _fstab, _root, _creds, _bin, _mountInfo;
    readonly FakeRunner _run = new();
    readonly RemoteService _svc;

    static string Fwd(string p) => p.Replace((char)92, '/');

    public RemoteServiceTests()
    {
        _fstab = Path.Combine(_dir, "fstab");
        _root = Path.Combine(_dir, "remote");
        _creds = Path.Combine(_dir, "creds");
        _bin = Path.Combine(_dir, "bin");
        _mountInfo = Path.Combine(_dir, "mountinfo");
        Directory.CreateDirectory(_bin);
        File.WriteAllText(_fstab, "UUID=abc / ext4 defaults 0 1\n");
        File.WriteAllText(Path.Combine(_bin, "mount.cifs"), "");
        File.WriteAllText(Path.Combine(_bin, "mount.nfs"), "");
        File.WriteAllText(_mountInfo, "");
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Disks:Fstab"] = Fwd(_fstab), ["Remote:MountRoot"] = Fwd(_root), ["Remote:CredDir"] = Fwd(_creds),
            ["Remote:BinDirs"] = Fwd(_bin), ["Remote:MountInfo"] = Fwd(_mountInfo),
        }).Build();
        _svc = new RemoteService(_run, cfg);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static AddRemoteRequest Smb(string name = "photos") => new(name, "smb", "192.168.1.10", "media/photos", "jean", "s3cret", "HOME", "3.0", false);

    string Target(string name) => Fwd(Path.Combine(_root, name));

    [Fact]
    public async Task Ajoute_une_connexion_smb_monte_puis_l_ecrit_dans_fstab_sans_le_mot_de_passe()
    {
        await _svc.AddAsync(Smb());

        var mount = Assert.Single(_run.Calls, c => c.File == "mount");
        Assert.Equal(["-t", "cifs", "//192.168.1.10/media/photos", Target("photos")], mount.Args[..4]);
        var options = mount.Args[5];
        Assert.Contains("credentials=", options);
        Assert.Contains("vers=3.0", options);
        Assert.Contains("nofail", options);
        Assert.Contains("_netdev", options);
        Assert.DoesNotContain("s3cret", string.Join(' ', mount.Args));

        var fstab = File.ReadAllText(_fstab);
        Assert.Contains("# ursanas-remote", fstab);
        Assert.Contains("//192.168.1.10/media/photos " + Target("photos") + " cifs ", fstab);
        Assert.DoesNotContain("s3cret", fstab);
        Assert.Contains("UUID=abc / ext4", fstab); // le reste est intact

        var cred = File.ReadAllText(Path.Combine(_creds, "photos.cred"));
        Assert.Contains("username=jean", cred);
        Assert.Contains("password=s3cret", cred);
        Assert.Contains("domain=HOME", cred);
    }

    [Fact]
    public async Task Une_connexion_nfs_n_a_pas_de_fichier_d_identifiants()
    {
        await _svc.AddAsync(new AddRemoteRequest("films", "nfs", "10.0.0.5", "/volume1/films", null, null, null, "4.1", true));

        var mount = Assert.Single(_run.Calls, c => c.File == "mount");
        Assert.Equal(["-t", "nfs", "10.0.0.5:/volume1/films"], mount.Args[..3]);
        Assert.Contains("ro", mount.Args[5].Split(','));
        Assert.Contains("vers=4.1", mount.Args[5]);
        Assert.False(Directory.Exists(_creds));
    }

    [Fact]
    public async Task L_acces_invite_utilise_guest_sans_fichier_d_identifiants()
    {
        await _svc.AddAsync(new AddRemoteRequest("pub", "smb", "srv", "public", null, null, null, null, false));
        Assert.Contains("guest", _run.Calls.Single(c => c.File == "mount").Args[5].Split(','));
        Assert.False(File.Exists(Path.Combine(_creds, "pub.cred")));
    }

    [Fact]
    public async Task La_liste_relit_fstab_et_l_etat_du_montage()
    {
        await _svc.AddAsync(Smb());
        await _svc.AddAsync(new AddRemoteRequest("v6", "nfs", "fe80::1", "/export dir", null, null, null, null, false));

        var list = _svc.List();
        Assert.Equal(["photos", "v6"], list.Select(r => r.Name));
        var smb = list[0];
        Assert.Equal(("smb", "192.168.1.10", "media/photos", "jean", "HOME", "3.0"), (smb.Type, smb.Host, smb.Path, smb.User, smb.Domain, smb.Version));
        Assert.False(smb.Mounted);
        var nfs = list[1];
        Assert.Equal(("nfs", "fe80::1", "/export dir"), (nfs.Type, nfs.Host, nfs.Path)); // adresse IPv6 et espace ressortent intacts

        File.WriteAllText(_mountInfo, $"36 35 0:40 / {Target("photos")} rw,relatime - cifs //192.168.1.10/media/photos rw\n");
        Assert.True(_svc.List()[0].Mounted);
    }

    [Fact]
    public async Task Un_montage_en_echec_ne_laisse_rien()
    {
        _run.Handler = c => c.File == "mount" ? new(32, "", "mount error(13): Permission denied") : new(0, "", "");

        var e = await Assert.ThrowsAsync<OperationException>(() => _svc.AddAsync(Smb()));

        Assert.Contains("Permission denied", e.Message);
        Assert.DoesNotContain("ursanas-remote", File.ReadAllText(_fstab));
        Assert.False(File.Exists(Path.Combine(_creds, "photos.cred")));
        Assert.False(Directory.Exists(Path.Combine(_root, "photos")));
        Assert.Empty(_svc.List());
    }

    [Fact]
    public async Task Refuse_un_nom_deja_pris_et_un_programme_absent()
    {
        await _svc.AddAsync(Smb());
        await Assert.ThrowsAsync<OperationException>(() => _svc.AddAsync(Smb()));

        File.Delete(Path.Combine(_bin, "mount.nfs"));
        await Assert.ThrowsAsync<OperationException>(() => _svc.AddAsync(new AddRemoteRequest("x", "nfs", "h", "/p", null, null, null, null, false)));
        Assert.Equal(["smb"], _svc.Types().Available);
    }

    [Fact]
    public async Task Tester_ne_garde_rien_et_demonte()
    {
        File.WriteAllText(_mountInfo, "");
        await _svc.TestAsync(Smb());

        Assert.Contains(_run.Calls, c => c.File == "mount");
        Assert.Contains(_run.Calls, c => c.File == "umount");
        Assert.DoesNotContain("ursanas-remote", File.ReadAllText(_fstab));
        Assert.Empty(Directory.GetFiles(_creds));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task Supprimer_demonte_efface_la_ligne_fstab_et_le_mot_de_passe()
    {
        await _svc.AddAsync(Smb());
        File.WriteAllText(_mountInfo, $"36 35 0:40 / {Target("photos")} rw - cifs //x/y rw\n");

        await _svc.RemoveAsync("photos");

        Assert.Contains(_run.Calls, c => c.File == "umount" && c.Args[0] == Target("photos"));
        var fstab = File.ReadAllText(_fstab);
        Assert.DoesNotContain("ursanas-remote", fstab);
        Assert.Contains("UUID=abc / ext4", fstab);
        Assert.False(File.Exists(Path.Combine(_creds, "photos.cred")));
        await Assert.ThrowsAsync<OperationException>(() => _svc.RemoveAsync("photos"));
    }

    [Fact]
    public async Task Monter_et_demonter_une_connexion_enregistree_passent_par_fstab()
    {
        await _svc.AddAsync(Smb());
        _run.Calls.Clear();

        await _svc.MountAsync("photos");
        var mount = Assert.Single(_run.Calls);
        Assert.Equal("mount", mount.File);
        Assert.Equal([Target("photos")], mount.Args); // la ligne fstab donne les options

        File.WriteAllText(_mountInfo, $"36 35 0:40 / {Target("photos")} rw - cifs //x/y rw\n");
        _run.Calls.Clear();
        await _svc.MountAsync("photos"); // déjà montée : rien à faire
        Assert.Empty(_run.Calls);
        await _svc.UnmountAsync("photos");
        Assert.Equal("umount", Assert.Single(_run.Calls).File);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("inconnu")]
    public async Task Un_nom_inconnu_ou_dangereux_est_refuse(string name)
    {
        await Assert.ThrowsAsync<OperationException>(() => _svc.MountAsync(name));
        await Assert.ThrowsAsync<OperationException>(() => _svc.RemoveAsync(name));
        Assert.Empty(_run.Calls);
    }
}
