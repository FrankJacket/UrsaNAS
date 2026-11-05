using Microsoft.Extensions.Configuration;
using Nas.Web.Data;
using Xunit;

namespace Nas.Agent.Tests;

public class RepoTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-test-" + Guid.NewGuid().ToString("N"));
    readonly Repo _repo;

    public RepoTests()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dir }).Build();
        var db = new Db(cfg);
        db.Init();
        _repo = new Repo(db);
    }

    [Fact]
    public async Task Le_journal_renvoie_les_entrees_les_plus_recentes_en_premier()
    {
        await _repo.AuditAsync("admin", "Première");
        await _repo.AuditAsync("admin", "Seconde", "détail");

        var rows = await _repo.GetAuditAsync();

        Assert.Equal(["Seconde", "Première"], rows.Select(r => r.Action));
        Assert.Equal("détail", rows[0].Detail);
        Assert.Null(rows[1].Detail);
    }

    [Fact]
    public async Task Le_journal_respecte_la_limite()
    {
        for (var i = 0; i < 5; i++) await _repo.AuditAsync("admin", $"a{i}");
        Assert.Equal(2, (await _repo.GetAuditAsync(2)).Count);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}
