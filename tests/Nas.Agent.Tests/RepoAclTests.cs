using Microsoft.Extensions.Configuration;
using Nas.Contracts;
using Nas.Web.Data;
using Xunit;

namespace Nas.Agent.Tests;

public class RepoAclTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-test-" + Guid.NewGuid().ToString("N"));
    readonly Repo _repo;

    public RepoAclTests()
    {
        var db = new Db(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dir }).Build());
        db.Init();
        _repo = new Repo(db);
    }

    [Fact]
    public async Task Les_regles_d_un_dossier_font_l_aller_retour_et_une_liste_vide_les_efface()
    {
        var rules = new[] { new AclRule(AclSubjectKind.Group, "compta", false, AclRights.CreateFiles | AclRights.DeleteSelf, AclApply.SubFolders | AclApply.Files, true) };
        await _repo.SetFolderAclAsync("docs", "a/b", rules);
        await _repo.SetFolderAclAsync("docs", "", rules);

        var all = await _repo.GetFolderAclsAsync("docs");
        Assert.Equal(["", "a/b"], all.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(rules, all["a/b"]);

        await _repo.SetFolderAclAsync("docs", "a/b", []);
        Assert.Equal([""], (await _repo.GetFolderAclsAsync("docs")).Keys);
    }

    [Fact]
    public async Task Un_partage_garde_son_mode_permissions_et_perd_ses_regles_a_la_suppression()
    {
        await _repo.UpsertShareAsync(new ShareDef("docs", "", false, ["alice"], null, Acl: true));
        await _repo.SetFolderAclAsync("docs", "", [new AclRule(AclSubjectKind.User, "alice", true, AclRights.Read, AclApply.Everything)]);
        Assert.True((await _repo.GetSharesAsync()).Single().Acl);

        await _repo.DeleteShareAsync("docs");
        Assert.Empty(await _repo.GetFolderAclsAsync("docs"));
    }

    static readonly AclRule[] R = [new AclRule(AclSubjectKind.Group, "g", true, AclRights.Read, AclApply.Everything)];

    async Task SeedAsync()
    {
        foreach (var p in new[] { "a", "a/b", "a_b", "ab/c", "x" })
            await _repo.SetFolderAclAsync("docs", p, R);
        await _repo.SetFolderStateAsync("other", "a", new FolderState(R, false));
    }

    [Fact]
    public async Task Renommer_un_dossier_deplace_ses_regles_et_celles_de_ses_sous_dossiers_seulement()
    {
        await SeedAsync();
        await _repo.RenameFolderStatesAsync("docs", "a", "z/a2");

        var keys = (await _repo.GetFolderAclsAsync("docs")).Keys.Order(StringComparer.Ordinal).ToList();
        // « a_b » et « ab/c » ressemblent à un préfixe de « a » sans en faire partie : ils ne bougent pas.
        Assert.Equal(["a_b", "ab/c", "x", "z/a2", "z/a2/b"], keys);
        Assert.Contains("a", (await _repo.GetFolderStatesAsync("other")).Keys); // un autre partage n'est pas touché
    }

    [Fact]
    public async Task Copier_un_dossier_duplique_regles_et_rupture_d_heritage()
    {
        await SeedAsync();
        await _repo.SetFolderStateAsync("docs", "a/b", new FolderState(R, false));
        await _repo.CopyFolderStatesAsync("docs", "a", "c/a");

        var states = await _repo.GetFolderStatesAsync("docs");
        Assert.Equal(["a", "a/b", "a_b", "ab/c", "c/a", "c/a/b", "x"], states.Keys.Order(StringComparer.Ordinal));
        Assert.False(states["c/a/b"].Inherit);
        Assert.True(states["c/a"].Inherit);
    }

    [Fact]
    public async Task Supprimer_un_dossier_efface_ses_regles_et_celles_de_son_contenu()
    {
        await SeedAsync();
        await _repo.DeleteFolderStatesAsync("docs", "a");
        Assert.Equal(["a_b", "ab/c", "x"], (await _repo.GetFolderAclsAsync("docs")).Keys.Order(StringComparer.Ordinal));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
