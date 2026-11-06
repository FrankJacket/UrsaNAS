using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Nas.Contracts;
using Nas.Web;
using Nas.Web.Alerting;
using Nas.Web.Data;

namespace Nas.Agent.Tests;

public class BackupAlertRuleTests
{
    static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    static List<Candidate> Eval(params BackupHealth[] b) => AlertRules.Evaluate(new HealthSnapshot(true, [], [], b), Now);

    static BackupHealth Job(string? last = "Success", int daysSinceSuccess = 1, int mode = BackupSchedule.Daily, bool enabled = true) =>
        new(1, "Photos", enabled, mode, Now.AddDays(-30), last, Now.AddDays(-daysSinceSuccess));

    [Fact]
    public void Une_sauvegarde_en_bonne_sante_ne_declenche_rien() => Assert.Empty(Eval(Job()));

    [Fact]
    public void Un_echec_est_critique_et_des_erreurs_partielles_un_avertissement()
    {
        Assert.Equal(AlertRules.Critical, Assert.Single(Eval(Job("Failed"))).Severity);
        var partial = Assert.Single(Eval(Job("CompletedWithErrors")));
        Assert.Equal(AlertRules.Warning, partial.Severity);
        Assert.Equal("backup:1:failed", partial.Code); // même code : la réussite suivante ferme l'alerte
    }

    [Fact]
    public void Une_sauvegarde_planifiee_sans_reussite_recente_est_signalee()
    {
        Assert.Equal("backup:1:stale", Assert.Single(Eval(Job(daysSinceSuccess: 4))).Code);
        Assert.Empty(Eval(Job(daysSinceSuccess: 4, mode: BackupSchedule.Manual))); // un travail manuel n'a pas d'échéance
    }

    [Fact]
    public void Un_travail_jamais_reussi_compte_depuis_sa_creation()
    {
        var neverRan = new BackupHealth(1, "Photos", true, BackupSchedule.Daily, Now.AddDays(-1), null, null);
        Assert.Empty(Eval(neverRan));
        Assert.Single(Eval(neverRan with { CreatedUtc = Now.AddDays(-5) }));
    }

    [Fact]
    public void Un_travail_desactive_ou_interrompu_ne_declenche_rien()
    {
        Assert.Empty(Eval(Job("Failed", enabled: false)));
        Assert.Empty(Eval(Job("Interrupted")));
    }
}

public class BackupRepoTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-brepo-" + Guid.NewGuid().ToString("N"));
    readonly BackupRepo _repo;

    public BackupRepoTests()
    {
        var db = new Db(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dir }).Build());
        db.Init();
        _repo = new BackupRepo(db);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    static BackupJobRow Job(string name = "Photos", string dir = "/mnt/usb") => new() { Name = name, Share = "photos", TargetDir = dir, DestFolder = "photos", Includes = "2024\n2025" };

    [Fact]
    public async Task Enregistre_et_relit_un_travail()
    {
        var id = await _repo.SaveJobAsync(Job());
        var j = (await _repo.GetJobAsync(id))!;
        Assert.Equal("Photos", j.Name);
        Assert.Equal(["2024", "2025"], j.IncludeList());
        Assert.Contains("@eaDir", j.ExcludeList());

        j.Name = "Photos 2";
        j.Snapshot = true;
        await _repo.SaveJobAsync(j);
        Assert.Equal("Photos 2", (await _repo.GetJobAsync(id))!.Name);
        Assert.True((await _repo.GetJobAsync(id))!.Snapshot);
    }

    [Fact]
    public async Task Un_disque_initialise_est_adopte_par_les_travaux_du_meme_disque_seulement()
    {
        var a = await _repo.SaveJobAsync(Job("A", "/mnt/usb"));
        var b = await _repo.SaveJobAsync(Job("B", "/mnt/usb"));
        var c = await _repo.SaveJobAsync(Job("C", "/mnt/autre"));
        await _repo.AdoptDiskAsync("/mnt/usb", "marker");
        Assert.Equal("marker", (await _repo.GetJobAsync(a))!.TargetId);
        Assert.Equal("marker", (await _repo.GetJobAsync(b))!.TargetId);
        Assert.Null((await _repo.GetJobAsync(c))!.TargetId);
    }

    [Fact]
    public async Task Suit_les_executions_de_l_ouverture_a_la_fin()
    {
        var id = await _repo.SaveJobAsync(Job());
        var job = (await _repo.GetJobAsync(id))!;
        await _repo.AddRunAsync(job, "Manual");
        var open = (await _repo.GetOpenRunAsync(id))!;
        Assert.Equal("Running", open.Status);
        Assert.Null(await _repo.LastFinishedRunAsync(id));

        open.Status = "Success"; open.EndedUtc = DateTime.UtcNow; open.Copied = 12;
        await _repo.FinishRunAsync(open);

        Assert.Null(await _repo.GetOpenRunAsync(id));
        Assert.Equal(12, (await _repo.LastFinishedRunAsync(id))!.Copied);
        var health = Assert.Single(await _repo.GetHealthAsync());
        Assert.Equal("Success", health.LastStatus);
        Assert.NotNull(health.LastSuccessUtc);
    }

    [Fact]
    public async Task Supprimer_un_travail_supprime_son_historique_et_ne_garde_que_les_dernieres_executions()
    {
        var id = await _repo.SaveJobAsync(Job());
        var job = (await _repo.GetJobAsync(id))!;
        for (var i = 0; i < 5; i++) { await _repo.AddRunAsync(job, "Manual"); var r = (await _repo.GetOpenRunAsync(id))!; r.Status = "Success"; r.EndedUtc = DateTime.UtcNow; await _repo.FinishRunAsync(r); }

        await _repo.PruneRunsAsync(id, keep: 2);
        Assert.Equal(2, (await _repo.GetRunsAsync(id)).Count);

        await _repo.DeleteJobAsync(id);
        Assert.Empty(await _repo.GetRunsAsync(id));
        Assert.Null(await _repo.GetJobAsync(id));
    }
}

/// <summary>Un agent minimal : mémorise les copies démarrées et répond aux appels de la sauvegarde.</summary>
sealed class FakeAgentHandler : HttpMessageHandler
{
    public List<BackupJobDef> Started { get; } = [];
    public List<int> Acknowledged { get; } = [];
    public List<BackupRunStatus> Runs { get; } = [];
    public BackupDiskState Disk { get; set; } = BackupDiskState.Connected;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };
        var response = (request.Method.Method, path) switch
        {
            ("GET", "backup/runs") => Json(Runs.ToList()),
            ("GET", "backup/disk") => Json(new BackupDiskStatus(Disk, 100, 1000)),
            ("POST", "backup/runs") => Start(request),
            ("DELETE", _) when path.StartsWith("backup/runs/") => Ack(path),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        return Task.FromResult(response);
    }

    HttpResponseMessage Start(HttpRequestMessage request)
    {
        Started.Add(JsonSerializer.Deserialize<BackupJobDef>(request.Content!.ReadAsStringAsync().Result, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    HttpResponseMessage Ack(string path)
    {
        var id = int.Parse(path[(path.LastIndexOf('/') + 1)..]);
        Acknowledged.Add(id);
        Runs.RemoveAll(r => r.JobId == id);
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    public static BackupRunStatus Finished(int jobId, string status, long copied = 3) =>
        new(jobId, false, "Finished", null, 0, 10, 1000, copied, 300, 7, 0, 0, 0, DateTime.UtcNow, DateTime.UtcNow, new BackupResult(status, null, [], 1));

    public static BackupRunStatus Active(int jobId) =>
        new(jobId, false, "Copying", "a.txt", 5, 1, 5, 0, 0, 0, 0, 0, 0, DateTime.UtcNow, DateTime.UtcNow, null);
}

public class BackupCoordinatorTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-bcoord-" + Guid.NewGuid().ToString("N"));
    readonly FakeAgentHandler _agent = new();
    readonly BackupRepo _jobs;
    readonly Repo _repo;
    readonly BackupCoordinator _coordinator;

    public BackupCoordinatorTests()
    {
        var db = new Db(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dir }).Build());
        db.Init();
        _jobs = new BackupRepo(db);
        _repo = new Repo(db);
        _coordinator = new BackupCoordinator(new AgentClient(new HttpClient(_agent) { BaseAddress = new Uri("http://agent/") }), _repo, _jobs);
        _repo.UpsertShareAsync(new ShareDef("photos", "", false, [], "tank/photos", Acl: true)).Wait();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    async Task<BackupJobRow> NewJobAsync(int mode = BackupSchedule.Manual, string? targetId = "marker", bool snapshot = false)
    {
        var job = new BackupJobRow
        {
            Name = "Photos", Share = "photos", TargetDir = "/mnt/usb", TargetId = targetId, DestFolder = "photos", ScheduleMode = mode,
            DailyTime = "00:00", Snapshot = snapshot, CreatedUtc = DateTime.UtcNow.AddDays(-2),
        };
        job.Id = await _jobs.SaveJobAsync(job);
        return job;
    }

    [Fact]
    public async Task Demarrer_envoie_la_definition_a_l_agent_avec_le_jeu_de_donnees_du_partage()
    {
        var job = await NewJobAsync(snapshot: true);
        await _coordinator.StartAsync(job, "Manual", "admin");

        var sent = Assert.Single(_agent.Started);
        Assert.Equal("photos", sent.Share);
        Assert.Equal("tank/photos", sent.Dataset);
        Assert.True(sent.Snapshot);
        Assert.Equal("Running", (await _jobs.GetOpenRunAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task Refuse_de_demarrer_un_travail_sans_disque_initialise()
    {
        var job = await NewJobAsync(targetId: null);
        await Assert.ThrowsAsync<AgentException>(() => _coordinator.StartAsync(job, "Manual", "admin"));
        Assert.Empty(_agent.Started);
    }

    [Fact]
    public async Task Enregistre_la_fin_d_une_copie_puis_la_fait_oublier_a_l_agent()
    {
        var job = await NewJobAsync();
        await _coordinator.StartAsync(job, "Manual", "admin");
        _agent.Runs.Add(FakeAgentHandler.Finished(job.Id, "Success"));

        await _coordinator.SyncAsync();

        var run = (await _jobs.LastFinishedRunAsync(job.Id))!;
        Assert.Equal("Success", run.Status);
        Assert.Equal(3, run.Copied);
        Assert.Equal(10, (await _jobs.GetJobAsync(job.Id))!.EstimatedFiles);
        Assert.Equal([job.Id], _agent.Acknowledged);
    }

    [Fact]
    public async Task Marque_interrompue_une_copie_que_l_agent_a_perdue_mais_pas_une_copie_en_cours()
    {
        var job = await NewJobAsync();
        await _coordinator.StartAsync(job, "Manual", "admin");
        _agent.Runs.Add(FakeAgentHandler.Active(job.Id));
        await _coordinator.SyncAsync();
        Assert.NotNull(await _jobs.GetOpenRunAsync(job.Id)); // toujours en cours

        _agent.Runs.Clear(); // l'agent a redémarré
        await _coordinator.SyncAsync();
        Assert.Null(await _jobs.GetOpenRunAsync(job.Id));
        Assert.Equal("Interrupted", (await _jobs.LastFinishedRunAsync(job.Id))!.Status);
    }

    [Fact]
    public async Task Lance_un_travail_quotidien_dont_l_heure_est_passee_une_seule_fois()
    {
        await NewJobAsync(BackupSchedule.Daily);
        var present = new Dictionary<int, bool>();
        var noon = DateTime.Now.Date.AddHours(12);

        await _coordinator.ScheduleAsync(noon, present);
        Assert.Single(_agent.Started);
        Assert.Equal("Schedule", (await _jobs.GetRunsAsync()).Single().Reason);

        await _coordinator.ScheduleAsync(noon.AddMinutes(1), present);
        Assert.Single(_agent.Started); // déjà lancé aujourd'hui
    }

    [Fact]
    public async Task N_attend_pas_l_heure_et_ne_lance_rien_si_le_disque_est_absent_ou_la_copie_deja_en_cours()
    {
        var job = await NewJobAsync(BackupSchedule.Daily);
        var present = new Dictionary<int, bool>();
        job.DailyTime = "23:00";
        await _jobs.SaveJobAsync(job);
        await _coordinator.ScheduleAsync(DateTime.Now.Date.AddHours(12), present); // l'heure n'est pas venue
        Assert.Empty(_agent.Started);

        job.DailyTime = "00:00";
        await _jobs.SaveJobAsync(job);

        _agent.Disk = BackupDiskState.Missing;
        await _coordinator.ScheduleAsync(DateTime.Now.Date.AddHours(12), present);
        Assert.Empty(_agent.Started);

        _agent.Disk = BackupDiskState.Connected;
        _agent.Runs.Add(FakeAgentHandler.Active(job.Id));
        await _coordinator.ScheduleAsync(DateTime.Now.Date.AddHours(12), present);
        Assert.Empty(_agent.Started);
    }

    [Fact]
    public async Task Lance_un_travail_des_que_le_disque_est_branche_et_pas_a_chaque_passage()
    {
        await NewJobAsync(BackupSchedule.OnPlug);
        var present = new Dictionary<int, bool>();

        _agent.Disk = BackupDiskState.Missing;
        await _coordinator.ScheduleAsync(DateTime.Now, present);
        Assert.Empty(_agent.Started);

        _agent.Disk = BackupDiskState.Connected;
        await _coordinator.ScheduleAsync(DateTime.Now, present);
        Assert.Single(_agent.Started);
        Assert.Equal("Disk connected", (await _jobs.GetRunsAsync()).Single().Reason);

        await _coordinator.ScheduleAsync(DateTime.Now, present);
        Assert.Single(_agent.Started); // le disque est resté branché
    }

    [Fact]
    public async Task Un_travail_desactive_n_est_pas_lance()
    {
        var job = await NewJobAsync(BackupSchedule.OnPlug);
        job.Enabled = false;
        await _jobs.SaveJobAsync(job);
        await _coordinator.ScheduleAsync(DateTime.Now, new Dictionary<int, bool>());
        Assert.Empty(_agent.Started);
    }
}
