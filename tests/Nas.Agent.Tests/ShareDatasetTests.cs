using Microsoft.Extensions.Configuration;
using Nas.Agent.Services;
using Nas.Contracts;
using Nas.Web.Data;
using Xunit;

namespace Nas.Agent.Tests;

public class ShareDatasetTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-sharezfs-" + Guid.NewGuid().ToString("N"));
    readonly string _mount;
    readonly FakeRunner _run = new();
    readonly ShareService _svc;

    public ShareDatasetTests()
    {
        _mount = Path.Combine(_dir, "zfs", "photos");
        Directory.CreateDirectory(_mount);
        File.WriteAllText(Path.Combine(_dir, "smb.conf"), "[global]\n   workgroup = X\n");
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Samba:ConfigDir"] = _dir,
            ["Samba:ShareRoot"] = Path.Combine(_dir, "shares"),
        }).Build();
        SetZfs($"tank\t1\t1\t1\t{Path.Combine(_dir, "zfs")}\ntank/photos\t1\t1\t1\t{_mount}\ntank/off\t1\t1\t1\tnone\n");
        _svc = new ShareService(_run, new UserService(_run), cfg);
    }

    void SetZfs(string datasets) =>
        _run.Handler = c => (c.File, c.Args[0]) switch
        {
            ("getent", "group") => new(0, "nasusers:x:990:alice\n", ""),
            ("zfs", "list") => new(0, datasets, ""),
            _ => new(0, "", ""),
        };

    string Conf => File.ReadAllText(Path.Combine(_dir, "ursanas.conf"));

    [Fact]
    public async Task Un_partage_sur_un_jeu_de_donnees_utilise_son_point_de_montage()
    {
        await _svc.ApplyAsync([new ShareDef("photos", "", false, ["alice"], "tank/photos")]);

        Assert.Contains($"path = {_mount}", Conf);
        Assert.Contains(_run.Calls, c => c.File == "chmod" && c.Args[0] == "2770" && c.Args[1] == _mount);
        Assert.Contains(_run.Calls, c => c.File == "chgrp" && c.Args[1] == _mount);
        Assert.False(Directory.Exists(Path.Combine(_dir, "shares", "photos"))); // pas de dossier simple en double
    }

    [Fact]
    public async Task Les_partages_simples_ne_consultent_pas_zfs()
    {
        await _svc.ApplyAsync([new ShareDef("docs", "", false, ["alice"])]);
        Assert.DoesNotContain(_run.Calls, c => c.File == "zfs");
    }

    [Theory]
    [InlineData("tank/absent", "does not exist")]
    [InlineData("tank/off", "no mount point")]
    public async Task Refuse_un_jeu_inconnu_ou_sans_point_de_montage(string dataset, string message)
    {
        var ex = await Assert.ThrowsAsync<OperationException>(() =>
            _svc.ApplyAsync([new ShareDef("x", "", false, ["alice"], dataset)]));
        Assert.Contains(message, ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "ursanas.conf")));
    }

    [Fact]
    public async Task Refuse_un_jeu_non_monte()
    {
        SetZfs($"tank/photos\t1\t1\t1\t{Path.Combine(_dir, "pas-monte")}\n");
        var ex = await Assert.ThrowsAsync<OperationException>(() =>
            _svc.ApplyAsync([new ShareDef("photos", "", false, ["alice"], "tank/photos")]));
        Assert.Contains("not mounted", ex.Message);
    }

    [Fact]
    public void Deux_partages_ne_peuvent_pas_partager_le_meme_jeu()
    {
        var known = new HashSet<string> { "alice" };
        var a = new ShareDef("a", "", false, ["alice"], "tank/photos");
        Assert.Null(Rules.ValidateShares([a], known));
        Assert.Contains("already used", Rules.ValidateShares([a, a with { Name = "b" }], known));
        Assert.NotNull(Rules.ValidateShares([a with { Dataset = "tank" }], known)); // racine d'un pool refusée
        Assert.NotNull(Rules.ValidateShares([a with { Dataset = "tank/../x" }], known));
    }

    [Fact]
    public async Task La_base_conserve_le_jeu_de_donnees()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = Path.Combine(_dir, "db") }).Build();
        var db = new Db(cfg);
        db.Init();
        db.Init(); // la migration doit être rejouable
        var repo = new Repo(db);

        await repo.UpsertShareAsync(new ShareDef("photos", "c", false, ["alice"], "tank/photos"));
        await repo.UpsertShareAsync(new ShareDef("docs", "", true, ["alice"]));

        var shares = await repo.GetSharesAsync();
        Assert.Equal("tank/photos", shares.Single(s => s.Name == "photos").Dataset);
        Assert.Null(shares.Single(s => s.Name == "docs").Dataset);

        await repo.UpsertShareAsync(new ShareDef("photos", "c", false, ["alice"])); // retour à un dossier simple
        Assert.Null((await repo.GetSharesAsync()).Single(s => s.Name == "photos").Dataset);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
