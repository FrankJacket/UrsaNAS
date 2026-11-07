using Microsoft.Extensions.Configuration;
using Nas.Web;
using Nas.Web.Auth;
using Nas.Web.Data;
using Xunit;

namespace Nas.Agent.Tests;

public class AdminAccountsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-auth-" + Guid.NewGuid().ToString("N"));
    readonly Repo _repo;
    readonly AdminAccounts _accounts;

    public AdminAccountsTests()
    {
        var db = new Db(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dir }).Build());
        db.Init();
        _repo = new Repo(db);
        _accounts = new AdminAccounts(_repo);
    }

    [Fact]
    public async Task Le_mot_de_passe_est_hache_et_la_connexion_ne_marche_qu_avec_le_bon()
    {
        await _accounts.CreateAsync("alice", "un-bon-mot-de-passe");

        var stored = await _repo.GetAdminAsync("alice");
        Assert.NotNull(stored);
        Assert.DoesNotContain("un-bon-mot-de-passe", stored!.PasswordHash);

        Assert.NotNull(await _accounts.ValidateAsync("alice", "un-bon-mot-de-passe"));
        Assert.NotNull(await _accounts.ValidateAsync("  ALICE ", "un-bon-mot-de-passe")); // casse et espaces tolérés sur l'identifiant
        Assert.Null(await _accounts.ValidateAsync("alice", "mauvais"));
        Assert.Null(await _accounts.ValidateAsync("inconnu", "un-bon-mot-de-passe"));
        Assert.Null(await _accounts.ValidateAsync("alice", ""));
        Assert.Null(await _accounts.ValidateAsync("", ""));
    }

    [Fact]
    public async Task Refuse_nom_invalide_mot_de_passe_court_et_doublon()
    {
        await Assert.ThrowsAsync<AgentException>(() => _accounts.CreateAsync("Ro ot", "un-bon-mot-de-passe"));
        await Assert.ThrowsAsync<AgentException>(() => _accounts.CreateAsync("root", "un-bon-mot-de-passe"));
        await Assert.ThrowsAsync<AgentException>(() => _accounts.CreateAsync("bob", "court"));
        await _accounts.CreateAsync("bob", "un-bon-mot-de-passe");
        await Assert.ThrowsAsync<AgentException>(() => _accounts.CreateAsync("bob", "un-autre-mot-de-passe"));
        Assert.Equal(1, await _accounts.CountAsync());
    }

    [Fact]
    public async Task Changer_le_mot_de_passe_invalide_les_sessions_ouvertes()
    {
        await _accounts.CreateAsync("alice", "ancien-mot-de-passe");
        var session = AdminAccounts.PrincipalFor((await _accounts.ValidateAsync("alice", "ancien-mot-de-passe"))!);
        Assert.True(await _accounts.IsStillValidAsync(session));

        await _accounts.SetPasswordAsync("alice", "nouveau-mot-de-passe");

        Assert.False(await _accounts.IsStillValidAsync(session));
        Assert.Null(await _accounts.ValidateAsync("alice", "ancien-mot-de-passe"));
        Assert.NotNull(await _accounts.ValidateAsync("alice", "nouveau-mot-de-passe"));
    }

    [Fact]
    public async Task Supprimer_un_compte_ferme_ses_sessions_mais_jamais_le_dernier_ni_soi_meme()
    {
        await _accounts.CreateAsync("alice", "mot-de-passe-alice");
        await _accounts.CreateAsync("bob", "mot-de-passe-de-bob");
        var bobSession = AdminAccounts.PrincipalFor((await _accounts.ValidateAsync("bob", "mot-de-passe-de-bob"))!);

        var self = await Assert.ThrowsAsync<AgentException>(() => _accounts.DeleteAsync("alice", actor: "Alice"));
        Assert.Contains("own account", self.Message);

        await _accounts.DeleteAsync("bob", actor: "alice");
        Assert.False(await _accounts.IsStillValidAsync(bobSession));

        var last = await Assert.ThrowsAsync<AgentException>(() => _accounts.DeleteAsync("alice", actor: "bob"));
        Assert.Contains("last administrator", last.Message);
        Assert.Equal(1, await _accounts.CountAsync());
    }

    [Fact]
    public async Task Une_session_sans_tampon_est_refusee()
    {
        await _accounts.CreateAsync("alice", "mot-de-passe-alice");
        var forged = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "alice")], "x"));
        Assert.False(await _accounts.IsStillValidAsync(forged));
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
