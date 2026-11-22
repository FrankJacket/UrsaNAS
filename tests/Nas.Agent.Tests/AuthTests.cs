using Microsoft.Extensions.Configuration;
using Nas.Contracts;
using Nas.Web;
using Nas.Web.Auth;
using Nas.Web.Data;
using Xunit;

namespace Nas.Agent.Tests;

/// <summary>Les comptes du NAS, en mémoire : mot de passe, rôle d'administrateur, et un tampon qui change avec le mot de passe.</summary>
sealed class FakeDirectory : IUserDirectory
{
    public Dictionary<string, (string Password, bool Admin, int Version)> Accounts { get; } = new();
    public bool Down { get; set; }

    void CheckUp() { if (Down) throw new HttpRequestException("agent muet"); }
    static string Stamp(string name, int v) => $"{name}#{v}";

    public Task<UserSession> VerifyAsync(string name, string password, CancellationToken ct = default)
    {
        CheckUp();
        return Task.FromResult(Accounts.TryGetValue(name, out var a) && a.Password == password ? new UserSession(true, a.Admin, Stamp(name, a.Version)) : new UserSession(false, false, ""));
    }

    public Task<UserSession> SessionAsync(string name, CancellationToken ct = default)
    {
        CheckUp();
        return Task.FromResult(Accounts.TryGetValue(name, out var a) ? new UserSession(true, a.Admin, Stamp(name, a.Version)) : new UserSession(false, false, ""));
    }

    public Task<IReadOnlyList<NasUser>> ListAsync(CancellationToken ct = default)
    {
        CheckUp();
        return Task.FromResult<IReadOnlyList<NasUser>>(Accounts.Select(a => new NasUser(a.Key, a.Value.Admin)).ToList());
    }

    public Task CreateAsync(string name, string password, bool admin, CancellationToken ct = default)
    {
        Accounts[name] = (password, admin, 1);
        return Task.CompletedTask;
    }

    public Task SetAdminAsync(string name, bool admin, CancellationToken ct = default)
    {
        Accounts[name] = Accounts[name] with { Admin = admin };
        return Task.CompletedTask;
    }

    public Task SetPasswordAsync(string name, string password, CancellationToken ct = default)
    {
        Accounts[name] = Accounts[name] with { Password = password, Version = Accounts[name].Version + 1 };
        return Task.CompletedTask;
    }
}

public class AdminAccountsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-auth-" + Guid.NewGuid().ToString("N"));
    readonly Db _db;
    readonly Repo _repo;
    readonly FakeDirectory _users = new();
    readonly AdminAccounts _accounts;

    public AdminAccountsTests()
    {
        _db = new Db(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dir }).Build());
        _db.Init();
        _repo = new Repo(_db);
        _accounts = new AdminAccounts(_repo, _users);
    }

    // Noms uniques : le cache des sessions est partagé par tous les tests.
    static string Name(string p) => p + Guid.NewGuid().ToString("N")[..6];

    void InsertLegacy(string name, string password)
    {
        var hash = new Microsoft.AspNetCore.Identity.PasswordHasher<string>().HashPassword(name, password);
        using var c = _db.Open();
        Dapper.SqlMapper.Execute(c, "INSERT INTO AdminUsers(Name,PasswordHash,Stamp,CreatedAt) VALUES(@name,@hash,'s','2026-01-01')", new { name, hash });
    }

    [Fact]
    public async Task Un_administrateur_se_connecte_avec_le_mot_de_passe_de_son_compte()
    {
        var n = Name("alice");
        await _users.CreateAsync(n, "un-bon-mot-de-passe", admin: true);

        Assert.True((await _accounts.ValidateAsync(n, "un-bon-mot-de-passe")).Ok);
        Assert.True((await _accounts.ValidateAsync($"  {n.ToUpperInvariant()} ", "un-bon-mot-de-passe")).Ok); // casse et espaces tolérés sur l'identifiant
        Assert.False((await _accounts.ValidateAsync(n, "mauvais")).Ok);
        Assert.False((await _accounts.ValidateAsync("inconnu", "un-bon-mot-de-passe")).Ok);
        Assert.False((await _accounts.ValidateAsync(n, "")).Ok);
        Assert.False((await _accounts.ValidateAsync("", "")).Ok);
    }

    [Fact]
    public async Task Un_utilisateur_qui_n_est_pas_administrateur_est_refuse_avec_son_motif()
    {
        var n = Name("bob");
        await _users.CreateAsync(n, "mot-de-passe-de-bob", admin: false);

        var r = await _accounts.ValidateAsync(n, "mot-de-passe-de-bob");
        Assert.False(r.Ok);
        Assert.True(r.NotAdmin);

        await _users.SetAdminAsync(n, true); // devenu administrateur : il entre
        Assert.True((await _accounts.ValidateAsync(n, "mot-de-passe-de-bob")).Ok);
    }

    [Fact]
    public async Task Changer_le_mot_de_passe_invalide_les_sessions_ouvertes()
    {
        var n = Name("alice");
        await _users.CreateAsync(n, "ancien-mot-de-passe", admin: true);
        var session = AdminAccounts.PrincipalFor(await _accounts.ValidateAsync(n, "ancien-mot-de-passe"));
        Assert.True(await _accounts.IsStillValidAsync(session));

        await _users.SetPasswordAsync(n, "nouveau-mot-de-passe");
        AdminAccounts.Forget(n); // ce que fait l'interface quand elle change un mot de passe

        Assert.False(await _accounts.IsStillValidAsync(session));
        Assert.False((await _accounts.ValidateAsync(n, "ancien-mot-de-passe")).Ok);
        Assert.True((await _accounts.ValidateAsync(n, "nouveau-mot-de-passe")).Ok);
    }

    [Fact]
    public async Task Retirer_le_role_ou_supprimer_le_compte_ferme_les_sessions()
    {
        var n = Name("carol");
        await _users.CreateAsync(n, "mot-de-passe-de-carol", admin: true);
        var session = AdminAccounts.PrincipalFor(await _accounts.ValidateAsync(n, "mot-de-passe-de-carol"));

        await _users.SetAdminAsync(n, false);
        AdminAccounts.Forget(n);
        Assert.False(await _accounts.IsStillValidAsync(session));

        await _users.SetAdminAsync(n, true);
        AdminAccounts.Forget(n);
        Assert.True(await _accounts.IsStillValidAsync(session));

        _users.Accounts.Remove(n);
        AdminAccounts.Forget(n);
        Assert.False(await _accounts.IsStillValidAsync(session));
    }

    [Fact]
    public async Task Une_panne_de_l_agent_ne_deconnecte_personne()
    {
        var n = Name("alice");
        await _users.CreateAsync(n, "mot-de-passe-alice", admin: true);
        var session = AdminAccounts.PrincipalFor(await _accounts.ValidateAsync(n, "mot-de-passe-alice"));
        AdminAccounts.Forget(n);

        _users.Down = true;
        Assert.True(await _accounts.IsStillValidAsync(session));
    }

    [Fact]
    public async Task Une_session_sans_tampon_est_refusee()
    {
        var n = Name("alice");
        await _users.CreateAsync(n, "mot-de-passe-alice", admin: true);
        var forged = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, n)], "x"));
        Assert.False(await _accounts.IsStillValidAsync(forged));
    }

    [Fact]
    public async Task Le_premier_administrateur_est_un_utilisateur_du_groupe_admins()
    {
        Assert.True(await _accounts.NeedsSetupAsync());

        await _accounts.CreateFirstAsync("admin1", "un-bon-mot-de-passe");

        Assert.False(await _accounts.NeedsSetupAsync());
        Assert.True(_users.Accounts["admin1"].Admin);
        Assert.True((await _accounts.ValidateAsync("admin1", "un-bon-mot-de-passe")).Ok);
        await Assert.ThrowsAsync<AgentException>(() => _accounts.CreateFirstAsync("admin1", "un-bon-mot-de-passe")); // doublon
        await Assert.ThrowsAsync<AgentException>(() => _accounts.CreateFirstAsync("Ro ot", "un-bon-mot-de-passe"));
        await Assert.ThrowsAsync<AgentException>(() => _accounts.CreateFirstAsync("bob", "court"));
    }

    [Fact]
    public async Task Agent_muet_pendant_l_installation_ne_propose_pas_de_creer_un_compte()
    {
        _users.Down = true;
        Assert.False(await _accounts.NeedsSetupAsync());
    }

    [Fact]
    public async Task Un_ancien_administrateur_est_converti_en_utilisateur_a_sa_premiere_connexion()
    {
        var n = Name("ancien");
        InsertLegacy(n, "ancien-mot-de-passe");
        Assert.False(await _accounts.NeedsSetupAsync()); // il existe déjà un administrateur : pas d'installation

        Assert.False((await _accounts.ValidateAsync(n, "mauvais")).Ok);
        Assert.Empty(_users.Accounts);

        Assert.True((await _accounts.ValidateAsync(n, "ancien-mot-de-passe")).Ok);
        Assert.True(_users.Accounts[n].Admin);
        Assert.Null(await _repo.GetAdminAsync(n)); // l'ancien compte est retiré
        Assert.Contains(await _repo.GetAuditAsync(), a => a.Action == "Administrator converted to user");
    }

    [Fact]
    public async Task Un_ancien_administrateur_qui_a_deja_un_compte_utilisateur_le_retrouve_administrateur()
    {
        var n = Name("ancien");
        await _users.CreateAsync(n, "mot-de-passe-du-partage", admin: false);
        InsertLegacy(n, "ancien-mot-de-passe");

        Assert.True((await _accounts.ValidateAsync(n, "ancien-mot-de-passe")).Ok);
        Assert.True(_users.Accounts[n].Admin);
        Assert.Equal("ancien-mot-de-passe", _users.Accounts[n].Password);
    }

    [Fact]
    public async Task Le_profil_d_un_utilisateur_se_conserve_se_modifie_et_se_supprime()
    {
        await _repo.SetUserProfileAsync("alice", "Alice Tremblay", "alice@example.com");
        var p = (await _repo.GetUserProfilesAsync())["ALICE"]; // insensible à la casse
        Assert.Equal(("Alice Tremblay", "alice@example.com"), (p.FullName, p.Email));

        await _repo.SetUserProfileAsync("alice", "Alice T.", "");
        Assert.Equal("Alice T.", (await _repo.GetUserProfilesAsync())["alice"].FullName);

        await _repo.SetUserProfileAsync("alice", "", ""); // profil vide : la ligne disparaît
        Assert.Empty(await _repo.GetUserProfilesAsync());

        await _repo.SetUserProfileAsync("bob", "Bob", "");
        await _repo.DeleteUserProfileAsync("bob");
        Assert.Empty(await _repo.GetUserProfilesAsync());
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData("Alice Tremblay", "alice@example.com", true)]
    [InlineData("Alice", "pas-un-courriel", false)]
    [InlineData("Ali\nce", "", false)]
    public void Validation_du_profil(string fullName, string email, bool ok)
    {
        Assert.Equal(ok, NasOperations.ValidateProfile(fullName, email) is null);
    }

    [Fact]
    public void Un_nom_complet_trop_long_est_refuse()
    {
        Assert.NotNull(NasOperations.ValidateProfile(new string('x', NasOperations.MaxFullNameLength + 1), ""));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}

public class LoginThrottleTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Cinq_echecs_bloquent_le_compte_pendant_cinq_minutes()
    {
        var clock = new Clock();
        var t = new LoginThrottle(clock);
        for (var i = 0; i < 4; i++) t.Fail("alice", $"10.0.0.{i}");
        Assert.False(t.IsLocked("alice", "10.0.0.99"));

        t.Fail("alice", "10.0.0.5");
        Assert.True(t.IsLocked("alice", "10.0.0.99"));
        Assert.True(t.IsLocked("ALICE ", "10.0.0.99")); // même compte, autre graphie
        Assert.False(t.IsLocked("bob", "10.0.0.99"));

        clock.Now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        Assert.False(t.IsLocked("alice", "10.0.0.99"));
    }

    [Fact]
    public void Une_connexion_reussie_remet_le_compteur_du_compte_a_zero()
    {
        var t = new LoginThrottle(new Clock());
        for (var i = 0; i < 4; i++) t.Fail("alice", "10.0.0.1");
        t.Success("alice");
        t.Fail("alice", "10.0.0.1");
        Assert.False(t.IsLocked("alice", "10.0.0.1"));
    }

    [Fact]
    public void Vingt_echecs_depuis_une_adresse_la_bloquent_meme_avec_des_comptes_differents()
    {
        var t = new LoginThrottle(new Clock());
        for (var i = 0; i < LoginThrottle.MaxPerIp; i++) t.Fail($"user{i}", "10.0.0.1");
        Assert.True(t.IsLocked("nouveau", "10.0.0.1"));
        Assert.False(t.IsLocked("nouveau", "10.0.0.2"));
    }

    [Fact]
    public void Les_anciens_echecs_sont_oublies()
    {
        var clock = new Clock();
        var t = new LoginThrottle(clock);
        for (var i = 0; i < 4; i++) t.Fail("alice", "10.0.0.1");
        clock.Now += TimeSpan.FromMinutes(11);
        t.Fail("alice", "10.0.0.1"); // repart de 1, pas de 5
        Assert.False(t.IsLocked("alice", "10.0.0.1"));
    }
}

public class LocalUrlTests
{
    [Theory]
    [InlineData("/shares", "/shares")]
    [InlineData("/datasets?x=1", "/datasets?x=1")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/ok\r\nSet-Cookie: x=1", "/")]
    public void Redirection_uniquement_vers_une_adresse_locale(string? input, string expected) =>
        Assert.Equal(expected, LocalUrl.Safe(input));
}
