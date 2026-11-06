using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class BackupRulesTests
{
    static BackupJobDef Job(string dest = "photos", string share = "docs", IReadOnlyList<string>? includes = null, int retention = 30, bool snapshot = false, string? dataset = null) =>
        new(1, "Photos", share, dataset, includes ?? [], "/mnt/usb", "id", dest, RetentionDays: retention, Snapshot: snapshot);

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
        new(7, "Docs", "docs", snapshot ? "tank/docs" : null, includes ?? [], Usb, targetId, "docs-copy", Snapshot: snapshot, WindowsDisk: false, Excludes: []);

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
