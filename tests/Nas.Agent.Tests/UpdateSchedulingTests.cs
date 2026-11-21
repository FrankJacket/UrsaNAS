using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Contracts;
using Nas.Web;
using Nas.Web.Alerting;
using Nas.Web.Data;
using Xunit;

namespace Nas.Agent.Tests;

public class UpdateScheduleRepoTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-upd-repo-" + Guid.NewGuid().ToString("N"));
    readonly Repo _repo;

    public UpdateScheduleRepoTests()
    {
        var db = new Db(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dir }).Build());
        db.Init();
        _repo = new Repo(db);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task Sans_reglage_enregistre_les_valeurs_par_defaut_sont_relues() =>
        Assert.Equal(UpdateScheduleSettings.Default, await _repo.GetUpdateScheduleAsync());

    [Fact]
    public async Task Les_reglages_enregistres_sont_relus_a_l_identique()
    {
        var s = new UpdateScheduleSettings("weekly", DayOfWeek.Wednesday, "02:30", true, true, true, true, DayOfWeek.Friday, "23:15");
        await _repo.SaveUpdateScheduleAsync(s);
        Assert.Equal(s, await _repo.GetUpdateScheduleAsync());
    }
}

public class UpdateScheduleDueTests
{
    static readonly UpdateScheduleSettings Weekly = UpdateScheduleSettings.Default with { CheckFrequency = "weekly", CheckDay = DayOfWeek.Monday, CheckTime = "03:00" };
    static readonly UpdateScheduleSettings Daily = UpdateScheduleSettings.Default with { CheckFrequency = "daily", CheckTime = "03:00" };
    static readonly DateTime Monday0400 = new(2026, 11, 9, 4, 0, 0); // un lundi
    static readonly DateTime Tuesday0400 = new(2026, 11, 10, 4, 0, 0);

    [Fact]
    public void Desactivee_la_verification_n_est_jamais_due() =>
        Assert.False(UpdateMonitor.IsCheckDue(UpdateScheduleSettings.Default, Monday0400, DateTime.MinValue));

    [Fact]
    public void Quotidienne_due_des_que_l_heure_est_passee_puis_plus_avant_le_lendemain()
    {
        Assert.False(UpdateMonitor.IsCheckDue(Daily, Monday0400.Date.AddHours(2), DateTime.MinValue)); // 02:00, pas encore 03:00
        Assert.True(UpdateMonitor.IsCheckDue(Daily, Monday0400, DateTime.MinValue));
        Assert.False(UpdateMonitor.IsCheckDue(Daily, Monday0400.AddMinutes(1), Monday0400.ToUniversalTime())); // déjà fait ce créneau
        Assert.True(UpdateMonitor.IsCheckDue(Daily, Tuesday0400, Monday0400.ToUniversalTime())); // le lendemain, c'est un nouveau créneau
    }

    [Fact]
    public void Hebdomadaire_due_seulement_le_bon_jour()
    {
        Assert.True(UpdateMonitor.IsCheckDue(Weekly, Monday0400, DateTime.MinValue));
        Assert.False(UpdateMonitor.IsCheckDue(Weekly, Tuesday0400, DateTime.MinValue));
    }

    [Fact]
    public void Aucune_case_d_installation_automatique_cochee_rien_n_est_du() =>
        Assert.False(UpdateMonitor.IsAutoInstallDue(UpdateScheduleSettings.Default with { AutoInstallDay = DayOfWeek.Monday, AutoInstallTime = "04:00" },
            Monday0400, DateTime.MinValue));

    [Fact]
    public void L_installation_automatique_suit_le_meme_principe_de_creneau()
    {
        var s = UpdateScheduleSettings.Default with { AutoInstallApp = true, AutoInstallDay = DayOfWeek.Monday, AutoInstallTime = "04:00" };
        Assert.True(UpdateMonitor.IsAutoInstallDue(s, Monday0400, DateTime.MinValue));
        Assert.False(UpdateMonitor.IsAutoInstallDue(s, Monday0400.AddMinutes(1), Monday0400.ToUniversalTime()));
        Assert.False(UpdateMonitor.IsAutoInstallDue(s, Tuesday0400, DateTime.MinValue)); // mauvais jour
    }
}

public class UpdateAlertRuleTests
{
    [Fact]
    public void Une_mise_a_jour_disponible_declenche_une_alerte()
    {
        var status = new UpdateStatus(new UpdateSettings("stable"), new AppUpdateInfo("1.0.0", "1.1.0", true, null, null, false, null, DateTimeOffset.UtcNow, null), null);
        var alert = Assert.Single(AlertRules.Evaluate(new HealthSnapshot(true, [], [], Updates: status)));
        Assert.Equal(("update:app", AlertRules.Warning), (alert.Code, alert.Severity));
    }

    [Fact]
    public void Des_paquets_suivis_disponibles_declenchent_une_alerte_mais_pas_les_non_suivis()
    {
        var pkgs = new PackageUpdates([new("samba", "1", "2", true, false), new("tzdata", "1", "2", false, false)], false, DateTimeOffset.UtcNow, null);
        var status = new UpdateStatus(new UpdateSettings("stable"), null, pkgs);
        var alert = Assert.Single(AlertRules.Evaluate(new HealthSnapshot(true, [], [], Updates: status)));
        Assert.Equal("update:packages", alert.Code);
    }

    [Fact]
    public void A_jour_et_sans_verification_ne_declenchent_rien()
    {
        Assert.Empty(AlertRules.Evaluate(new HealthSnapshot(true, [], [], Updates: null)));
        var upToDate = new UpdateStatus(new UpdateSettings("stable"), new AppUpdateInfo("1.0.0", "1.0.0", false, null, null, false, null, DateTimeOffset.UtcNow, null), null);
        Assert.Empty(AlertRules.Evaluate(new HealthSnapshot(true, [], [], Updates: upToDate)));
    }
}

sealed class FakeUpdateAgentHandler : HttpMessageHandler
{
    public AppUpdateInfo App = new("1.0.0", "1.1.0", true, null, null, false, null, DateTimeOffset.UtcNow, null);
    public PackageUpdates Packages = new([new("samba", "1", "2", true, false), new("docker.io", "1", "2", true, true)], true, DateTimeOffset.UtcNow, null);
    public List<string> InstalledPackages = [];
    public bool AppInstallCalled;
    public bool PowerCalled;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };
        var response = (request.Method.Method, path) switch
        {
            ("GET", "system/update") => Json(new UpdateStatus(new UpdateSettings("stable"), App, Packages)),
            ("POST", "system/update/check-app") => Json(App),
            ("POST", "system/update/check-packages") => Json(Packages),
            ("POST", "system/update/install-app") => Install(),
            ("POST", "system/update/install-packages") => InstallPackages(request),
            ("POST", "system/power/reboot") or ("POST", "system/reboot") => Power(),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        return Task.FromResult(response);
    }

    HttpResponseMessage Install() { AppInstallCalled = true; return new HttpResponseMessage(HttpStatusCode.NoContent); }
    HttpResponseMessage Power() { PowerCalled = true; return new HttpResponseMessage(HttpStatusCode.NoContent); }

    HttpResponseMessage InstallPackages(HttpRequestMessage request)
    {
        var req = request.Content!.ReadFromJsonAsync<InstallPackagesRequest>().Result!;
        InstalledPackages.AddRange(req.Names);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }
}

public class UpdateSchedulerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-upd-sched-" + Guid.NewGuid().ToString("N"));
    readonly Repo _repo;
    readonly FakeUpdateAgentHandler _handler = new();
    readonly UpdateScheduler _scheduler;

    public UpdateSchedulerTests()
    {
        var db = new Db(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dir }).Build());
        db.Init();
        _repo = new Repo(db);
        var agent = new AgentClient(new HttpClient(_handler) { BaseAddress = new Uri("http://agent/") });
        _scheduler = new UpdateScheduler(agent, _repo, NullLogger<UpdateScheduler>.Instance);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task Une_verification_planifiee_journalise_ce_qu_elle_trouve()
    {
        await _scheduler.RunCheckAsync(default);
        var entry = Assert.Single(await _repo.GetAuditAsync());
        Assert.Equal(("system", "Scheduled update check"), (entry.Actor, entry.Action));
        Assert.Contains("1.1.0", entry.Detail);
    }

    [Fact]
    public async Task L_installation_automatique_installe_l_app_et_les_paquets_non_manuels_seulement()
    {
        var s = UpdateScheduleSettings.Default with { AutoInstallApp = true, AutoInstallPackages = true };
        await _scheduler.RunAutoInstallAsync(s, default);

        Assert.True(_handler.AppInstallCalled);
        Assert.Equal(["samba"], _handler.InstalledPackages); // docker.io est "Manuel seulement" : exclu par défaut
        Assert.False(_handler.PowerCalled); // AutoReboot est désactivé
    }

    [Fact]
    public async Task Les_paquets_manuels_sont_inclus_si_demande()
    {
        var s = UpdateScheduleSettings.Default with { AutoInstallPackages = true, AutoInstallManualPackages = true };
        await _scheduler.RunAutoInstallAsync(s, default);
        Assert.Equal(["samba", "docker.io"], _handler.InstalledPackages);
    }

    [Fact]
    public async Task Rien_n_est_installe_si_les_deux_cases_sont_decochees()
    {
        await _scheduler.RunAutoInstallAsync(UpdateScheduleSettings.Default, default);
        Assert.False(_handler.AppInstallCalled);
        Assert.Empty(_handler.InstalledPackages);
    }

    [Fact]
    public async Task Un_redemarrage_planifie_n_a_lieu_que_si_necessaire_et_demande()
    {
        _handler.Packages = _handler.Packages with { RebootRequired = false };
        var s = UpdateScheduleSettings.Default with { AutoInstallPackages = true, AutoReboot = true };
        await _scheduler.RunAutoInstallAsync(s, default);
        Assert.False(_handler.PowerCalled); // pas nécessaire

        _handler.Packages = _handler.Packages with { RebootRequired = true };
        await _scheduler.RunAutoInstallAsync(s, default);
        Assert.True(_handler.PowerCalled);
    }
}
