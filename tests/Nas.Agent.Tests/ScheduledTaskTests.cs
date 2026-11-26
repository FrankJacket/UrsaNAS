using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Nas.Contracts;
using Nas.Web;
using Nas.Web.Alerting;
using Nas.Web.Data;
using Xunit;

namespace Nas.Agent.Tests;

public class ScheduleRuleTests
{
    // Fuseau fixe pour des tests indépendants de la machine : UTC-5, UTC-4 du 2e dimanche de mars au 1er dimanche de novembre (2 h).
    static readonly TimeZoneInfo Eastern = TimeZoneInfo.CreateCustomTimeZone("test-eastern", TimeSpan.FromHours(-5), "test", "std", "dst",
    [
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2100, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday)),
    ]);

    static DateTime Utc(int y, int month, int d, int h = 0, int minute = 0) => new(y, month, d, h, minute, 0, DateTimeKind.Utc);

    static DateTime? Next(ScheduleRule r, DateTime after, TimeZoneInfo? zone = null) => r.NextAfter(after, zone ?? TimeZoneInfo.Utc);

    [Fact]
    public void Chaque_jour_prend_aujourd_hui_puis_le_lendemain_une_fois_l_heure_atteinte()
    {
        var r = new ScheduleRule { Repeat = RepeatKind.Daily, Time = "02:00" };
        Assert.Equal(Utc(2026, 11, 10, 2), Next(r, Utc(2026, 11, 10, 1)));
        Assert.Equal(Utc(2026, 11, 11, 2), Next(r, Utc(2026, 11, 10, 2))); // strictement après
    }

    [Fact]
    public void Aux_n_heures_compte_depuis_minuit_a_la_minute_choisie()
    {
        var r = new ScheduleRule { Repeat = RepeatKind.Hours, EveryHours = 6, Minute = 15 };
        Assert.Equal(Utc(2026, 11, 10, 6, 15), Next(r, Utc(2026, 11, 10, 0, 15)));
        Assert.Equal(Utc(2026, 11, 11, 0, 15), Next(r, Utc(2026, 11, 10, 18, 15)));
    }

    [Fact]
    public void Chaque_semaine_ne_prend_que_les_jours_coches()
    {
        var r = new ScheduleRule { Repeat = RepeatKind.Weekly, Time = "03:00", Days = (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Friday) };
        Assert.Equal(Utc(2026, 11, 13, 3), Next(r, Utc(2026, 11, 9, 4))); // lundi passé → vendredi
        Assert.Equal(Utc(2026, 11, 16, 3), Next(r, Utc(2026, 11, 13, 4)));
    }

    [Fact]
    public void Un_mois_trop_court_prend_son_dernier_jour()
    {
        var r = new ScheduleRule { Repeat = RepeatKind.MonthlyDay, MonthDay = 31, Time = "02:00" };
        Assert.Equal(Utc(2027, 2, 28, 2), Next(r, Utc(2027, 2, 1)));
        Assert.Equal(Utc(2027, 3, 31, 2), Next(r, Utc(2027, 2, 28, 3)));
        Assert.Equal(Utc(2028, 2, 29, 2), Next(r with { MonthDay = ScheduleRule.LastDay }, Utc(2028, 2, 1)));
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, 13)]
    [InlineData(4, 27)]
    [InlineData(ScheduleRule.LastRank, 27)]
    public void Le_nieme_jour_de_semaine_du_mois(int rank, int day)
    {
        var r = new ScheduleRule { Repeat = RepeatKind.MonthlyWeekday, WeekRank = rank, WeekDay = DayOfWeek.Sunday, Time = "02:00" };
        Assert.Equal(Utc(2026, 12, day, 2), Next(r, Utc(2026, 12, 1)));
    }

    [Fact]
    public void Dernier_peut_etre_le_cinquieme()
    {
        var r = new ScheduleRule { Repeat = RepeatKind.MonthlyWeekday, WeekRank = ScheduleRule.LastRank, WeekDay = DayOfWeek.Sunday, Time = "02:00" };
        Assert.Equal(Utc(2026, 11, 29, 2), Next(r, Utc(2026, 11, 1)));
    }

    [Fact]
    public void Une_execution_unique_passee_n_a_plus_de_suite()
    {
        var r = new ScheduleRule { Repeat = RepeatKind.Once, Date = new DateOnly(2026, 12, 1), Time = "08:30" };
        Assert.Equal(Utc(2026, 12, 1, 8, 30), Next(r, Utc(2026, 11, 1)));
        Assert.Null(Next(r, Utc(2026, 12, 1, 8, 30)));
    }

    [Fact]
    public void Une_heure_sautee_s_execute_des_que_l_horloge_reprend()
    {
        var r = new ScheduleRule { Repeat = RepeatKind.Daily, Time = "02:30" };
        // 14 mars 2027 : de 2 h on passe à 3 h (07:00 UTC).
        Assert.Equal(Utc(2027, 3, 14, 7), Next(r, Utc(2027, 3, 14, 5), Eastern));
        Assert.Equal(Utc(2027, 3, 15, 6, 30), Next(r, Utc(2027, 3, 14, 7), Eastern));
    }

    [Fact]
    public void Une_heure_repetee_ne_s_execute_qu_une_fois()
    {
        var r = new ScheduleRule { Repeat = RepeatKind.Daily, Time = "01:30" };
        // 1er novembre 2026 : 1 h 30 se produit à 05:30 puis à 06:30 UTC.
        Assert.Equal(Utc(2026, 11, 1, 5, 30), Next(r, Utc(2026, 11, 1, 4), Eastern));
        Assert.Equal(Utc(2026, 11, 2, 6, 30), Next(r, Utc(2026, 11, 1, 5, 30), Eastern));
    }

    [Fact]
    public void Un_horaire_incomplet_est_refuse()
    {
        Assert.NotNull(new ScheduleRule { Repeat = RepeatKind.Once }.Validate());
        Assert.NotNull(new ScheduleRule { Repeat = RepeatKind.Weekly, Days = 0 }.Validate());
        Assert.NotNull(new ScheduleRule { Repeat = RepeatKind.Daily, Time = "25:00" }.Validate());
        Assert.NotNull(new ScheduleRule { Repeat = RepeatKind.Hours, EveryHours = 5 }.Validate());
        Assert.NotNull(new ScheduleRule { Repeat = "cron" }.Validate());
        Assert.Null(new ScheduleRule().Validate());
    }

    [Fact]
    public void L_horaire_enregistre_est_relu_a_l_identique()
    {
        var rule = new ScheduleRule { Repeat = RepeatKind.Once, Date = new DateOnly(2026, 12, 1), Time = "08:30" };
        var task = new ScheduledTaskRow();
        task.SetRule(rule);
        Assert.Equal(rule, task.Rule());
        task.Schedule = "illisible";
        Assert.Null(task.Rule().NextAfter(Utc(2026, 11, 1), TimeZoneInfo.Utc));
    }
}

public class SnapshotRetentionTests
{
    static SnapshotInfo Snap(string dataset, string name, int day) =>
        new($"{dataset}@{name}", dataset, name, 0, new DateTimeOffset(2026, 11, day, 2, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Seuls_les_instantanes_de_la_tache_au_dela_du_nombre_garde_expirent()
    {
        SnapshotInfo[] all =
        [
            Snap("tank/a", "auto-aaaa-20261101-020000", 1),
            Snap("tank/a", "auto-aaaa-20261102-020000", 2),
            Snap("tank/a", "auto-aaaa-20261103-020000", 3),
            Snap("tank/a", "manuel", 1),
            Snap("tank/a", "ursacopy-1", 1),
            Snap("tank/a", "auto-bbbb-20261001-020000", 1),        // une autre tâche
            Snap("tank/a", "auto-aaaa-20261001-020000-garde", 1),  // renommé à la main
            Snap("tank/b", "auto-aaaa-20261001-020000", 1),        // un autre jeu de données
        ];
        var expired = ScheduledTaskCoordinator.Expired(all, "tank/a", "aaaa", keep: 2);
        Assert.Equal("tank/a@auto-aaaa-20261101-020000", Assert.Single(expired).Name);
        Assert.Empty(ScheduledTaskCoordinator.Expired(all, "tank/a", "", keep: 1)); // sans marque, on ne supprime rien
    }

    [Theory]
    [InlineData("scrub in progress since Sun Nov 29 02:00:01 2026 — 1.2G scanned", "No known data errors", ScrubState.Running)]
    [InlineData("scrub paused since Sun Nov 29 02:00:01 2026", "No known data errors", ScrubState.Running)]
    [InlineData("scrub repaired 0B in 00:10:23 with 0 errors on Sun Nov 29 02:10:24 2026", "No known data errors", ScrubState.Succeeded)]
    [InlineData("scrub repaired 4K in 1 days 00:10:23 with 2 errors on Sun Nov 29 02:10:24 2026", "No known data errors", ScrubState.Errors)]
    [InlineData("scrub repaired 0B in 00:10:23 with 0 errors on Sun Nov 29 02:10:24 2026", "2 data errors, use '-v' for a list", ScrubState.Errors)]
    [InlineData("scrub canceled on Sun Nov 29 02:01:00 2026", "", ScrubState.Cancelled)]
    [InlineData("resilvered 1.2G in 00:01:00 with 0 errors on Sun Nov 29 02:01:00 2026", "", ScrubState.Superseded)]
    [InlineData("", "", ScrubState.Superseded)]
    public void La_ligne_scan_donne_l_etat_du_scrub(string scan, string errors, ScrubState expected) =>
        Assert.Equal(expected, ScheduledTaskCoordinator.ReadScrub(scan, errors));
}

/// <summary>Un agent minimal : des pools et des instantanés en mémoire.</summary>
sealed class FakeZfsAgentHandler : HttpMessageHandler
{
    public List<SnapshotInfo> Snapshots { get; } = [];
    public HashSet<string> BrokenDatasets { get; } = [];
    public List<string> Scrubbed { get; } = [];
    public string Scan { get; set; } = "";
    public bool Unreachable { get; set; }
    public bool PoolsBroken { get; set; }
    public List<ScriptRunRequest> ScriptStarts { get; } = [];
    public Dictionary<string, ScriptRunStatus> ScriptRuns { get; } = [];
    public List<string> ScriptCancels { get; } = [];
    public string? ScriptRefusal { get; set; }

    /// <summary>Termine le dernier script lancé.</summary>
    public void EndScript(string state, int? exitCode = null, string output = "")
    {
        var id = ScriptStarts[^1].Id;
        ScriptRuns[id] = new(id, state, exitCode, output, DateTime.UtcNow, TimeoutMinutes: ScriptStarts[^1].TimeoutMinutes);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Unreachable) throw new HttpRequestException("unreachable");
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };
        static HttpResponseMessage Done() => new(HttpStatusCode.NoContent);
        switch (request.Method.Method, path)
        {
            case ("GET", "scripts/runs"):
                return Json(ScriptRuns.Values.ToList());
            case ("POST", "scripts/runs"):
                var start = (await request.Content!.ReadFromJsonAsync<ScriptRunRequest>(ct))!;
                if (ScriptRefusal is not null)
                    return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { detail = ScriptRefusal }) };
                ScriptStarts.Add(start);
                ScriptRuns[start.Id] = new(start.Id, ScriptState.Running);
                return Done();
            case ("POST", _) when path.StartsWith("scripts/runs/", StringComparison.Ordinal) && path.EndsWith("/cancel", StringComparison.Ordinal):
                ScriptCancels.Add(path.Split('/')[2]);
                return Done();
            case ("DELETE", _) when path.StartsWith("scripts/runs/", StringComparison.Ordinal):
                ScriptRuns.Remove(path.Split('/')[2]);
                return Done();
            case ("GET", "zfs/pools") when PoolsBroken:
                return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { detail = "zpool not found" }) };
            case ("GET", "zfs/pools"):
                return Json(new[] { new PoolInfo("tank", "ONLINE", 100, 10, 90, Scan, "No known data errors", []) });
            case ("GET", "zfs/snapshots"):
                return Json(Snapshots.ToList());
            case ("POST", "zfs/snapshots"):
                var r = (await request.Content!.ReadFromJsonAsync<CreateSnapshotRequest>(ct))!;
                if (BrokenDatasets.Contains(r.Dataset))
                    return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { detail = "dataset does not exist" }) };
                Snapshots.Add(new($"{r.Dataset}@{r.Name}", r.Dataset, r.Name, 0, DateTimeOffset.UtcNow));
                return Done();
            case ("DELETE", "zfs/snapshots"):
                var name = Uri.UnescapeDataString(request.RequestUri.Query["?name=".Length..]);
                Snapshots.RemoveAll(s => s.Name == name);
                return Done();
            case ("POST", "zfs/pools/tank/scrub"):
                Scrubbed.Add("tank");
                Scan = "scrub in progress since Sun Nov 29 02:00:01 2026";
                return Done();
            default:
                return new(HttpStatusCode.NotFound);
        }
    }
}

public class ScheduledTaskCoordinatorTests : IDisposable
{
    sealed class FakeMail : IMailSender
    {
        public Task SendAsync(MailSettings s, string subject, string body, CancellationToken ct = default) => Task.CompletedTask;
    }

    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-tasks-" + Guid.NewGuid().ToString("N"));
    readonly FakeZfsAgentHandler _agent = new();
    readonly Repo _repo;
    readonly ScheduledTaskRepo _tasks;
    readonly AlertService _alerts;
    readonly ScheduledTaskCoordinator _coordinator;
    static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    public ScheduledTaskCoordinatorTests()
    {
        var db = new Db(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dir }).Build());
        db.Init();
        _repo = new Repo(db);
        _tasks = new ScheduledTaskRepo(db);
        _alerts = new AlertService(_repo, new FakeMail(), new AlertState());
        var client = new AgentClient(new HttpClient(_agent) { BaseAddress = new Uri("http://agent/") });
        _coordinator = new ScheduledTaskCoordinator(client, _repo, _tasks, _alerts);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    async Task<int> AddAsync(string type = ScheduledTaskType.Snapshot, string targets = "tank/a", DateTime? nextRun = null, bool catchUp = false)
    {
        var t = new ScheduledTaskRow { Name = "nuit", Type = type, Targets = targets, Keep = 2, SnapshotTag = "aaaa", CatchUp = catchUp, NextRunUtc = nextRun };
        return await _tasks.SaveTaskAsync(t);
    }

    async Task<ScheduledRunRow> LastRunAsync(int id) => (await _tasks.GetRunsAsync(id))[0];

    [Fact]
    public async Task Une_tache_due_s_execute_et_sa_prochaine_occurrence_est_enregistree()
    {
        var now = DateTime.UtcNow;
        var id = await AddAsync(nextRun: now.AddMinutes(-1));
        await _coordinator.TickAsync(now, Zone);

        var run = await LastRunAsync(id);
        Assert.Equal((TaskRunStatus.Success, "Schedule"), (run.Status, run.Reason));
        Assert.Single(_agent.Snapshots);
        Assert.True((await _tasks.GetTaskAsync(id))!.NextRunUtc > now);

        await _coordinator.TickAsync(now.AddSeconds(20), Zone);
        Assert.Single(await _tasks.GetRunsAsync(id)); // pas deux fois la même occurrence
    }

    [Theory]
    [InlineData(false, TaskRunStatus.Missed, "Schedule", 0)]
    [InlineData(true, TaskRunStatus.Success, "Catch-up", 1)]
    public async Task Une_occurrence_trop_ancienne_est_manquee_ou_rattrapee_une_seule_fois(bool catchUp, string status, string reason, int snapshots)
    {
        var now = DateTime.UtcNow;
        var id = await AddAsync(nextRun: now.AddDays(-4), catchUp: catchUp); // plusieurs occurrences manquées
        await _coordinator.TickAsync(now, Zone);

        var run = Assert.Single(await _tasks.GetRunsAsync(id));
        Assert.Equal((status, reason), (run.Status, run.Reason));
        Assert.Equal(snapshots, _agent.Snapshots.Count);
    }

    [Fact]
    public async Task Agent_injoignable_l_occurrence_reste_due()
    {
        var now = DateTime.UtcNow;
        var id = await AddAsync(nextRun: now.AddMinutes(-1));
        _agent.Unreachable = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => _coordinator.TickAsync(now, Zone));
        Assert.Empty(await _tasks.GetRunsAsync(id));
        Assert.True((await _tasks.GetTaskAsync(id))!.NextRunUtc < now);
    }

    [Fact]
    public async Task La_retention_garde_les_plus_recents_et_ne_touche_pas_aux_autres()
    {
        for (var day = 1; day <= 3; day++)
            _agent.Snapshots.Add(new($"tank/a@auto-aaaa-2026010{day}-020000", "tank/a", $"auto-aaaa-2026010{day}-020000", 0, new DateTimeOffset(2026, 1, day, 2, 0, 0, TimeSpan.Zero)));
        _agent.Snapshots.Add(new("tank/a@manuel", "tank/a", "manuel", 0, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var id = await AddAsync();

        await _coordinator.RunNowAsync(id, "alice");

        Assert.Equal(3, _agent.Snapshots.Count); // le nouveau, le plus récent des anciens et le manuel
        Assert.Contains(_agent.Snapshots, s => s.Snapshot == "manuel");
        Assert.Contains(_agent.Snapshots, s => s.Snapshot == "auto-aaaa-20260103-020000");
    }

    [Fact]
    public async Task Un_echec_ouvre_aussitot_une_alerte_et_les_autres_jeux_sont_traites()
    {
        _agent.BrokenDatasets.Add("tank/a");
        var id = await AddAsync(targets: "tank/a\ntank/b");

        await _coordinator.RunNowAsync(id, "alice");

        var run = await LastRunAsync(id);
        Assert.Equal(TaskRunStatus.Failed, run.Status);
        Assert.Contains("dataset does not exist", run.Message);
        Assert.Equal("tank/b", Assert.Single(_agent.Snapshots).Dataset);
        Assert.Equal($"task:{id}:failed", Assert.Single(await _repo.GetActiveAlertsAsync()).Code);

        // La réussite suivante ferme l'alerte.
        _agent.BrokenDatasets.Clear();
        await Task.Delay(1100); // un nom d'instantané par seconde
        await _coordinator.RunNowAsync(id, "alice");
        await _alerts.ProcessAsync(new HealthSnapshot(true, [], [], Tasks: await _tasks.GetHealthAsync(), CollectedUtc: DateTime.UtcNow));
        Assert.Empty(await _repo.GetActiveAlertsAsync());
    }

    [Fact]
    public async Task Une_collecte_anterieure_a_l_echec_ne_ferme_pas_son_alerte()
    {
        var before = new HealthSnapshot(true, [], [], Tasks: [], CollectedUtc: DateTime.UtcNow.AddSeconds(-5));
        await _alerts.RaiseNowAsync(AlertRules.TaskFailed(1, "nuit"));
        await _alerts.ProcessAsync(before);
        Assert.Single(await _repo.GetActiveAlertsAsync());
    }

    [Fact]
    public async Task Un_scrub_reste_en_cours_jusqu_a_sa_fin_verifiee()
    {
        var id = await AddAsync(ScheduledTaskType.Scrub, "tank");
        await _coordinator.RunNowAsync(id, "alice");
        Assert.Equal(TaskRunStatus.Running, (await LastRunAsync(id)).Status);
        await Assert.ThrowsAsync<AgentException>(() => _coordinator.RunNowAsync(id, "alice"));

        await _coordinator.TickAsync(DateTime.UtcNow, Zone);
        Assert.Equal(TaskRunStatus.Running, (await LastRunAsync(id)).Status);

        _agent.Scan = "scrub repaired 0B in 00:00:01 with 0 errors on Sun Nov 29 02:00:02 2026";
        await _coordinator.TickAsync(DateTime.UtcNow, Zone);
        var done = await LastRunAsync(id);
        Assert.Equal((TaskRunStatus.Success, "Scrub completed without errors."), (done.Status, done.Message));
        Assert.Equal("", done.Pending);
    }

    [Fact]
    public async Task Un_scrub_avec_erreurs_echoue_et_une_occurrence_pendant_l_attente_est_ignoree()
    {
        var id = await AddAsync(ScheduledTaskType.Scrub, "tank");
        await _coordinator.RunNowAsync(id, "alice");
        var now = DateTime.UtcNow;
        await _tasks.SetNextRunAsync(id, now.AddMinutes(-1));
        await _coordinator.TickAsync(now, Zone);
        Assert.Equal(TaskRunStatus.Skipped, (await LastRunAsync(id)).Status);
        Assert.Equal(TaskRunStatus.Running, (await _tasks.GetLatestRunsAsync())[id].Status); // la liste montre l'exécution en cours

        _agent.Scan = "scrub repaired 0B in 00:00:01 with 3 errors on Sun Nov 29 02:00:02 2026";
        await _coordinator.TickAsync(DateTime.UtcNow, Zone);
        Assert.Equal(TaskRunStatus.Failed, (await _tasks.GetRunsAsync(id))[^1].Status);
        Assert.Single(await _repo.GetActiveAlertsAsync());
    }

    [Fact]
    public async Task Une_erreur_au_lancement_reste_le_message_quand_les_autres_pools_reussissent()
    {
        var id = await AddAsync(ScheduledTaskType.Scrub, "absent\ntank");
        await _coordinator.RunNowAsync(id, "alice");
        _agent.Scan = "scrub repaired 0B in 00:00:01 with 0 errors on Sun Nov 29 02:00:02 2026";
        await _coordinator.TickAsync(DateTime.UtcNow, Zone);

        var run = await LastRunAsync(id);
        Assert.Equal(TaskRunStatus.Failed, run.Status);
        Assert.Contains("absent", run.Message);
    }

    [Fact]
    public async Task Un_scrub_deja_en_cours_n_est_pas_relance()
    {
        _agent.Scan = "scrub in progress since Sun Nov 29 02:00:01 2026";
        var id = await AddAsync(ScheduledTaskType.Scrub, "tank");
        await _coordinator.RunNowAsync(id, "alice");
        Assert.Equal(TaskRunStatus.Skipped, (await LastRunAsync(id)).Status);
        Assert.Empty(_agent.Scrubbed);
        Assert.Empty(await _repo.GetActiveAlertsAsync());
    }

    async Task<int> AddScriptAsync(DateTime? nextRun = null) =>
        await _tasks.SaveTaskAsync(new ScheduledTaskRow
        {
            Name = "copie", Type = ScheduledTaskType.Script, Script = "echo bonjour\n", RunAs = "alice", TimeoutMinutes = 5, NextRunUtc = nextRun,
        });

    [Fact]
    public async Task Un_script_reste_en_cours_puis_son_resultat_et_sa_sortie_sont_notes()
    {
        var id = await AddScriptAsync();
        await _coordinator.RunNowAsync(id, "alice");

        var sent = Assert.Single(_agent.ScriptStarts);
        Assert.Equal(("echo bonjour\n", "alice", 5), (sent.Script, sent.RunAs, sent.TimeoutMinutes));
        var run = await LastRunAsync(id);
        Assert.Equal((TaskRunStatus.Running, ScheduledTaskCoordinator.ScriptPrefix + sent.Id), (run.Status, run.Pending));
        await Assert.ThrowsAsync<AgentException>(() => _coordinator.RunNowAsync(id, "alice"));

        _agent.PoolsBroken = true; // un ZFS en panne ne retient pas le résultat d'un script
        _agent.EndScript(ScriptState.Succeeded, 0, "bonjour\n");
        await _coordinator.TickAsync(DateTime.UtcNow, Zone);

        run = await LastRunAsync(id);
        Assert.Equal((TaskRunStatus.Success, "Script completed.", "bonjour\n", ""), (run.Status, run.Message, run.Details, run.Pending));
        Assert.NotNull(run.EndedUtc);
        Assert.Empty(_agent.ScriptRuns); // acquitté
    }

    [Theory]
    [InlineData(ScriptState.Failed, 3, TaskRunStatus.Failed, "The script ended with code 3.", 1)]
    [InlineData(ScriptState.TimedOut, null, TaskRunStatus.Failed, "The script exceeded its time limit of 5 minute(s).", 1)]
    [InlineData(ScriptState.Stopped, null, TaskRunStatus.Cancelled, "Stopped by an administrator.", 0)]
    public async Task Le_resultat_d_un_script_decide_de_l_alerte(string state, int? code, string status, string message, int alerts)
    {
        var id = await AddScriptAsync();
        await _coordinator.RunNowAsync(id, "alice");
        _agent.EndScript(state, code);
        await _coordinator.TickAsync(DateTime.UtcNow, Zone);

        var run = await LastRunAsync(id);
        Assert.Equal((status, message), (run.Status, run.Message));
        Assert.Null(run.Details);
        Assert.Equal(alerts, (await _repo.GetActiveAlertsAsync()).Count);
    }

    [Fact]
    public async Task Un_script_refuse_par_l_agent_echoue_tout_de_suite()
    {
        _agent.ScriptRefusal = "User “alice” does not exist.";
        var id = await AddScriptAsync();
        await _coordinator.RunNowAsync(id, "alice");

        var run = await LastRunAsync(id);
        Assert.Equal((TaskRunStatus.Failed, _agent.ScriptRefusal, ""), (run.Status, run.Message, run.Pending));
        Assert.Null(run.Details);
    }

    [Fact]
    public async Task Un_script_que_l_agent_ne_connait_plus_est_interrompu()
    {
        var id = await AddScriptAsync();
        await _coordinator.RunNowAsync(id, "alice");
        _agent.ScriptRuns.Clear(); // l'agent a redémarré
        await _coordinator.TickAsync(DateTime.UtcNow, Zone);

        Assert.Equal(TaskRunStatus.Interrupted, (await LastRunAsync(id)).Status);
    }

    [Fact]
    public async Task Un_script_en_cours_s_arrete_avant_de_pouvoir_supprimer_la_tache()
    {
        var id = await AddScriptAsync();
        await Assert.ThrowsAsync<AgentException>(() => _coordinator.StopAsync(id, "alice"));
        await _coordinator.RunNowAsync(id, "alice");
        await Assert.ThrowsAsync<AgentException>(() => _coordinator.DeleteAsync(id));

        await _coordinator.StopAsync(id, "alice");
        Assert.Equal(_agent.ScriptStarts[0].Id, Assert.Single(_agent.ScriptCancels));
        _agent.EndScript(ScriptState.Stopped);
        await _coordinator.TickAsync(DateTime.UtcNow, Zone);

        Assert.Equal("copie", await _coordinator.DeleteAsync(id));
        Assert.Null(await _tasks.GetTaskAsync(id));
    }

    [Fact]
    public async Task Une_tache_de_script_garde_ses_reglages()
    {
        var id = await AddScriptAsync();
        var t = (await _tasks.GetTaskAsync(id))!;
        t.Script = "echo salut\n";
        t.RunAs = "root";
        t.TimeoutMinutes = 90;
        await _tasks.SaveTaskAsync(t);

        t = (await _tasks.GetTaskAsync(id))!;
        Assert.Equal(("echo salut\n", "root", 90), (t.Script, t.RunAs, t.TimeoutMinutes));
        Assert.Null(ScheduledTaskCoordinator.Validate(t));
        t.RunAs = "";
        Assert.NotNull(ScheduledTaskCoordinator.Validate(t));
    }

    [Fact]
    public async Task L_historique_est_limite_sans_perdre_l_execution_en_cours()
    {
        var id = await AddAsync();
        await _tasks.AddRunAsync(new ScheduledRunRow { TaskId = id, TaskName = "nuit", StartedUtc = DateTime.UtcNow });
        for (var i = 0; i < 5; i++)
            await _tasks.AddRunAsync(new ScheduledRunRow { TaskId = id, TaskName = "nuit", StartedUtc = DateTime.UtcNow, Status = TaskRunStatus.Skipped });
        await _tasks.PruneRunsAsync(id, keep: 2);

        var runs = await _tasks.GetRunsAsync(id);
        Assert.Equal(3, runs.Count);
        Assert.Contains(runs, r => r.Status == TaskRunStatus.Running);

        await _tasks.DeleteTaskAsync(id);
        Assert.Empty(await _tasks.GetRunsAsync(id));
    }
}
