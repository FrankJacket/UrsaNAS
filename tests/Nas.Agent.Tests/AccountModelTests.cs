using Nas.Agent.Services;
using Nas.Contracts;
using Xunit;

namespace Nas.Agent.Tests;

public class NtHashTests
{
    // Valeurs de référence connues du monde Samba/Windows.
    [Theory]
    [InlineData("", "31D6CFE0D16AE931B73C59D7E0C089C0")]
    [InlineData("password", "8846F7EAEE8FB117AD06BDD830B7586C")]
    public void Empreinte_NT_connue(string password, string expected)
    {
        Assert.Equal(expected, NtHash.Of(password));
    }

    [Fact]
    public void Lit_l_empreinte_dans_la_sortie_de_pdbedit()
    {
        const string output = "alice:1001:XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX:8846F7EAEE8FB117AD06BDD830B7586C:[U          ]:LCT-6560F0A1:\n";
        Assert.Equal("8846F7EAEE8FB117AD06BDD830B7586C", NtHash.FromPdbedit(output, "alice"));
        Assert.Null(NtHash.FromPdbedit(output, "bob"));
        Assert.Null(NtHash.FromPdbedit("alice:1001:XXXX:XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX:[DU         ]:LCT-0:\n", "alice")); // compte sans mot de passe
    }
}

public class AccountModelTests
{
    readonly FakeCommandRunner _run = new();
    readonly UserService _users;
    readonly GroupService _groups;

    public AccountModelTests()
    {
        _users = new UserService(_run);
        _groups = new GroupService(_run, _users);
    }

    async Task<IReadOnlyList<string>> Members(string group) =>
        (await _groups.ListAsync()).Single(g => g.Name == group).Members;

    [Fact]
    public async Task Les_groupes_fixes_sont_crees_et_les_comptes_existants_rangent_dans_allusers()
    {
        await _users.EnsureSystemGroupsAsync();

        Assert.Empty(await Members(AclRules.AdminsGroup));
        Assert.Equal(["alice", "bob", "carol"], await Members(AclRules.AllUsersGroup));
    }

    [Fact]
    public async Task Un_nouvel_utilisateur_est_membre_de_allusers_et_de_admins_seulement_s_il_est_administrateur()
    {
        await _users.CreateAsync(new CreateUserRequest("dave", "mot-de-passe-1"));
        await _users.CreateAsync(new CreateUserRequest("erin", "mot-de-passe-2", Admin: true));

        Assert.Contains("dave", await Members(AclRules.AllUsersGroup));
        Assert.Contains("erin", await Members(AclRules.AllUsersGroup));
        Assert.Equal(["erin"], await Members(AclRules.AdminsGroup));

        var list = await _users.ListAsync();
        Assert.False(list.Single(u => u.Name == "dave").Admin);
        Assert.True(list.Single(u => u.Name == "erin").Admin);
    }

    [Fact]
    public async Task Donner_et_retirer_le_role_sans_jamais_retirer_le_dernier_administrateur()
    {
        await _users.CreateAsync(new CreateUserRequest("erin", "mot-de-passe-2", Admin: true));

        await _users.SetAdminAsync("alice", true);
        Assert.Equal(["alice", "erin"], await Members(AclRules.AdminsGroup));

        await _users.SetAdminAsync("alice", false);
        Assert.Equal(["erin"], await Members(AclRules.AdminsGroup));

        var ex = await Assert.ThrowsAsync<OperationException>(() => _users.SetAdminAsync("erin", false));
        Assert.Contains("last administrator", ex.Message);
        var del = await Assert.ThrowsAsync<OperationException>(() => _users.DeleteAsync("erin"));
        Assert.Contains("last administrator", del.Message);
    }

    [Fact]
    public async Task Le_mot_de_passe_est_verifie_contre_le_compte_samba_et_le_tampon_change_avec_lui()
    {
        await _users.CreateAsync(new CreateUserRequest("erin", "mot-de-passe-2", Admin: true));
        await _users.CreateAsync(new CreateUserRequest("dave", "mot-de-passe-1"));

        var ok = await _users.VerifyAsync(new VerifyUserRequest("erin", "mot-de-passe-2"));
        Assert.True(ok.Valid);
        Assert.True(ok.Admin);
        Assert.False((await _users.VerifyAsync(new VerifyUserRequest("erin", "mauvais"))).Valid);
        Assert.False((await _users.VerifyAsync(new VerifyUserRequest("fantome", "mot-de-passe-2"))).Valid);
        Assert.False((await _users.VerifyAsync(new VerifyUserRequest("../etc", "x"))).Valid);
        Assert.False((await _users.VerifyAsync(new VerifyUserRequest("dave", "mot-de-passe-1"))).Admin);

        Assert.Equal(ok.Stamp, (await _users.SessionAsync("erin")).Stamp);
        await _users.SetPasswordAsync("erin", "nouveau-mot-de-passe");
        var after = await _users.SessionAsync("erin");
        Assert.NotEqual(ok.Stamp, after.Stamp);
        Assert.False((await _users.VerifyAsync(new VerifyUserRequest("erin", "mot-de-passe-2"))).Valid);
        Assert.True((await _users.VerifyAsync(new VerifyUserRequest("erin", "nouveau-mot-de-passe"))).Valid);
        Assert.DoesNotContain("nouveau-mot-de-passe", after.Stamp);
    }

    [Fact]
    public async Task Les_groupes_fixes_ne_se_suppriment_pas_et_allusers_ne_se_modifie_pas()
    {
        await _users.EnsureSystemGroupsAsync();

        await Assert.ThrowsAsync<OperationException>(() => _groups.DeleteAsync(AclRules.AdminsGroup));
        await Assert.ThrowsAsync<OperationException>(() => _groups.DeleteAsync(AclRules.AllUsersGroup));
        await Assert.ThrowsAsync<OperationException>(() => _groups.SetMembersAsync(AclRules.AllUsersGroup, ["alice"]));
        await Assert.ThrowsAsync<OperationException>(() => _groups.SetMembersAsync(AclRules.AdminsGroup, []));
        await Assert.ThrowsAsync<OperationException>(() => _groups.CreateAsync(new CreateGroupRequest(AclRules.AdminsGroup, [])));

        await _groups.SetMembersAsync(AclRules.AdminsGroup, ["alice", "bob"]); // les membres de admins se choisissent
        Assert.Equal(["alice", "bob"], await Members(AclRules.AdminsGroup));
    }
}

public class AdminShareAccessTests
{
    static readonly ShareDef Docs = new("docs", "", false, ["alice"]);

    [Fact]
    public void Les_administrateurs_ont_tous_les_droits_sur_un_partage_simple()
    {
        var conf = ShareConf.Render([Docs], "/srv/nas");
        Assert.Contains("valid users = alice @nasg-admins", conf);
        Assert.Contains("admin users = @nasg-admins", conf);
        Assert.DoesNotContain("write list", conf); // pas en lecture seule : rien à ajouter
    }

    [Fact]
    public void Meme_en_lecture_seule_les_administrateurs_peuvent_ecrire()
    {
        var conf = ShareConf.Render([Docs with { ReadOnly = true }], "/srv/nas");
        Assert.Contains("read only = yes", conf);
        Assert.Contains("write list = @nasg-admins", conf);
    }

    [Fact]
    public void Un_partage_a_permissions_avancees_laisse_aussi_les_administrateurs_passer_outre_les_ACL()
    {
        var conf = ShareConf.Render([Docs with { Acl = true, ValidUsers = [] }], "/srv/nas");
        Assert.Contains($"admin users = {AclService.ServiceUser} @nasg-admins", conf);
    }
}
