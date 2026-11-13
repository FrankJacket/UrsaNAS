using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;
using Xunit;

namespace Nas.Agent.Tests;

public sealed class UpdateTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-update-" + Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    sealed class Handler(HttpStatusCode code, string json) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    UpdateService Make(FakeRunner? runner = null, Handler? handler = null, string version = "1.0.0")
    {
        File.WriteAllText(Path.Combine(_dir, "VERSION"), version);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Update:SettingsFile"] = Path.Combine(_dir, "settings.json"),
            ["Update:TokenFile"] = Path.Combine(_dir, "token"),
            ["Update:VersionFile"] = Path.Combine(_dir, "VERSION"),
            ["Update:RebootRequiredFile"] = Path.Combine(_dir, "reboot-required"),
        }).Build();
        return new UpdateService(runner ?? new FakeRunner(), cfg, NullLogger<UpdateService>.Instance, handler);
    }

    const string Releases = """
        [
          {"tag_name":"v1.2.0","draft":false,"prerelease":false,"html_url":"https://x/1.2.0","body":"notes","published_at":"2026-05-01T10:00:00Z"},
          {"tag_name":"v1.3.0-rc1","draft":false,"prerelease":true,"html_url":"https://x/1.3.0-rc1","body":"rc","published_at":"2026-05-10T10:00:00Z"},
          {"tag_name":"v9.0.0","draft":true,"prerelease":false},
          {"tag_name":"v1.1.0","draft":false,"prerelease":false}
        ]
        """;

    [Fact]
    public void Le_canal_stable_ignore_les_preversions_et_les_brouillons()
    {
        var info = UpdateService.Pick("1.0.0", false, Releases);
        Assert.Equal("1.2.0", info.Latest);
        Assert.True(info.Available);
        Assert.False(info.Prerelease);
        Assert.Equal("notes", info.Notes);
    }

    [Fact]
    public void Le_canal_preversion_prend_la_plus_recente()
    {
        var info = UpdateService.Pick("1.0.0", true, Releases);
        Assert.Equal("1.3.0-rc1", info.Latest);
        Assert.True(info.Prerelease);
    }

    [Theory]
    [InlineData("1.2.0", false)]
    [InlineData("1.2.1", false)]
    [InlineData("1.1.9", true)]
    public void Pas_de_mise_a_jour_si_la_version_installee_est_aussi_recente(string current, bool available) =>
        Assert.Equal(available, UpdateService.Pick(current, false, Releases).Available);

    [Fact]
    public void Une_version_finale_depasse_sa_preversion() =>
        Assert.False(UpdateService.Pick("1.3.0", true, Releases).Available);

    [Fact]
    public void Une_preversion_installee_est_depassee_par_la_version_finale() =>
        Assert.True(UpdateService.Pick("1.2.0-rc1", false, Releases).Available);

    [Fact]
    public async Task La_recherche_envoie_le_jeton_et_choisit_la_version()
    {
        var h = new Handler(HttpStatusCode.OK, Releases);
        var svc = Make(handler: h);
        svc.SetSettings(new SetUpdateSettingsRequest("stable", "github_pat_ABCDEFGHIJKLMNOPQRSTUV"));
        var info = await svc.CheckAppAsync(default);
        Assert.Null(info.Error);
        Assert.Equal("1.2.0", info.Latest);
        Assert.Equal("Bearer", h.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("github_pat_ABCDEFGHIJKLMNOPQRSTUV", h.Request.Headers.Authorization.Parameter);
        Assert.Equal(info, svc.Status().App);
    }

    [Fact]
    public async Task Sans_jeton_un_depot_prive_donne_un_message_clair()
    {
        var svc = Make(handler: new Handler(HttpStatusCode.NotFound, "{}"));
        var info = await svc.CheckAppAsync(default);
        Assert.Contains("private", info.Error);
        Assert.Equal("1.0.0", info.Current);
    }

    [Fact]
    public async Task Un_jeton_refuse_est_signale()
    {
        var svc = Make(handler: new Handler(HttpStatusCode.Unauthorized, "{}"));
        svc.SetSettings(new SetUpdateSettingsRequest("stable", "ghp_ABCDEFGHIJKLMNOPQRSTUVWX"));
        Assert.Contains("refused", (await svc.CheckAppAsync(default)).Error);
    }

    [Fact]
    public void Le_jeton_est_garde_a_part_et_jamais_renvoye()
    {
        var svc = Make();
        Assert.Equal(new UpdateSettings("stable", false), svc.GetSettings());
        svc.SetSettings(new SetUpdateSettingsRequest("prerelease", "ghp_ABCDEFGHIJKLMNOPQRSTUVWX"));
        Assert.Equal(new UpdateSettings("prerelease", true), svc.GetSettings());
        Assert.DoesNotContain("ghp_", File.ReadAllText(Path.Combine(_dir, "settings.json")));
        svc.SetSettings(new SetUpdateSettingsRequest("stable", null)); // null : le jeton reste
        Assert.Equal(new UpdateSettings("stable", true), svc.GetSettings());
        svc.SetSettings(new SetUpdateSettingsRequest("stable", ""));
        Assert.Equal(new UpdateSettings("stable", false), svc.GetSettings());
    }

    [Theory]
    [InlineData("nightly", null)]
    [InlineData("stable", "abc")]
    [InlineData("stable", "ghp_ABCDEFGHIJKLMNOPQRSTUVWX\r\nX-Evil: 1")]
    public void Des_reglages_invalides_sont_refuses(string channel, string? token) =>
        Assert.Throws<OperationException>(() => Make().SetSettings(new SetUpdateSettingsRequest(channel, token)));

    [Fact]
    public void La_liste_des_paquets_est_lue_quelle_que_soit_la_langue()
    {
        var items = UpdateService.ParseUpgradable("""
            Listing...
            tzdata/stable-updates 2025c-1 all [upgradable from: 2025b-5]
            samba/stable-security 2:4.22.4+dfsg-1 amd64 [pouvant être mis à jour depuis : 2:4.22.3+dfsg-1]
            docker.io/stable 26.1.6-1 amd64 [upgradable from: 26.1.5-1]
            """);
        Assert.Equal(["docker.io", "samba", "tzdata"], items.Select(i => i.Name));
        var samba = items[1];
        Assert.Equal(("2:4.22.3+dfsg-1", "2:4.22.4+dfsg-1", true, false), (samba.Installed, samba.Candidate, samba.Tracked, samba.ManualOnly));
        Assert.True(items[0].ManualOnly && items[0].Tracked);
        Assert.False(items[2].Tracked);
    }

    [Fact]
    public async Task La_verification_des_paquets_actualise_les_listes_et_signale_le_redemarrage()
    {
        var runner = new FakeRunner
        {
            Handler = c => c.File == "apt"
                ? new CommandResult(0, "Listing...\nsmartmontools/stable 7.5-1 amd64 [upgradable from: 7.4-3]\n", "")
                : new CommandResult(0, "", ""),
        };
        var svc = Make(runner);
        File.WriteAllText(Path.Combine(_dir, "reboot-required"), "");
        var r = await svc.CheckPackagesAsync(default);
        Assert.Contains(runner.Calls, c => c.File == "apt-get" && c.Args.SequenceEqual(["update", "-qq"]));
        Assert.Contains(runner.Calls, c => c.File == "apt" && c.Args.SequenceEqual(["list", "--upgradable"]));
        Assert.Equal("smartmontools", Assert.Single(r.Items).Name);
        Assert.True(r.RebootRequired);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Un_echec_d_actualisation_des_listes_est_signale_sans_perdre_le_resultat()
    {
        var runner = new FakeRunner
        {
            Handler = c => c.File == "apt-get" ? new CommandResult(100, "", "Temporary failure resolving") : new CommandResult(0, "Listing...\n", ""),
        };
        var r = await Make(runner).CheckPackagesAsync(default);
        Assert.Contains("Temporary failure", r.Error);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void La_version_installee_vient_du_fichier_VERSION() => Assert.Equal("2.5.1", Make(version: "2.5.1").CurrentVersion());
}
