using Microsoft.Extensions.Configuration;
using Nas.Agent.Services;
using Nas.Contracts;
using Xunit;

namespace Nas.Agent.Tests;

public class ZfsRulesTests
{
    [Theory]
    [InlineData("tank", true)]
    [InlineData("data-2", true)]
    [InlineData("", false)]
    [InlineData("1tank", false)]
    [InlineData("mirror", false)]
    [InlineData("RAIDZ2", false)]
    [InlineData("c0t1", false)]
    [InlineData("ta nk", false)]
    [InlineData("-f", false)]
    public void Nom_de_pool(string name, bool ok) => Assert.Equal(ok, Rules.ValidatePoolName(name) is null);

    [Theory]
    [InlineData("tank/photos", true)]
    [InlineData("tank/a/b", true)]
    [InlineData("tank", false)]
    [InlineData("tank/", false)]
    [InlineData("tank/../x", false)]
    [InlineData("tank/-r", false)]
    [InlineData("tank/a b", false)]
    [InlineData("tank/a@b", false)]
    public void Nom_de_jeu_de_donnees(string name, bool ok) => Assert.Equal(ok, Rules.ValidateDatasetName(name) is null);

    [Theory]
    [InlineData("manuel-2026.01", true)]
    [InlineData("a@b", false)]
    [InlineData("-x", false)]
    [InlineData("", false)]
    public void Nom_d_instantane(string name, bool ok) => Assert.Equal(ok, Rules.ValidateSnapshotName(name) is null);

    [Theory]
    [InlineData("mirror", 2, true)]
    [InlineData("mirror", 1, false)]
    [InlineData("raidz1", 3, true)]
    [InlineData("raidz1", 2, false)]
    [InlineData("raidz2", 3, false)]
    [InlineData("raidz2", 4, true)]
    [InlineData("stripe", 4, false)]
    public void Type_de_pool_et_nombre_de_disques(string layout, int count, bool ok) =>
        Assert.Equal(ok, Rules.ValidatePoolLayout(layout, count) is null);
}

public class ZfsParserTests
{
    const string Status = """
          pool: tank
         state: DEGRADED
        status: One or more devices could not be used because the label is missing or
        	invalid.  Sufficient replicas exist for the pool to continue
        	functioning in a degraded state.
        action: Replace the device using 'zpool replace'.
          scan: scrub repaired 0B in 00:00:01 with 0 errors on Sun Nov  1 00:24:02 2026
        config:

        	NAME        STATE     READ WRITE CKSUM
        	tank        DEGRADED     0     0     0
        	  raidz1-0  DEGRADED     0     0     0
        	    sdb     ONLINE       0     0     0
        	    sdc     UNAVAIL      0     0     0  cannot open
        	    sdd     ONLINE       0     0     0

        errors: No known data errors
        """;

    [Fact]
    public void Liste_des_pools()
    {
        var pools = ZfsParser.ParsePoolList("tank\t29927223296\t1048576\t29926174720\tONLINE\n");
        var p = Assert.Single(pools);
        Assert.Equal(("tank", 29927223296L, 1048576L, 29926174720L, "ONLINE"), p);
        Assert.Empty(ZfsParser.ParsePoolList(""));
    }

    [Fact]
    public void Etat_d_un_pool_degrade()
    {
        // Les lignes du modèle sont indentées de 8 espaces par le littéral brut : on retire l'indentation commune.
        var (scan, errors, devices) = ZfsParser.ParseStatus(Status);

        Assert.StartsWith("scrub repaired 0B", scan);
        Assert.Equal("No known data errors", errors);
        Assert.Equal(["tank", "raidz1-0", "sdb", "sdc", "sdd"], devices.Select(d => d.Name));
        Assert.Equal([0, 1, 2, 2, 2], devices.Select(d => d.Depth));
        Assert.Equal("UNAVAIL", devices[3].State);
        Assert.Equal("DEGRADED", devices[0].State);
    }

    [Fact]
    public void Analyse_en_cours_sur_plusieurs_lignes()
    {
        var text = "  pool: t\n state: ONLINE\n  scan: scrub in progress since Sun Nov  1 10:00:00 2026\n\t1G scanned, 50% done\nconfig:\n\n\tNAME  STATE  READ WRITE CKSUM\n\tt     ONLINE 0 0 0\n\nerrors: No known data errors\n";
        var (scan, _, devices) = ZfsParser.ParseStatus(text);
        Assert.Contains("scrub in progress", scan);
        Assert.Contains("50% done", scan);
        Assert.Single(devices);
    }

    [Fact]
    public void Jeux_de_donnees_et_instantanes()
    {
        var ds = ZfsParser.ParseDatasets("tank\t100\t900\t24\t/srv/zfs/tank\ntank/photos\t10\t900\t10\t/srv/zfs/tank/photos\n");
        Assert.Equal(["tank", "tank/photos"], ds.Select(d => d.Name));
        Assert.Equal("/srv/zfs/tank/photos", ds[1].Mountpoint);

        var snaps = ZfsParser.ParseSnapshots("tank/photos@avant\t0\t1767225600\n");
        var s = Assert.Single(snaps);
        Assert.Equal("tank/photos", s.Dataset);
        Assert.Equal("avant", s.Snapshot);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), s.Created);
    }
}

public class ZfsServiceTests
{
    readonly FakeRunner _run = new();
    readonly ZfsService _svc;
    List<DiskInfo> _disks =
    [
        new("sda", "/dev/sda", 20L << 30, "", "", false, ["/", "/boot/efi"]),
        new("sdb", "/dev/sdb", 10L << 30, "", "", false, []),
        new("sdc", "/dev/sdc", 10L << 30, "", "", false, []),
        new("sdd", "/dev/sdd", 10L << 30, "", "", false, []),
    ];

    sealed class Disks(Func<List<DiskInfo>> get) : IDiskProvider
    {
        public Task<IReadOnlyList<DiskInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DiskInfo>>(get());
    }

    public ZfsServiceTests()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection().Build();
        _svc = new ZfsService(_run, new Disks(() => _disks), cfg);
    }

    [Fact]
    public async Task Cree_un_pool_sans_option_force()
    {
        await _svc.CreatePoolAsync(new CreatePoolRequest("tank", "raidz1", ["/dev/sdb", "/dev/sdc", "/dev/sdd"]));

        var create = Assert.Single(_run.Calls, c => c.File == "zpool" && c.Args[0] == "create");
        Assert.DoesNotContain("-f", create.Args);
        Assert.Contains("ashift=12", create.Args);
        Assert.Equal(["/srv/zfs/tank"], create.Args.SkipWhile(a => a != "-m").Skip(1).Take(1));
        Assert.Equal(["tank", "raidz1", "/dev/sdb", "/dev/sdc", "/dev/sdd"], create.Args.TakeLast(5));
    }

    [Fact]
    public async Task Refuse_le_disque_systeme()
    {
        var ex = await Assert.ThrowsAsync<OperationException>(() =>
            _svc.CreatePoolAsync(new CreatePoolRequest("tank", "mirror", ["/dev/sda", "/dev/sdb"])));
        Assert.Contains("système", ex.Message);
        Assert.DoesNotContain(_run.Calls, c => c.Args.Length > 0 && c.Args[0] == "create");
    }

    [Fact]
    public async Task Refuse_un_chemin_inconnu_ou_en_double_ou_un_type_invalide()
    {
        await Assert.ThrowsAsync<OperationException>(() => _svc.CreatePoolAsync(new("tank", "mirror", ["/dev/sdb", "/etc/passwd"])));
        await Assert.ThrowsAsync<OperationException>(() => _svc.CreatePoolAsync(new("tank", "mirror", ["/dev/sdb", "/dev/sdb"])));
        await Assert.ThrowsAsync<OperationException>(() => _svc.CreatePoolAsync(new("tank", "raidz2", ["/dev/sdb", "/dev/sdc"])));
        Assert.DoesNotContain(_run.Calls, c => c.Args.Length > 0 && c.Args[0] == "create");
    }

    [Fact]
    public async Task Refuse_un_pool_qui_existe_deja()
    {
        _run.Handler = c => c.Args[0] == "list" ? new(0, "tank\t1\t0\t1\tONLINE\n", "") : new(0, "", "");
        var ex = await Assert.ThrowsAsync<OperationException>(() => _svc.CreatePoolAsync(new("tank", "mirror", ["/dev/sdb", "/dev/sdc"])));
        Assert.Contains("existe déjà", ex.Message);
    }

    [Fact]
    public async Task Remonte_l_erreur_de_zpool()
    {
        _run.Handler = c => c.Args[0] == "create" ? new(1, "", "cannot create 'tank': /dev/sdb is in use") : new(0, "", "");
        var ex = await Assert.ThrowsAsync<OperationException>(() => _svc.CreatePoolAsync(new("tank", "mirror", ["/dev/sdb", "/dev/sdc"])));
        Assert.Contains("is in use", ex.Message);
    }

    [Fact]
    public async Task Destruction_d_un_pool_inexistant_refusee()
    {
        await Assert.ThrowsAsync<OperationException>(() => _svc.DestroyPoolAsync("tank"));
        Assert.DoesNotContain(_run.Calls, c => c.Args.Length > 0 && c.Args[0] == "destroy");
    }

    [Fact]
    public async Task Jeux_de_donnees_et_instantanes_sans_option_recursive()
    {
        await _svc.CreateDatasetAsync("tank/photos");
        await _svc.CreateSnapshotAsync(new CreateSnapshotRequest("tank/photos", "avant"));
        await _svc.DestroySnapshotAsync("tank/photos@avant");
        await _svc.DestroyDatasetAsync("tank/photos");

        Assert.Equal(["create", "tank/photos"], _run.Calls[0].Args);
        Assert.Equal(["snapshot", "tank/photos@avant"], _run.Calls[1].Args);
        Assert.Equal(["destroy", "tank/photos@avant"], _run.Calls[2].Args);
        Assert.Equal(["destroy", "tank/photos"], _run.Calls[3].Args);
        Assert.DoesNotContain(_run.Calls, c => c.Args.Any(a => a.StartsWith("-")));
    }

    [Fact]
    public async Task Refuse_de_detruire_la_racine_d_un_pool_ou_un_nom_piege()
    {
        await Assert.ThrowsAsync<OperationException>(() => _svc.DestroyDatasetAsync("tank"));
        await Assert.ThrowsAsync<OperationException>(() => _svc.DestroyDatasetAsync("-r"));
        await Assert.ThrowsAsync<OperationException>(() => _svc.DestroySnapshotAsync("tank/photos"));
        await Assert.ThrowsAsync<OperationException>(() => _svc.DestroySnapshotAsync("tank/photos@-r"));
        Assert.Empty(_run.Calls);
    }
}
