using Microsoft.Extensions.Configuration;
using Nas.Agent.Services;
using Nas.Contracts;
using Nas.Web.Alerting;
using Nas.Web.Data;
using Xunit;

namespace Nas.Agent.Tests;

public class SmartParserTests
{
    [Fact]
    public void Disque_ATA_en_bonne_sante()
    {
        var json = """
        {"smart_status":{"passed":true},"temperature":{"current":34},"power_on_time":{"hours":1200},
         "ata_smart_attributes":{"table":[{"id":5,"raw":{"value":0}},{"id":197,"raw":{"value":0}}]}}
        """;
        var s = SmartParser.Parse("/dev/sda", json);
        Assert.Equal("ok", s.Status);
        Assert.Equal(34, s.TemperatureC);
        Assert.Equal(1200, s.PowerOnHours);
        Assert.Null(s.Detail);
    }

    [Fact]
    public void Disque_qui_annonce_sa_defaillance()
    {
        var s = SmartParser.Parse("/dev/sda", """{"smart_status":{"passed":false}}""");
        Assert.Equal("failing", s.Status);
        Assert.Contains("failure", s.Detail);
    }

    [Fact]
    public void Secteurs_realloues_ou_en_attente_donnent_un_avertissement()
    {
        var json = """{"smart_status":{"passed":true},"ata_smart_attributes":{"table":[{"id":5,"raw":{"value":8}},{"id":197,"raw":{"value":2}},{"id":198,"raw":{"value":1}}]}}""";
        var s = SmartParser.Parse("/dev/sda", json);
        Assert.Equal("warning", s.Status);
        Assert.Equal(8, s.ReallocatedSectors);
        Assert.Equal(3, s.PendingSectors);
        Assert.Contains("reallocated", s.Detail);
    }

    [Fact]
    public void Disque_virtuel_sans_smart_est_non_supporte()
    {
        var s = SmartParser.Parse("/dev/sdb", """{"smartctl":{"messages":[{"string":"SMART support is: Unavailable - device lacks SMART capability."}]}}""");
        Assert.Equal("unsupported", s.Status);
        Assert.Contains("Unavailable", s.Detail);
    }

    [Fact]
    public void Nvme_avec_avertissement_critique()
    {
        var s = SmartParser.Parse("/dev/nvme0n1", """{"smart_status":{"passed":true},"nvme_smart_health_information_log":{"critical_warning":4,"media_errors":0,"temperature":40}}""");
        Assert.Equal("failing", s.Status);
        Assert.Equal(40, s.TemperatureC);
    }

    [Fact]
    public void Sortie_illisible_est_non_supportee() =>
        Assert.Equal("unsupported", SmartParser.Parse("/dev/sda", "pas du json").Status);
}

public class AlertRulesTests
{
    static PoolDevice Dev(string name, string state, int depth, string r = "0", string w = "0", string c = "0") => new(name, state, r, w, c, depth);

    static PoolInfo Pool(string health = "ONLINE", string errors = "No known data errors", long alloc = 1, long size = 100, params PoolDevice[] devices) =>
        new("tank", health, size, alloc, size - alloc, "", errors, devices);

    static List<Candidate> Eval(params PoolInfo[] pools) => AlertRules.Evaluate(new HealthSnapshot(true, pools, []));

    [Fact]
    public void Un_pool_sain_ne_declenche_rien() =>
        Assert.Empty(Eval(Pool(devices: [Dev("tank", "ONLINE", 0), Dev("sdb", "ONLINE", 2)])));

    [Fact]
    public void Pool_degrade_nomme_le_disque_en_cause()
    {
        var c = Assert.Single(Eval(Pool("DEGRADED", devices: [Dev("tank", "DEGRADED", 0), Dev("raidz1-0", "DEGRADED", 1), Dev("sdd", "OFFLINE", 2)])));
        Assert.Equal("pool:tank:health", c.Code);
        Assert.Equal(AlertRules.Critical, c.Severity);
        Assert.Contains("sdd (OFFLINE)", c.Detail);
        Assert.Contains("raidz1-0 (DEGRADED)", c.Detail);
    }

    [Fact]
    public void Erreurs_de_donnees_et_compteurs_par_disque()
    {
        var found = Eval(Pool(errors: "Permanent errors have been detected in the following files", devices: [Dev("sdb", "ONLINE", 2, c: "12")]));
        Assert.Contains(found, c => c.Code == "pool:tank:errors" && c.Severity == AlertRules.Critical);
        Assert.Contains(found, c => c.Code == "pool:tank:dev:sdb" && c.Severity == AlertRules.Warning);
    }

    [Theory]
    [InlineData(89, null)]
    [InlineData(90, AlertRules.Warning)]
    [InlineData(95, AlertRules.Critical)]
    public void Pool_presque_plein(long percent, string? severity)
    {
        var found = Eval(Pool(alloc: percent));
        Assert.Equal(severity, found.SingleOrDefault(c => c.Code == "pool:tank:space")?.Severity);
    }

    [Fact]
    public void Smart_et_agent_muet()
    {
        var smart = new[]
        {
            new SmartInfo("/dev/sda", "failing", null, null, null, null, "the disk reports its own failure"),
            new SmartInfo("/dev/sdb", "warning", null, null, 8, null, "8 reallocated sectors"),
            new SmartInfo("/dev/sdc", "ok", null, null, null, null, null),
            new SmartInfo("/dev/sdd", "unsupported", null, null, null, null, "indisponible"),
        };
        var found = AlertRules.Evaluate(new HealthSnapshot(true, [], smart));
        Assert.Equal(["smart:/dev/sda", "smart:/dev/sdb"], found.Select(c => c.Code));
        Assert.Equal(AlertRules.Critical, found[0].Severity);

        var down = AlertRules.Evaluate(new HealthSnapshot(false, null, null));
        Assert.Equal("agent", Assert.Single(down).Code);
    }
}

public class RemoteAlertTests
{
    static RemoteStorage R(string name, bool mounted) => new(name, "smb", "srv", "share", null, null, null, false, "/srv/remote/" + name, mounted, 0, 0);

    [Fact]
    public void Une_connexion_distante_non_montee_donne_une_alerte_et_une_connexion_montee_aucune()
    {
        var found = AlertRules.Evaluate(new HealthSnapshot(true, [], [], null, [R("papa", false), R("one", true)]));
        var alert = Assert.Single(found);
        Assert.Equal("remote:papa:down", alert.Code);
        Assert.Equal(AlertRules.Warning, alert.Severity);

        Assert.Empty(AlertRules.Evaluate(new HealthSnapshot(true, [], [], null, [R("papa", true)])));
        Assert.Empty(AlertRules.Evaluate(new HealthSnapshot(true, [], [], null, null))); // on ne sait pas : pas d'alerte
    }

    [Fact]
    public void L_alerte_se_ferme_au_retour_du_montage_mais_pas_quand_l_etat_est_inconnu()
    {
        var row = new AlertRow { Id = 1, Code = "remote:papa:down", Severity = AlertRules.Warning, Title = "t" };
        var (_, resolveOk) = AlertEngine.Step([], [row], new HashSet<string>(), []);
        Assert.Single(resolveOk);
        var (_, resolveUnknown) = AlertEngine.Step([], [row], new HashSet<string>(), ["remote:"]);
        Assert.Empty(resolveUnknown);
    }
}

public class AlertEngineTests
{
    static Candidate C(string code) => new(code, AlertRules.Critical, code, "");
    static AlertRow Row(string code, long id = 1) => new() { Id = id, Code = code, Severity = AlertRules.Critical, Title = code };
    static readonly IReadOnlySet<string> None = new HashSet<string>();

    [Fact]
    public void Une_alerte_n_est_ouverte_qu_au_deuxieme_constat()
    {
        Assert.Empty(AlertEngine.Step([C("a")], [], None, []).Raise);
        Assert.Single(AlertEngine.Step([C("a")], [], new HashSet<string> { "a" }, []).Raise);
    }

    [Fact]
    public void Controle_immediat_ouvre_tout_de_suite() =>
        Assert.Single(AlertEngine.Step([C("a")], [], None, [], immediate: true).Raise);

    [Fact]
    public void Une_alerte_deja_ouverte_n_est_pas_rouverte()
    {
        var (raise, resolve) = AlertEngine.Step([C("a")], [Row("a")], new HashSet<string> { "a" }, []);
        Assert.Empty(raise);
        Assert.Empty(resolve);
    }

    [Fact]
    public void Une_alerte_disparue_est_fermee()
    {
        var (_, resolve) = AlertEngine.Step([], [Row("a")], None, []);
        Assert.Equal("a", Assert.Single(resolve).Code);
    }

    [Fact]
    public void Ce_qu_on_n_a_pas_pu_observer_n_est_jamais_ferme()
    {
        var active = new[] { Row("pool:tank:health", 1), Row("smart:/dev/sda", 2), Row("agent", 3) };
        var (_, resolve) = AlertEngine.Step([C("agent")], active, None, ["pool:", "smart:"]);
        Assert.Empty(resolve);
    }
}

public class AlertServiceTests : IDisposable
{
    sealed class FakeMail : IMailSender
    {
        public List<(string Subject, string Body)> Sent { get; } = [];
        public bool Fail { get; set; }
        public Task SendAsync(MailSettings s, string subject, string body, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("SMTP injoignable");
            Sent.Add((subject, body));
            return Task.CompletedTask;
        }
    }

    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-alert-" + Guid.NewGuid().ToString("N"));
    readonly Repo _repo;
    readonly FakeMail _mail = new();
    readonly AlertState _state = new();
    readonly AlertService _svc;

    static readonly MailSettings Mail = new(true, "smtp.example.com", 587, "starttls", "u", "p", "nas@example.com", "moi@example.com");

    static HealthSnapshot Sick() => new(true, [new PoolInfo("tank", "DEGRADED", 100, 1, 99, "", "No known data errors",
        [new PoolDevice("sdd", "OFFLINE", "0", "0", "0", 2)])], []);
    static HealthSnapshot Healthy() => new(true, [new PoolInfo("tank", "ONLINE", 100, 1, 99, "", "No known data errors", [])], []);

    public AlertServiceTests()
    {
        var db = new Db(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dir }).Build());
        db.Init();
        _repo = new Repo(db);
        _svc = new AlertService(_repo, _mail, _state);
    }

    [Fact]
    public async Task Alerte_ouverte_au_deuxieme_constat_avec_un_courriel_puis_fermee_avec_un_courriel()
    {
        await _repo.SaveMailSettingsAsync(Mail);

        await _svc.ProcessAsync(Sick());
        Assert.Empty(await _repo.GetActiveAlertsAsync());
        Assert.Empty(_mail.Sent);

        await _svc.ProcessAsync(Sick());
        var alert = Assert.Single(await _repo.GetActiveAlertsAsync());
        Assert.Equal("pool:tank:health", alert.Code);
        Assert.Equal((1, 1), (_state.Count, _state.CriticalCount));
        var m = Assert.Single(_mail.Sent);
        Assert.Contains("CRITICAL", m.Subject);
        Assert.Contains("sdd (OFFLINE)", m.Body);

        await _svc.ProcessAsync(Sick()); // toujours malade : pas de nouvel e-mail
        Assert.Single(_mail.Sent);

        await _svc.ProcessAsync(Healthy());
        Assert.Empty(await _repo.GetActiveAlertsAsync());
        Assert.Equal(0, _state.Count);
        Assert.Equal(2, _mail.Sent.Count);
        Assert.StartsWith("[UrsaNAS] Recovered", _mail.Sent[1].Subject);
        Assert.NotNull((await _repo.GetAlertHistoryAsync()).Single().ResolvedAt);
    }

    [Fact]
    public async Task Agent_muet_ne_ferme_pas_les_alertes_de_pool()
    {
        await _svc.ProcessAsync(Sick(), immediate: true);
        await _svc.ProcessAsync(new HealthSnapshot(false, null, null), immediate: true);

        var codes = (await _repo.GetActiveAlertsAsync()).Select(a => a.Code).Order().ToList();
        Assert.Equal(["agent", "pool:tank:health"], codes);
    }

    [Fact]
    public async Task Sans_courriel_active_rien_n_est_envoye()
    {
        await _svc.ProcessAsync(Sick(), immediate: true);
        Assert.Empty(_mail.Sent);
        Assert.Single(await _repo.GetActiveAlertsAsync());
    }

    [Fact]
    public async Task Echec_d_envoi_est_journalise_et_n_empeche_pas_l_alerte()
    {
        await _repo.SaveMailSettingsAsync(Mail);
        _mail.Fail = true;

        await _svc.ProcessAsync(Sick(), immediate: true);

        Assert.Single(await _repo.GetActiveAlertsAsync());
        Assert.Contains(await _repo.GetAuditAsync(), a => a.Action == "Email not sent" && a.Detail!.Contains("SMTP injoignable"));
    }

    [Fact]
    public async Task Reglages_courriel_le_mot_de_passe_vide_conserve_l_ancien()
    {
        Assert.False((await _repo.GetMailSettingsAsync()).Enabled);

        await _repo.SaveMailSettingsAsync(Mail);
        await _repo.SaveMailSettingsAsync(Mail with { Host = "autre.example.com", Password = "" });

        var s = await _repo.GetMailSettingsAsync();
        Assert.Equal("autre.example.com", s.Host);
        Assert.Equal("p", s.Password);
        Assert.Equal(587, s.Port);
        Assert.True(s.Enabled);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }
}

public class MailRulesTests
{
    static readonly MailSettings Ok = new(true, "smtp.example.com", 587, "starttls", "", "", "nas@example.com", "a@example.com, b@example.com");

    [Fact]
    public void Reglages_valides_et_desactives()
    {
        Assert.Null(MailRules.Validate(Ok));
        Assert.Null(MailRules.Validate(MailSettings.Default)); // désactivé : non vérifié
    }

    [Theory]
    [InlineData("host", "")]
    [InlineData("host", "a b")]
    [InlineData("port", "0")]
    [InlineData("port", "70000")]
    [InlineData("security", "tls13")]
    [InlineData("from", "pas-une-adresse")]
    [InlineData("from", "a@b.c\nBcc: x@y.z")]
    [InlineData("to", "")]
    [InlineData("to", "a@example.com, nimporte")]
    [InlineData("to", "a@e.co,b@e.co,c@e.co,d@e.co,e@e.co,f@e.co")]
    public void Reglages_invalides(string field, string value)
    {
        var s = field switch
        {
            "host" => Ok with { Host = value },
            "port" => Ok with { Port = int.Parse(value) },
            "security" => Ok with { Security = value },
            "from" => Ok with { From = value },
            _ => Ok with { To = value },
        };
        Assert.NotNull(MailRules.Validate(s));
    }
}
