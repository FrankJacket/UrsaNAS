using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class BackupRulesTests
{
    static BackupJobDef Job(string dest = "photos", string share = "docs", IReadOnlyList<string>? includes = null, int retention = 30, bool snapshot = false, string? dataset = null) =>
        new(1, "Photos", [new BackupSource(share, dataset, includes ?? [])], false, "/mnt/usb", "id", dest, RetentionDays: retention, Snapshot: snapshot);


    [Fact]
    public void Valide_les_sources()
    {
        BackupJobDef Multi(params BackupSource[] s) => new(1, "N", s, true, "/mnt/usb", "id", "d");
        Assert.Null(BackupRules.Validate(Multi(new BackupSource("docs", null, []), new BackupSource("photos", "tank/photos", ["a.txt"]))));
        Assert.NotNull(BackupRules.Validate(Multi())); // rien à copier
        Assert.NotNull(BackupRules.Validate(Multi(new BackupSource("docs", null, []), new BackupSource("docs", null, [])))); // partage en double
        Assert.NotNull(BackupRules.Validate(Multi(new BackupSource("docs", null, ["../x"]))));
        Assert.NotNull(BackupRules.Validate(Multi(new BackupSource("bad name", null, []))));
        // Sans sous-dossiers (ancienne forme) : un seul partage.
        Assert.NotNull(BackupRules.Validate(Multi(new BackupSource("docs", null, []), new BackupSource("photos", null, [])) with { SubFolders = false }));
        // Le snapshot exige au moins un partage sur ZFS.
        Assert.NotNull(BackupRules.Validate(Multi(new BackupSource("docs", null, [])) with { Snapshot = true }));
        Assert.Null(BackupRules.Validate(Multi(new BackupSource("docs", null, []), new BackupSource("photos", "tank/photos", [])) with { Snapshot = true }));
    }

    [Theory]
    [InlineData("photos", true)]
    [InlineData("Mes photos 2024", true)]
    [InlineData("a/b", false)]
    [InlineData("..", false)]
    [InlineData(".ursacopy-trash", false)]
    [InlineData("lost+found", false)]
    [InlineData("name.", false)]
    [InlineData("", false)]
    public void Valide_le_dossier_de_destination(string dest, bool ok) => Assert.Equal(ok, BackupRules.Validate(Job(dest)) is null);

    [Theory]
    [InlineData("2024/photos", true)]
    [InlineData("../etc", false)]
    [InlineData("/abs", false)]
    [InlineData("a//b", false)]
    [InlineData("a\\b", false)]
    public void Valide_les_dossiers_inclus(string include, bool ok) => Assert.Equal(ok, BackupRules.Validate(Job(includes: [include])) is null);

    [Fact]
    public void Valide_la_retention_et_le_snapshot()
    {
        Assert.NotNull(BackupRules.Validate(Job(retention: -1)));
        Assert.NotNull(BackupRules.Validate(Job(retention: 4000)));
        Assert.NotNull(BackupRules.Validate(Job(snapshot: true))); // pas de jeu de données
        Assert.Null(BackupRules.Validate(Job(snapshot: true, dataset: "tank/docs")));
    }
}

public class MountTableTests
{
    [Fact]
    public void Lit_point_de_montage_systeme_de_fichiers_et_peripherique()
    {
        const string info = "36 25 8:17 / /mnt/usb\\040disk rw,relatime shared:1 - ntfs3 /dev/sdb1 rw\n25 1 8:1 / / rw - ext4 /dev/sda1 rw\n";
        var m = MountTable.Parse(info);
        Assert.Equal(new MountEntry("/mnt/usb disk", "ntfs3", "/dev/sdb1"), m[0]);
        Assert.Equal("/", m[1].MountPoint);
    }
}

sealed class StubLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}

public class BackupServiceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-backup-" + Guid.NewGuid().ToString("N"));
    readonly string _roots, _share, _usb, _mountInfo;
    readonly FakeRunner _run = new();
    readonly BackupService _svc;

    public BackupServiceTests()
    {
        _roots = Path.Combine(_dir, "mnt");
        _usb = Path.Combine(_roots, "usb");
        _share = Path.Combine(_dir, "srv", "docs");
        _mountInfo = Path.Combine(_dir, "mountinfo");
        Directory.CreateDirectory(_usb);
        Directory.CreateDirectory(Path.Combine(_share, "sub"));
        File.WriteAllText(Path.Combine(_share, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(_share, "sub", "b.txt"), "beta");

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Roots:0"] = _roots.Replace('\\', '/'),
            ["Backup:RequireMountPoint"] = "false",
            ["Backup:MountInfo"] = _mountInfo,
        }).Build();
        _run.Handler = c => c.File switch
        {
            "testparm" => new(0, _share + "\n", ""),
            "zfs" when c.Args[0] == "get" => new(0, _share + "\n", ""),
            _ => new(0, "", ""),
        };
        var users = new UserService(_run);
        _svc = new BackupService(_run, new AclService(_run, users, new GroupService(_run, users), cfg), cfg, NullLogger<BackupService>.Instance, new StubLifetime());
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    string Usb => _usb.Replace('\\', '/');

    BackupJobDef Job(string? targetId, bool snapshot = false, IReadOnlyList<string>? includes = null) =>
        new(7, "Docs", [new BackupSource("docs", snapshot ? "tank/docs" : null, includes ?? [])], false, Usb, targetId, "docs-copy", Snapshot: snapshot, WindowsDisk: false, Excludes: []);

    async Task<BackupRunStatus> WaitDoneAsync(int id)
    {
        for (var i = 0; i < 200; i++)
        {
            if (_svc.Statuses().FirstOrDefault(s => s.JobId == id) is { Result: not null } s) return s;
            await Task.Delay(50);
        }
        throw new TimeoutException("La copie ne se termine pas.");
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/mnt")]
    [InlineData("relative/dir")]
    [InlineData("")]
    public void Refuse_une_cible_hors_des_racines_autorisees(string dir) => Assert.Throws<OperationException>(() => _svc.ValidateTarget(dir));

    [Fact]
    public void Refuse_les_remontees_et_les_dossiers_inexistants()
    {
        Assert.Throws<OperationException>(() => _svc.ValidateTarget(Usb + "/../../etc"));
        Assert.Throws<OperationException>(() => _svc.ValidateTarget(Usb + "/absent"));
        Assert.Equal(Usb, _svc.ValidateTarget(Usb));
    }

    [Fact]
    public void Exige_un_point_de_montage_quand_la_verification_est_active()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Roots:0"] = _roots.Replace('\\', '/'),
            ["Backup:MountInfo"] = _mountInfo,
        }).Build();
        var users = new UserService(_run);
        var strict = new BackupService(_run, new AclService(_run, users, new GroupService(_run, users), cfg), cfg, NullLogger<BackupService>.Instance, new StubLifetime());

        File.WriteAllText(_mountInfo, "25 1 8:1 / / rw - ext4 /dev/sda1 rw\n");
        Assert.Throws<OperationException>(() => strict.ValidateTarget(Usb)); // un dossier ordinaire du disque système
        File.WriteAllText(_mountInfo, $"36 25 8:17 / {Usb} rw - ntfs3 /dev/sdb1 rw\n");
        Assert.Equal(Usb, strict.ValidateTarget(Usb));
        Assert.Equal("/dev/sdb1", strict.ListTargets().Single().Device);
    }

    [Fact]
    public void Les_stockages_distants_montes_ne_servent_qu_au_gestionnaire_de_fichiers()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Roots:0"] = _roots.Replace('\\', '/'),
            ["Backup:MountInfo"] = _mountInfo,
            ["Remote:MountRoot"] = _roots.Replace((char)92, (char)47) + "-remote",
        }).Build();
        var users = new UserService(_run);
        var strict = new BackupService(_run, new AclService(_run, users, new GroupService(_run, users), cfg), cfg, NullLogger<BackupService>.Instance, new StubLifetime());
        var remote = _roots.Replace((char)92, (char)47) + "-remote";
        File.WriteAllText(_mountInfo, $"36 25 0:40 / {remote}/cloud rw - fuse.rclone remote:/ rw\n");

        Assert.Equal(remote + "/cloud", strict.ValidatePlace(remote + "/cloud"));
        Assert.Throws<OperationException>(() => strict.ValidateTarget(remote + "/cloud")); // ni sauvegarde ni machine virtuelle
        Assert.Throws<OperationException>(() => strict.ValidatePlace(remote + "/absent")); // pas monté
        Assert.Throws<OperationException>(() => strict.ValidatePlace(remote + "/cloud/../../etc"));
        Assert.Empty(strict.ListTargets());

        // ... mais une sauvegarde peut les viser : ils sont listés, marqués comme distants, et reconnus comme cible
        var listed = Assert.Single(strict.ListTargets(includeRemote: true));
        Assert.True(listed.Remote);
        Assert.Equal(remote + "/cloud", listed.Dir);
        Assert.Equal(BackupDiskState.NotInitialised, strict.CheckDisk(remote + "/cloud", null).State);
        Assert.Equal(BackupDiskState.Missing, strict.CheckDisk(remote + "/absent", "x").State);
    }

    [Fact]
    public void Etat_du_disque_absent_non_initialise_connecte_ou_autre()
    {
        Assert.Equal(BackupDiskState.Missing, _svc.CheckDisk(Usb + "/absent", "x").State);
        Assert.Equal(BackupDiskState.NotInitialised, _svc.CheckDisk(Usb, null).State);
        Assert.Equal(BackupDiskState.NotInitialised, _svc.CheckDisk(Usb, "").State); // le web envoie « id= » quand il n'y en a pas
        Assert.Equal(BackupDiskState.Missing, _svc.CheckDisk(Usb, "x").State); // pas de marqueur sur le disque

        var id = _svc.InitDisk(Usb);
        Assert.Equal(id, _svc.InitDisk(Usb)); // initialiser deux fois ne change rien
        Assert.Equal(BackupDiskState.Connected, _svc.CheckDisk(Usb, id).State);
        Assert.Equal(BackupDiskState.OtherDisk, _svc.CheckDisk(Usb, "autre").State);
    }

    [Fact]
    public async Task Copie_le_partage_vers_le_disque_puis_oublie_la_copie_acquittee()
    {
        var id = _svc.InitDisk(Usb);
        _svc.Start(Job(id));
        var done = await WaitDoneAsync(7);

        Assert.Equal("Success", done.Result!.Status);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(_usb, "docs-copy", "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(_usb, "docs-copy", "sub", "b.txt")));
        Assert.Equal(2, done.Copied);

        _svc.Acknowledge(7);
        Assert.Empty(_svc.Statuses());
    }

    [Fact]
    public async Task Ne_copie_que_les_dossiers_choisis()
    {
        var id = _svc.InitDisk(Usb);
        _svc.Start(Job(id, includes: ["sub"]));
        await WaitDoneAsync(7);
        Assert.True(File.Exists(Path.Combine(_usb, "docs-copy", "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(_usb, "docs-copy", "a.txt")));
    }


    [Fact]
    public async Task Copie_plusieurs_partages_chacun_dans_son_sous_dossier_avec_dossiers_et_fichiers_choisis()
    {
        var photos = Path.Combine(_dir, "srv", "photos");
        Directory.CreateDirectory(Path.Combine(photos, "2024"));
        File.WriteAllText(Path.Combine(photos, "2024", "p.jpg"), "pic");
        File.WriteAllText(Path.Combine(photos, "autre.jpg"), "other");
        _run.Handler = c => c.File == "testparm"
            ? new(0, (c.Args.Any(a => a.EndsWith("=photos")) ? photos : _share) + "\n", "")
            : new(0, "", "");
        var id = _svc.InitDisk(Usb);

        var job = new BackupJobDef(7, "Tout", [new BackupSource("docs", null, ["a.txt"]), new BackupSource("photos", null, ["2024"])],
            true, Usb, id, "mix", WindowsDisk: false, Excludes: []);
        _svc.Start(job);
        var done = await WaitDoneAsync(7);

        Assert.Equal("Success", done.Result!.Status);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(_usb, "mix", "docs", "a.txt")));
        Assert.False(File.Exists(Path.Combine(_usb, "mix", "docs", "sub", "b.txt"))); // seul le fichier choisi
        Assert.Equal("pic", File.ReadAllText(Path.Combine(_usb, "mix", "photos", "2024", "p.jpg")));
        Assert.False(File.Exists(Path.Combine(_usb, "mix", "photos", "autre.jpg")));
        Assert.Equal(2, done.Copied);
    }

    [Fact]
    public async Task Un_partage_qui_echoue_n_empeche_pas_les_autres_et_le_travail_est_termine_avec_des_erreurs()
    {
        _run.Handler = c => c.File == "testparm"
            ? (c.Args.Any(a => a.EndsWith("=ghost")) ? new(1, "", "unknown") : new(0, _share + "\n", ""))
            : new(0, "", "");
        var id = _svc.InitDisk(Usb);
        _svc.Start(new BackupJobDef(7, "Mix", [new BackupSource("ghost", null, []), new BackupSource("docs", null, [])], true, Usb, id, "mix", WindowsDisk: false, Excludes: []));
        var done = await WaitDoneAsync(7);

        Assert.Equal("CompletedWithErrors", done.Result!.Status);
        Assert.Contains("ghost", done.Result.Message);
        Assert.True(File.Exists(Path.Combine(_usb, "mix", "docs", "a.txt")));
    }

    [Fact]
    public void Refuse_un_travail_dont_le_disque_n_est_pas_initialise() =>
        Assert.Throws<OperationException>(() => _svc.Start(Job(null)));

    [Fact]
    public async Task Refuse_un_partage_inconnu()
    {
        var id = _svc.InitDisk(Usb);
        _run.Handler = c => c.File == "testparm" ? new(1, "", "unknown") : new(0, "", "");
        _svc.Start(Job(id));
        var done = await WaitDoneAsync(7);
        Assert.Equal("Failed", done.Result!.Status);
    }

    [Fact]
    public async Task Copie_depuis_un_snapshot_zfs_puis_le_detruit()
    {
        var snap = Path.Combine(_share, ".zfs", "snapshot", "ursacopy-7");
        Directory.CreateDirectory(snap);
        File.WriteAllText(Path.Combine(snap, "frozen.txt"), "figé");
        var id = _svc.InitDisk(Usb);

        _svc.Start(Job(id, snapshot: true));
        var done = await WaitDoneAsync(7);

        Assert.Equal("Success", done.Result!.Status);
        Assert.Equal("figé", File.ReadAllText(Path.Combine(_usb, "docs-copy", "frozen.txt")));
        Assert.False(File.Exists(Path.Combine(_usb, "docs-copy", "a.txt"))); // la source est le snapshot, pas le partage vivant
        Assert.Contains(_run.Calls, c => c.File == "zfs" && c.Args.SequenceEqual(["snapshot", "tank/docs@ursacopy-7"]));
        Assert.Equal("destroy", _run.Calls.Last(c => c.File == "zfs").Args[0]);
    }

    [Fact]
    public async Task Refuse_le_snapshot_quand_le_jeu_de_donnees_n_est_pas_celui_du_partage()
    {
        var id = _svc.InitDisk(Usb);
        _run.Handler = c => c.File switch
        {
            "testparm" => new(0, _share + "\n", ""),
            "zfs" when c.Args[0] == "get" => new(0, "/autre/chemin\n", ""),
            _ => new(0, "", ""),
        };
        _svc.Start(Job(id, snapshot: true));
        var done = await WaitDoneAsync(7);
        Assert.Equal("Failed", done.Result!.Status);
        Assert.DoesNotContain(_run.Calls, c => c.File == "zfs" && c.Args[0] == "snapshot");
    }
}
