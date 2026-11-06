using Microsoft.Extensions.Configuration;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class ProtocolRulesTests
{
    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("192.168.1.0/24", true)]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd00::/64", true)]
    [InlineData("client.local", true)]
    [InlineData("nas", true)]
    [InlineData("", false)]
    [InlineData("*", false)]
    [InlineData("192.168.1.0/33", false)]
    [InlineData("192.168.1.0/", false)]
    [InlineData("300.1.1.1", false)]
    [InlineData("127.1", false)]
    [InlineData("a b", false)]
    [InlineData("host(rw,no_root_squash)", false)]
    [InlineData("a,b", false)]
    [InlineData("host/24", false)]
    [InlineData("fe80::1%eth0", false)]
    public void Valide_un_client_nfs(string client, bool ok) => Assert.Equal(ok, Rules.ValidateNfsClient(client) is null);

    static readonly IReadOnlySet<string> Known = new HashSet<string> { "alice", "bob" };

    [Fact]
    public void Un_export_nfs_demande_au_moins_un_client_sans_doublon()
    {
        Assert.NotNull(Rules.ValidateShares([new ShareDef("a", "", false, [], Acl: true, Nfs: new NfsExport([]))], Known));
        Assert.NotNull(Rules.ValidateShares([new ShareDef("a", "", false, [], Acl: true, Nfs: new NfsExport(["10.0.0.1", "10.0.0.1"]))], Known));
        Assert.Null(Rules.ValidateShares([new ShareDef("a", "", false, [], Acl: true, Nfs: new NfsExport(["10.0.0.1"]))], Known));
    }

    [Fact]
    public void Un_acces_ftp_demande_des_utilisateurs_connus()
    {
        Assert.NotNull(Rules.ValidateShares([new ShareDef("a", "", false, [], Acl: true, Ftp: new FtpAccess([]))], Known));
        Assert.NotNull(Rules.ValidateShares([new ShareDef("a", "", false, [], Acl: true, Ftp: new FtpAccess(["mallory"]))], Known));
        Assert.Null(Rules.ValidateShares([new ShareDef("a", "", false, [], Acl: true, Ftp: new FtpAccess(["alice"]))], Known));
    }

    [Theory]
    [InlineData(40000, 40100, true)]
    [InlineData(1023, 2000, false)]
    [InlineData(40100, 40000, false)]
    [InlineData(2000, 65536, false)]
    [InlineData(2000, 4000, false)]
    public void Valide_la_plage_de_ports_passifs(int min, int max, bool ok) =>
        Assert.Equal(ok, Rules.ValidateFtpSettings(new FtpSettings(false, min, max)) is null);
}

public class ProtocolConfTests
{
    static readonly Dictionary<string, string> Paths = new() { ["docs"] = "/srv/nas/docs", ["my share"] = "/tank/my share" };

    [Fact]
    public void Exports_nfs_ramene_tous_les_clients_au_groupe_nasusers()
    {
        var text = ProtocolConf.RenderExports(
            [new ShareDef("docs", "", false, [], Nfs: new NfsExport(["192.168.1.0/24", "fd00::/64"], ReadOnly: true)),
             new ShareDef("autre", "", false, [])], Paths, 990);

        Assert.Contains("/srv/nas/docs 192.168.1.0/24(ro,sync,no_subtree_check,all_squash,anonuid=65534,anongid=990) [fd00::]/64(ro,", text);
        Assert.DoesNotContain("autre", text);
        Assert.DoesNotContain("no_root_squash", text);
    }

    [Fact]
    public void Exports_nfs_echappe_les_espaces_du_chemin() =>
        Assert.Contains("/tank/my\\040share 10.0.0.1(rw,", ProtocolConf.RenderExports([new ShareDef("my share", "", false, [], Nfs: new NfsExport(["10.0.0.1"]))], Paths, 990));

    [Fact]
    public void Configuration_ftp_n_autorise_que_la_liste_et_enferme_les_utilisateurs()
    {
        var text = ProtocolConf.RenderFtpConf(new FtpSettings(false, 41000, 41010), "/c.pem", "/etc/l");
        Assert.Contains("anonymous_enable=NO", text);
        Assert.Contains("chroot_local_user=YES", text);
        Assert.Contains("userlist_deny=NO", text);
        Assert.Contains("userlist_file=/etc/l", text);
        Assert.Contains("pasv_min_port=41000", text);
        Assert.Contains("ssl_enable=NO", text);
        Assert.DoesNotContain("rsa_cert_file", text);
    }

    [Fact]
    public void Configuration_ftp_tls_exige_le_chiffrement()
    {
        var text = ProtocolConf.RenderFtpConf(new FtpSettings(true), "/c.pem", "/etc/l");
        Assert.Contains("ssl_enable=YES", text);
        Assert.Contains("force_local_logins_ssl=YES", text);
        Assert.Contains("rsa_cert_file=/c.pem", text);
    }

    [Fact]
    public void Lit_les_points_de_montage_avec_leurs_espaces()
    {
        const string info = "36 25 0:32 / /srv/ursanas-ftp/alice/docs rw,relatime - ext4 /dev/sda1 rw\n37 25 0:33 / /mnt/with\\040space rw - ext4 /dev/sdb rw\n";
        Assert.Equal(["/srv/ursanas-ftp/alice/docs", "/mnt/with space"], ProtocolConf.ParseMountPoints(info));
    }
}

public class ProtocolServiceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-proto-" + Guid.NewGuid().ToString("N"));
    readonly FakeRunner _run = new();
    readonly ProtocolService _svc;
    string Exports => Path.Combine(_dir, "exports.d", "ursanas.exports");
    string FtpConf => Path.Combine(_dir, "vsftpd.conf");
    string MountInfo => Path.Combine(_dir, "mountinfo");

    static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string> { ["docs"] = "/srv/nas/docs" };

    public ProtocolServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "exports.d"));
        File.WriteAllText(FtpConf, "# stock\nanonymous_enable=YES\n");
        File.WriteAllText(MountInfo, "");
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Nfs:ExportsFile"] = Exports,
            ["Ftp:ConfFile"] = FtpConf,
            ["Ftp:Root"] = Path.Combine(_dir, "ftp"),
            ["Ftp:StateFile"] = Path.Combine(_dir, "state"),
            ["Ftp:CertFile"] = Path.Combine(_dir, "ftp.pem"),
            ["Ftp:MountInfo"] = MountInfo,
        }).Build();
        _run.Handler = c => (c.File, c.Args[0]) switch
        {
            ("getent", "group") => new(0, "nasusers:x:990:\n", ""),
            _ => new(0, "", ""),
        };
        _svc = new ProtocolService(_run, cfg);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    ShareDef Docs(NfsExport? nfs = null, FtpAccess? ftp = null) => new("docs", "", false, [], Acl: true, Nfs: nfs, Ftp: ftp);

    [Fact]
    public async Task Ecrit_les_exports_nfs_et_les_recharge()
    {
        await _svc.ApplyAsync([Docs(nfs: new NfsExport(["10.0.0.0/24"]))], Paths, null);
        Assert.Contains("/srv/nas/docs 10.0.0.0/24(rw,", File.ReadAllText(Exports));
        Assert.Contains("anongid=990", File.ReadAllText(Exports));
        Assert.Contains(_run.Calls, c => c.File == "exportfs" && c.Args.SequenceEqual(["-ra"]));
    }

    [Fact]
    public async Task Echec_d_exportfs_restaure_le_fichier_precedent()
    {
        await _svc.ApplyAsync([Docs(nfs: new NfsExport(["10.0.0.1"]))], Paths, null);
        var before = File.ReadAllText(Exports);
        _run.Handler = c => c.File == "exportfs" ? new(1, "", "bad") : new(0, "nasusers:x:990:\n", "");
        await Assert.ThrowsAsync<OperationException>(() => _svc.ApplyAsync([Docs(nfs: new NfsExport(["10.0.0.2"]))], Paths, null));
        Assert.Equal(before, File.ReadAllText(Exports));
    }

    [Fact]
    public async Task Refuse_nfs_et_ftp_quand_les_services_ne_sont_pas_installes()
    {
        Directory.Delete(Path.Combine(_dir, "exports.d"));
        File.Delete(FtpConf);
        await Assert.ThrowsAsync<OperationException>(() => _svc.ApplyAsync([Docs(nfs: new NfsExport(["10.0.0.1"]))], Paths, null));
        await Assert.ThrowsAsync<OperationException>(() => _svc.ApplyAsync([Docs(ftp: new FtpAccess(["alice"]))], Paths, null));
        await _svc.ApplyAsync([Docs()], Paths, null); // sans NFS ni FTP demandé : rien à faire, pas d'erreur
    }

    [Fact]
    public async Task Ftp_cree_un_dossier_par_utilisateur_avec_ses_partages_montes()
    {
        await _svc.ApplyAsync([Docs(ftp: new FtpAccess(["alice", "bob"], ReadOnly: true))], Paths, null);

        Assert.Equal("alice\nbob\n", File.ReadAllText(FtpConf + ".userlist"));
        Assert.Contains(_run.Calls, c => c.File == "usermod" && c.Args.SequenceEqual(["--home", Path.Combine(_dir, "ftp") + "/alice", "alice"])); // vsftpd enferme l'utilisateur dans son dossier personnel
        Assert.Contains("anonymous_enable=NO", File.ReadAllText(FtpConf));
        Assert.True(File.Exists(FtpConf + ".ursanas-bak")); // la configuration d'origine est gardée
        var target = Path.Combine(_dir, "ftp") + "/alice/docs";
        Assert.Contains(_run.Calls, c => c.File == "mount" && c.Args.SequenceEqual(["--bind", "/srv/nas/docs", target]));
        Assert.Contains(_run.Calls, c => c.File == "mount" && c.Args.SequenceEqual(["-o", "remount,bind,ro", target]));
        Assert.Contains(_run.Calls, c => c.File == "systemctl" && c.Args[0] == "restart");
    }

    [Fact]
    public async Task Ftp_ne_redemarre_pas_quand_la_configuration_est_inchangee()
    {
        var share = Docs(ftp: new FtpAccess(["alice"]));
        await _svc.ApplyAsync([share], Paths, null);
        _run.Calls.Clear();
        await _svc.ApplyAsync([share], Paths, null);
        Assert.DoesNotContain(_run.Calls, c => c.File == "systemctl" && c.Args[0] == "restart");
    }

    [Fact]
    public async Task Ftp_demonte_ce_qui_est_retire_sans_jamais_supprimer_de_donnees()
    {
        var target = Path.Combine(_dir, "ftp") + "/alice/docs";
        await _svc.ApplyAsync([Docs(ftp: new FtpAccess(["alice"]))], Paths, null);
        File.WriteAllText(MountInfo, $"36 25 0:32 / {target} rw - ext4 /dev/sda1 rw\n");
        _run.Calls.Clear();

        await _svc.ApplyAsync([Docs()], Paths, null);

        Assert.Contains(_run.Calls, c => c.File == "umount" && c.Args.SequenceEqual(["-l", target]));
        Assert.Equal("", File.ReadAllText(FtpConf + ".userlist"));
        Assert.DoesNotContain(_run.Calls, c => c.File == "rm");
    }

    [Fact]
    public async Task Ftp_tls_cree_le_certificat_une_seule_fois()
    {
        await _svc.ApplyAsync([Docs(ftp: new FtpAccess(["alice"]))], Paths, new FtpSettings(true));
        Assert.Contains(_run.Calls, c => c.File == "openssl");
        File.WriteAllText(Path.Combine(_dir, "ftp.pem"), "x");
        _run.Calls.Clear();
        await _svc.ApplyAsync([Docs(ftp: new FtpAccess(["alice"]))], Paths, new FtpSettings(true));
        Assert.DoesNotContain(_run.Calls, c => c.File == "openssl");
    }

    [Fact]
    public async Task Un_service_inconnu_ou_non_installe_est_refuse()
    {
        await Assert.ThrowsAsync<OperationException>(() => _svc.SetServiceAsync("telnet", true));
        _run.Handler = c => c.File == "systemctl" && c.Args[0] == "cat" ? new(1, "", "") : new(0, "", "");
        await Assert.ThrowsAsync<OperationException>(() => _svc.SetServiceAsync("ftp", true));
        _run.Handler = null;
        await _svc.SetServiceAsync("ftp", true);
        Assert.Contains(_run.Calls, c => c.File == "systemctl" && c.Args.SequenceEqual(["enable", "--now", "vsftpd"]));
    }
}

public class UserPasswordTests
{
    [Fact]
    public async Task Le_mot_de_passe_systeme_passe_par_stdin_pour_ftp()
    {
        var run = new FakeRunner
        {
            Handler = c => (c.File, c.Args.Length > 0 ? c.Args[0] : "") switch
            {
                ("getent", "group") => new(0, "nasusers:x:990:\n", ""),
                ("id", _) => new(1, "", ""),
                _ => new(0, "", ""),
            },
        };
        await new UserService(run).CreateAsync(new CreateUserRequest("alice", "se:cret-pw-1"));

        var chpasswd = Assert.Single(run.Calls, c => c.File == "chpasswd");
        Assert.Equal("alice:se:cret-pw-1\n", chpasswd.Stdin);
        Assert.DoesNotContain(run.Calls, c => c.Args.Any(a => a.Contains("se:cret-pw-1")));
    }

    [Fact]
    public async Task Echec_de_chpasswd_supprime_le_compte_cree()
    {
        var run = new FakeRunner
        {
            Handler = c => (c.File, c.Args.Length > 0 ? c.Args[0] : "") switch
            {
                ("getent", "group") => new(0, "nasusers:x:990:\n", ""),
                ("id", _) => new(1, "", ""),
                ("chpasswd", _) => new(1, "", "boom"),
                _ => new(0, "", ""),
            },
        };
        await Assert.ThrowsAsync<OperationException>(() => new UserService(run).CreateAsync(new CreateUserRequest("alice", "12345678")));
        Assert.Contains(run.Calls, c => c.File == "userdel" && c.Args[0] == "alice");
        Assert.Contains(run.Calls, c => c.File == "smbpasswd" && c.Args[0] == "-x");
    }
}
