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

public class RemoteCloudRulesTests
{
    const string Token = "{\"access_token\":\"abc\",\"refresh_token\":\"def\",\"expiry\":\"2026-11-10T16:00:00Z\"}";

    static AddRemoteRequest Cloud(string? token = Token, string path = "", string cache = "full", int gb = 10, int bw = 0, string type = "onedrive") =>
        new("c1", type, "", path, null, null, null, null, false, token, cache, gb, bw);

    [Fact]
    public void Un_compte_en_ligne_correct_est_accepte()
    {
        Assert.Null(Rules.ValidateRemote(Cloud()));
        Assert.Null(Rules.ValidateRemote(Cloud(type: "dropbox", path: "Documents/Mes photos", cache: "off", bw: 10)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pas du json")]
    [InlineData("[1,2]")]
    [InlineData("{\"refresh_token\":\"x\"}")]          // pas d'access_token
    [InlineData("{\"access_token\":\"\"}")]
    public void Un_jeton_invalide_est_refuse(string? token) => Assert.NotNull(Rules.ValidateRemote(Cloud(token)));

    [Fact]
    public void Le_jeton_est_remis_sur_une_seule_ligne()
    {
        Assert.True(Rules.IsRcloneToken("  {\n  \"access_token\": \"abc\",\n  \"expiry\": \"x\"\n}\n", out var compact));
        Assert.DoesNotContain('\n', compact);
        Assert.StartsWith("{", compact);
    }

    [Theory]
    [InlineData("/Documents")]
    [InlineData("a/../b")]
    [InlineData("a%20b")]       // « % » et « $ » sont interprétés par systemd dans l'unité
    [InlineData("a$b")]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("a\nb")]
    public void Un_sous_dossier_dangereux_est_refuse(string path) => Assert.NotNull(Rules.ValidateRemote(Cloud(path: path)));

    [Fact]
    public void Le_cache_et_le_debit_sont_bornes()
    {
        Assert.NotNull(Rules.ValidateRemote(Cloud(cache: "all")));
        Assert.NotNull(Rules.ValidateRemote(Cloud(gb: 0)));
        Assert.NotNull(Rules.ValidateRemote(Cloud(gb: 5000)));
        Assert.NotNull(Rules.ValidateRemote(Cloud(bw: -1)));
    }
}

public class RemoteServiceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-remote-" + Guid.NewGuid().ToString("N"));
    readonly string _fstab, _root, _creds, _bin, _mountInfo, _units, _cache;
    readonly FakeRunner _run = new();
    readonly StubDrive _drive = new();
    readonly RemoteService _svc;

    sealed class StubDrive : IDriveLookup
    {
        public int Calls;
        public string? Token;
        public Task<(string Id, string Type)> OneDriveAsync(string accessToken, CancellationToken ct)
        {
            Calls++;
            Token = accessToken;
            return Task.FromResult(("DRIVE123", "personal"));
        }
    }

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
        File.WriteAllText(Path.Combine(_bin, "rclone"), "");
        File.WriteAllText(Path.Combine(_bin, "fusermount3"), "");
        File.WriteAllText(_mountInfo, "");
        _units = Path.Combine(_dir, "units");
        _cache = Path.Combine(_dir, "cache");
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Disks:Fstab"] = Fwd(_fstab), ["Remote:MountRoot"] = Fwd(_root), ["Remote:CredDir"] = Fwd(_creds),
            ["Remote:BinDirs"] = Fwd(_bin), ["Remote:MountInfo"] = Fwd(_mountInfo),
            ["Remote:UnitDir"] = Fwd(_units), ["Remote:CacheDir"] = Fwd(_cache),
            ["Remote:OneDriveClientId"] = "11111111-2222-3333-4444-555555555555", ["Remote:DropboxAppKey"] = "dbxkey1234567",
        }).Build();
        _svc = new RemoteService(_run, cfg, _drive);
    }

    const string Token = "{\"access_token\":\"AT-SECRET\",\"token_type\":\"Bearer\",\"refresh_token\":\"RT-SECRET\",\"expiry\":\"2026-11-10T16:00:00Z\"}";

    static AddRemoteRequest Cloud(string type = "onedrive", string name = "cloud") =>
        new(name, type, "", "Documents/Photos", null, null, null, null, false, Token, "full", 10, 0);

    string Unit(string name) => File.ReadAllText(Path.Combine(_units, $"ursanas-rclone-{name}.service"));

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
        Assert.DoesNotContain("nfs", _svc.Types().Available);
        Assert.Contains("smb", _svc.Types().Available);
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

    // ---- OneDrive et Dropbox

    [Fact]
    public async Task Ajoute_un_onedrive_avec_son_disque_son_unite_systemd_et_sans_fuite_du_jeton()
    {
        await _svc.AddAsync(Cloud() with { ReadOnly = true, CacheGb = 20, BwLimitMbps = 5 });

        Assert.Equal("AT-SECRET", _drive.Token); // le disque du compte est retrouvé avec le jeton
        var conf = File.ReadAllText(Path.Combine(_creds, "cloud.rclone.conf"));
        Assert.Contains("[cloud]", conf);
        Assert.Contains("type = onedrive", conf);
        Assert.Contains("drive_id = DRIVE123", conf);
        Assert.Contains("drive_type = personal", conf);
        Assert.Contains("RT-SECRET", conf);

        var lsd = Assert.Single(_run.Calls, c => c.File.EndsWith("rclone") && c.Args[0] == "lsd"); // un compte inaccessible est refusé d'abord
        Assert.Equal("cloud:Documents/Photos", lsd.Args[1]);

        var unit = Unit("cloud");
        Assert.Contains("mount \"cloud:Documents/Photos\" \"" + Target("cloud") + "\"", unit);
        Assert.Contains("--vfs-cache-mode full", unit);
        Assert.Contains("--vfs-cache-max-size 20G", unit);
        Assert.Contains("--read-only", unit);
        Assert.Contains("--bwlimit 5M", unit);
        Assert.Contains("Type=notify", unit);
        Assert.Contains("fusermount3 -uz", unit);
        Assert.DoesNotContain("SECRET", unit); // le jeton n'est que dans le fichier rclone

        Assert.Contains(_run.Calls, c => c.File == "systemctl" && c.Args[0] == "daemon-reload");
        Assert.Contains(_run.Calls, c => c.File == "systemctl" && c.Args.SequenceEqual(["enable", "--now", "ursanas-rclone-cloud.service"]));
        Assert.DoesNotContain("SECRET", string.Join(' ', _run.Calls.SelectMany(c => c.Args)));
        Assert.DoesNotContain("ursanas-remote", File.ReadAllText(_fstab)); // pas de ligne fstab : c'est systemd qui remonte
    }

    [Fact]
    public async Task Un_jeton_de_la_connexion_integree_ecrit_l_application_pour_que_rclone_le_renouvelle()
    {
        await _svc.AddAsync(Cloud() with { OwnApp = true });
        Assert.Contains("client_id = 11111111-2222-3333-4444-555555555555", File.ReadAllText(Path.Combine(_creds, "cloud.rclone.conf")));

        await _svc.AddAsync(Cloud(name: "autre"));
        Assert.DoesNotContain("client_id", File.ReadAllText(Path.Combine(_creds, "autre.rclone.conf"))); // jeton collé : rclone garde son application
    }

    [Fact]
    public async Task Un_jeton_dropbox_de_la_connexion_integree_ecrit_la_cle_de_l_application()
    {
        await _svc.AddAsync(Cloud("dropbox") with { OwnApp = true });
        Assert.Contains("client_id = dbxkey1234567", File.ReadAllText(Path.Combine(_creds, "cloud.rclone.conf")));
    }

    // ---- Modification

    [Fact]
    public async Task Modifier_une_connexion_smb_garde_le_mot_de_passe_laisse_vide_et_remplace_la_ligne_fstab()
    {
        await _svc.AddAsync(Smb());
        _run.Calls.Clear();

        await _svc.UpdateAsync(Smb() with { Path = "media/films", Password = null, ReadOnly = true });

        Assert.Contains("password=s3cret", File.ReadAllText(Path.Combine(_creds, "photos.cred"))); // gardé
        var fstab = File.ReadAllText(_fstab);
        Assert.Single(fstab.Split('\n'), l => l.Contains("# ursanas-remote")); // remplacée, pas ajoutée
        Assert.Contains("//192.168.1.10/media/films " + Target("photos") + " cifs ", fstab);
        Assert.DoesNotContain("media/photos", fstab);
        Assert.Contains("ro", Assert.Single(_run.Calls, c => c.File == "mount").Args[5].Split(','));
        Assert.Equal("films", _svc.List().Single().Path.Split('/')[1]);
    }

    [Fact]
    public async Task Modifier_une_connexion_smb_en_echec_remet_les_anciens_identifiants_et_la_ligne()
    {
        await _svc.AddAsync(Smb());
        var before = File.ReadAllText(_fstab);
        _run.Handler = c => c.File == "mount" ? new(32, "", "mount error(13): Permission denied") : new(0, "", "");

        await Assert.ThrowsAsync<OperationException>(() => _svc.UpdateAsync(Smb() with { User = "autre", Password = "nouveau" }));

        Assert.Equal(before, File.ReadAllText(_fstab));
        var cred = File.ReadAllText(Path.Combine(_creds, "photos.cred"));
        Assert.Contains("username=jean", cred);
        Assert.DoesNotContain("nouveau", cred);
    }

    [Fact]
    public async Task Le_type_et_le_mot_de_passe_obligatoire_d_un_nouvel_utilisateur_sont_controles_a_la_modification()
    {
        await _svc.AddAsync(Smb());
        await Assert.ThrowsAsync<OperationException>(() => _svc.UpdateAsync(new AddRemoteRequest("photos", "nfs", "srv", "/x", null, null, null, null, false))); // autre type
        await Assert.ThrowsAsync<OperationException>(() => _svc.UpdateAsync(Smb(name: "absent")));
        Assert.Null(Rules.ValidateRemote(Smb() with { Password = null }, update: true));
        Assert.NotNull(Rules.ValidateRemote(Smb() with { Password = null }));
    }

    [Fact]
    public async Task Modifier_un_onedrive_sans_nouveau_jeton_garde_la_configuration_et_relance_l_unite()
    {
        await _svc.AddAsync(Cloud() with { OwnApp = true });
        var conf = File.ReadAllText(Path.Combine(_creds, "cloud.rclone.conf"));
        _drive.Calls = 0;
        _run.Calls.Clear();

        await _svc.UpdateAsync(Cloud() with { Token = null, Path = "Autre", BwLimitMbps = 8, Cache = "writes" });

        Assert.Equal(conf, File.ReadAllText(Path.Combine(_creds, "cloud.rclone.conf"))); // jeton, client_id et disque intacts
        Assert.Equal(0, _drive.Calls);
        Assert.Equal("cloud:Autre", Assert.Single(_run.Calls, c => c.Args.Length > 0 && c.Args[0] == "lsd").Args[1]); // le dossier est essayé d'abord
        var unit = Unit("cloud");
        Assert.Contains("mount \"cloud:Autre\"", unit);
        Assert.Contains("--bwlimit 8M", unit);
        Assert.Contains("--vfs-cache-mode writes", unit);
        Assert.Contains(_run.Calls, c => c.File == "systemctl" && c.Args.SequenceEqual(["restart", "ursanas-rclone-cloud.service"]));
        Assert.Equal("Autre", _svc.List().Single().Path);
    }

    [Fact]
    public async Task Modifier_un_onedrive_avec_un_nouveau_jeton_le_remplace()
    {
        await _svc.AddAsync(Cloud());
        const string other = "{\"access_token\":\"AT-NEW\",\"token_type\":\"Bearer\",\"refresh_token\":\"RT-NEW\",\"expiry\":\"2026-11-10T16:00:00Z\"}";

        await _svc.UpdateAsync(Cloud() with { Token = other, OwnApp = true });

        var conf = File.ReadAllText(Path.Combine(_creds, "cloud.rclone.conf"));
        Assert.Contains("RT-NEW", conf);
        Assert.DoesNotContain("RT-SECRET", conf);
        Assert.Contains("client_id = 11111111", conf);
        Assert.False(File.Exists(Path.Combine(_creds, ".cloud.new.conf"))); // pas de fichier temporaire oublié
    }

    [Fact]
    public async Task Modifier_un_dossier_inaccessible_ou_un_montage_en_echec_ne_change_rien()
    {
        await _svc.AddAsync(Cloud());
        var unit = Unit("cloud");
        var conf = File.ReadAllText(Path.Combine(_creds, "cloud.rclone.conf"));

        _run.Handler = c => c.Args.Length > 0 && c.Args[0] == "lsd" ? new(1, "", "directory not found") : new(0, "", "");
        await Assert.ThrowsAsync<OperationException>(() => _svc.UpdateAsync(Cloud() with { Token = null, Path = "Absent" }));
        Assert.Equal(unit, Unit("cloud"));

        _run.Handler = c => c.File == "systemctl" && c.Args[0] == "restart" && c.Args.Length > 1 && !_failedOnce ? Fail() : new(0, "", "");
        await Assert.ThrowsAsync<OperationException>(() => _svc.UpdateAsync(Cloud() with { Token = null, BwLimitMbps = 9 }));
        Assert.Equal(unit, Unit("cloud")); // l'ancienne unité est remise
        Assert.Equal(conf, File.ReadAllText(Path.Combine(_creds, "cloud.rclone.conf")));
        Assert.Equal(0, _svc.List().Single().BwLimitMbps);
    }

    bool _failedOnce;
    CommandResult Fail() { _failedOnce = true; return new(1, "", "Job failed"); }

    [Fact]
    public async Task Dropbox_n_interroge_pas_de_disque_et_le_cache_complet_est_optionnel()
    {
        await _svc.AddAsync(Cloud("dropbox") with { Path = "", Cache = "writes" });

        Assert.Equal(0, _drive.Calls);
        var conf = File.ReadAllText(Path.Combine(_creds, "cloud.rclone.conf"));
        Assert.Contains("type = dropbox", conf);
        Assert.DoesNotContain("drive_id", conf);
        Assert.Equal("cloud:", Assert.Single(_run.Calls, c => c.Args.Length > 0 && c.Args[0] == "lsd").Args[1]); // racine du compte
        var unit = Unit("cloud");
        Assert.Contains("--vfs-cache-mode writes", unit);
        Assert.DoesNotContain("--vfs-cache-max-size", unit);
        Assert.DoesNotContain("--bwlimit", unit);
    }

    [Fact]
    public async Task La_liste_montre_les_connexions_en_ligne_et_leurs_reglages()
    {
        await _svc.AddAsync(Smb());
        await _svc.AddAsync(Cloud() with { CacheGb = 30, BwLimitMbps = 2 });

        var list = _svc.List();
        Assert.Equal(["cloud", "photos"], list.Select(r => r.Name));
        var c = list[0];
        Assert.Equal(("onedrive", "", "Documents/Photos", "full", 30, 2), (c.Type, c.Host, c.Path, c.Cache, c.CacheGb, c.BwLimitMbps));
        Assert.False(c.Mounted);

        File.WriteAllText(_mountInfo, $"36 35 0:40 / {Target("cloud")} rw - fuse.rclone cloud: rw\n");
        Assert.True(_svc.List()[0].Mounted);
    }

    [Fact]
    public async Task Un_compte_inaccessible_ne_laisse_rien_et_l_erreur_ne_contient_pas_le_jeton()
    {
        _run.Handler = c => c.Args.Length > 0 && c.Args[0] == "lsd" ? new(1, "", "Failed to create file system: couldn't login: " + Token) : new(0, "", "");

        var e = await Assert.ThrowsAsync<OperationException>(() => _svc.AddAsync(Cloud()));

        Assert.DoesNotContain("SECRET", e.Message);
        Assert.Empty(Directory.GetFiles(_creds));
        Assert.False(Directory.Exists(_units) && Directory.GetFiles(_units).Length > 0);
        Assert.DoesNotContain(_run.Calls, c => c.File == "systemctl");
        Assert.Empty(_svc.List());
    }

    [Fact]
    public async Task Une_unite_qui_ne_demarre_pas_est_defaite_et_le_journal_explique_pourquoi()
    {
        _run.Handler = c => c.File switch
        {
            "systemctl" when c.Args[0] == "enable" => new(1, "", "Job for ursanas-rclone-cloud.service failed."),
            "journalctl" => new(0, "Failed to mount: fusermount3: fuse device not found\n", ""),
            _ => new(0, "", ""),
        };

        var e = await Assert.ThrowsAsync<OperationException>(() => _svc.AddAsync(Cloud()));

        Assert.Contains("fuse device not found", e.Message);
        Assert.Contains(_run.Calls, c => c.File == "systemctl" && c.Args.SequenceEqual(["disable", "--now", "ursanas-rclone-cloud.service"]));
        Assert.Empty(Directory.GetFiles(_creds));
        Assert.Empty(Directory.GetFiles(_units));
        Assert.Empty(_svc.List());
    }

    [Fact]
    public async Task Monter_demonter_et_supprimer_une_connexion_en_ligne_passent_par_systemd()
    {
        await _svc.AddAsync(Cloud());
        _run.Calls.Clear();

        await _svc.MountAsync("cloud");
        await _svc.UnmountAsync("cloud");
        Assert.Equal([["start", "ursanas-rclone-cloud.service"], ["stop", "ursanas-rclone-cloud.service"]],
            _run.Calls.Where(c => c.File == "systemctl").Select(c => c.Args).ToList());

        await _svc.RemoveAsync("cloud");
        Assert.Contains(_run.Calls, c => c.File == "systemctl" && c.Args.SequenceEqual(["disable", "--now", "ursanas-rclone-cloud.service"]));
        Assert.Empty(Directory.GetFiles(_creds));
        Assert.Empty(Directory.GetFiles(_units));
        Assert.False(Directory.Exists(Path.Combine(_cache, "cloud")));
        Assert.Empty(_svc.List());
    }

    [Fact]
    public async Task Tester_un_compte_ne_garde_rien()
    {
        await _svc.TestAsync(Cloud());

        Assert.Contains(_run.Calls, c => c.Args.Length > 0 && c.Args[0] == "lsd");
        Assert.DoesNotContain(_run.Calls, c => c.File == "systemctl");
        Assert.Empty(Directory.GetFiles(_creds));
        Assert.False(Directory.Exists(_units));
    }

    [Fact]
    public async Task Un_nom_est_unique_entre_smb_nfs_et_en_ligne_et_rclone_est_requis()
    {
        await _svc.AddAsync(Smb("shared"));
        await Assert.ThrowsAsync<OperationException>(() => _svc.AddAsync(Cloud(name: "shared")));

        File.Delete(Path.Combine(_bin, "fusermount3"));
        await Assert.ThrowsAsync<OperationException>(() => _svc.AddAsync(Cloud(name: "other")));
        Assert.DoesNotContain("onedrive", _svc.Types().Available);
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
