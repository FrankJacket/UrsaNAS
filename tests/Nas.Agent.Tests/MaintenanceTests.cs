using Nas.Agent.Services;
using Nas.Contracts;
using Nas.Web;

namespace Nas.Agent.Tests;

public class TrimParserTests
{
    const string Status = """
          pool: tank
         state: ONLINE
          scan: scrub repaired 0B in 00:00:00 with 0 errors on Thu Nov 26 16:07:33 2026
        config:

        	NAME        STATE     READ WRITE CKSUM
        	tank        ONLINE       0     0     0
        	  raidz1-0  DEGRADED     0     0     0
        	    sdb     ONLINE       0     0     0  (untrimmed)
        	    sdc     DEGRADED     0     0     0  (42% trimmed, started at Fri Nov 27 02:00:01 2026)
        	    sdd     FAULTED      0     0     0  (untrimmed)
        	    sde     ONLINE       0     0     0  (trim unsupported)
        	logs
        	  sdf       ONLINE       0     0     0  (100% trimmed, completed at Fri Nov 27 02:00:09 2026)
        	cache
        	  sdg       ONLINE       0     0     0  (untrimmed)
        	spares
        	  sdh       AVAIL    (untrimmed)

        errors: No known data errors
        """;

    [Fact]
    public void Lit_l_etat_des_disques_de_donnees_et_de_journal_seulement()
    {
        var devices = ZfsParser.ParseTrim(Status);

        Assert.Equal(
            [("sdb", true, TrimState.Untrimmed), ("sdc", true, TrimState.Running), ("sdd", false, TrimState.Untrimmed),
             ("sde", true, TrimState.Unsupported), ("sdf", true, TrimState.Completed)],
            devices.Select(d => (d.Name, d.Online, d.State)));
        Assert.Equal([null, 42, null, null, 100], devices.Select(d => d.Percent));
    }

    [Fact]
    public void Un_trim_suspendu_est_reconnu()
    {
        var text = "config:\n\n\tNAME   STATE\n\ttank   ONLINE\n\t  sda  ONLINE  0 0 0  (7% trimmed, suspended, started at Fri Nov 27 02:00:01 2026)\n\nerrors: none\n";
        Assert.Equal(TrimState.Suspended, Assert.Single(ZfsParser.ParseTrim(text)).State);
    }

    static TrimOutcome Outcome(params (bool Online, TrimState State)[] devices) =>
        ScheduledTaskCoordinator.ReadTrim(new PoolTrimStatus("tank", devices.Select((d, i) => new TrimDevice("sd" + i, d.Online, d.State)).ToList()));

    [Fact]
    public void L_etat_du_pool_ne_compte_que_les_disques_en_service_qui_acceptent_le_trim()
    {
        Assert.Equal(TrimOutcome.Running, Outcome((true, TrimState.Completed), (true, TrimState.Running)));
        Assert.Equal(TrimOutcome.Running, Outcome((true, TrimState.Suspended)));
        Assert.Equal(TrimOutcome.Completed, Outcome((true, TrimState.Completed), (true, TrimState.Unsupported), (false, TrimState.Untrimmed)));
        Assert.Equal(TrimOutcome.Cancelled, Outcome((true, TrimState.Completed), (true, TrimState.Untrimmed)));
        Assert.Equal(TrimOutcome.Unknown, Outcome((true, TrimState.Unsupported)));
        Assert.Equal(TrimOutcome.Unknown, Outcome());
    }
}

/// <summary>JSON built from the keys of smartctl 7.4; real self-test results could not be observed on virtual disks.</summary>
public class SelfTestParserTests
{
    // Concatenated: the JSON's closing braces would clash with the ones of an interpolated string.
    static string Ata(int value, bool supported = true) =>
        """{"ata_smart_data":{"capabilities":{"self_tests_supported":""" + (supported ? "true" : "false")
        + """},"self_test":{"status":{"value":""" + value + ""","string":"detail"}}}}""";

    [Theory]
    [InlineData(0x00, SelfTestState.Passed, null)]
    [InlineData(0x10, SelfTestState.Aborted, null)]
    [InlineData(0x20, SelfTestState.Aborted, null)]
    [InlineData(0x30, SelfTestState.Failed, null)]
    [InlineData(0x79, SelfTestState.Failed, null)]
    [InlineData(0xF9, SelfTestState.Running, 90)]
    public void Disque_ata(int value, SelfTestState state, int? remaining)
    {
        var read = SmartParser.ParseSelfTest(Ata(value));
        Assert.Equal((state, remaining), (read.State, read.RemainingPercent));
    }

    [Theory]
    [InlineData(0, SelfTestState.None)] // never tested: the status alone would read as a success
    [InlineData(1, SelfTestState.Passed)]
    public void Un_journal_ata_vide_veut_dire_aucun_autotest(int entries, SelfTestState state)
    {
        var json = Ata(0).TrimEnd()[..^1] + ""","ata_smart_self_test_log":{"standard":{"count":""" + entries + "}}}";
        Assert.Equal(state, SmartParser.ParseSelfTest(json).State);
    }

    [Fact]
    public void Disque_ata_sans_autotest() => Assert.Equal(SelfTestState.Unsupported, SmartParser.ParseSelfTest(Ata(0, supported: false)).State);

    [Theory]
    [InlineData("""{"nvme_self_test_log":{"current_self_test_operation":{"value":0}}}""", SelfTestState.None)]
    [InlineData("""{"nvme_self_test_log":{"current_self_test_operation":{"value":1},"current_self_test_completion_percent":30}}""", SelfTestState.Running)]
    [InlineData("""{"nvme_self_test_log":{"current_self_test_operation":{"value":0},"table":[{"self_test_result":{"value":0}},{"self_test_result":{"value":7}}]}}""", SelfTestState.Passed)]
    [InlineData("""{"nvme_self_test_log":{"current_self_test_operation":{"value":0},"table":[{"self_test_result":{"value":7}}]}}""", SelfTestState.Failed)]
    [InlineData("""{"nvme_self_test_log":{"current_self_test_operation":{"value":0},"table":[{"self_test_result":{"value":2}}]}}""", SelfTestState.Aborted)]
    [InlineData("""{"nvme_smart_health_information_log":{},"smart_status":{"passed":true},"device":{"protocol":"NVMe"}}""", SelfTestState.Unsupported)]
    [InlineData("""{"scsi_self_test_0":{"result":{"value":15}}}""", SelfTestState.Running)]
    [InlineData("""{"scsi_self_test_0":{"result":{"value":0}},"scsi_self_test_1":{"result":{"value":7}}}""", SelfTestState.Passed)]
    [InlineData("""{"scsi_self_test_0":{"result":{"value":4}}}""", SelfTestState.Failed)]
    [InlineData("""{"scsi_self_test_0":{"result":{"value":1}}}""", SelfTestState.Aborted)]
    [InlineData("""{"device":{"protocol":"SCSI"},"smart_status":{"passed":true}}""", SelfTestState.None)]
    [InlineData("""{"device":{"protocol":"SCSI"},"scsi_product":"Virtual Disk","smartctl":{"exit_status":2}}""", SelfTestState.Unsupported)]
    [InlineData("pas du JSON", SelfTestState.Unsupported)]
    public void Disques_nvme_et_scsi(string json, SelfTestState state) => Assert.Equal(state, SmartParser.ParseSelfTest(json).State);

    [Fact]
    public void L_avancement_nvme_donne_le_reste() =>
        Assert.Equal(70, SmartParser.ParseSelfTest("""{"nvme_self_test_log":{"current_self_test_operation":{"value":2},"current_self_test_completion_percent":30}}""").RemainingPercent);
}

public class SelfTestServiceTests
{
    sealed class Disks(params (string Name, string? Serial)[] disks) : IDiskProvider
    {
        public Task<IReadOnlyList<DiskInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DiskInfo>>(
            disks.Select(d => new DiskInfo(d.Name, "/dev/" + d.Name, 1000, "Model", d.Serial, true, [])).ToList());
    }

    const string Idle = """{"ata_smart_data":{"capabilities":{"self_tests_supported":true},"self_test":{"status":{"value":0}}}}""";
    const string Busy = """{"ata_smart_data":{"capabilities":{"self_tests_supported":true},"self_test":{"status":{"value":249}}}}""";

    static (SmartService, FakeRunner) Service(string status, CommandResult? start = null, params (string, string?)[] disks)
    {
        var run = new FakeRunner { Handler = c => c.Args.Contains("-t") ? start ?? new CommandResult(0, "{}", "") : new CommandResult(0, status, "") };
        return (new SmartService(run, new Disks(disks.Length > 0 ? disks : [("sda", "A1"), ("sdb", "B2"), ("sdc", null)])), run);
    }

    [Fact]
    public async Task Seuls_les_disques_demandes_sont_interroges()
    {
        var (service, run) = Service(Idle);

        Assert.Equal(["A1", "B2"], (await service.ListSelfTestsAsync([])).Select(d => d.Serial)); // no serial number: left out
        run.Calls.Clear();
        Assert.Equal("/dev/sdb", Assert.Single(await service.ListSelfTestsAsync(["B2"])).Disk);
        Assert.Equal(["-j", "-a", "/dev/sdb"], Assert.Single(run.Calls).Args);
    }

    [Fact]
    public async Task Le_test_demarre_sur_le_disque_qui_porte_le_numero_de_serie()
    {
        var (service, run) = Service(Idle);
        await service.StartSelfTestAsync(new SelfTestRequest("B2", SelfTestType.Long));
        Assert.Equal(["-j", "-t", "long", "/dev/sdb"], run.Calls[^1].Args);
    }

    [Theory]
    [InlineData("Z9", SelfTestType.Short)] // unknown disk
    [InlineData("A1", "offline")] // unknown test type
    public async Task Une_demande_invalide_ne_lance_rien(string serial, string type)
    {
        var (service, run) = Service(Idle);
        await Assert.ThrowsAsync<OperationException>(() => service.StartSelfTestAsync(new SelfTestRequest(serial, type)));
        Assert.DoesNotContain(run.Calls, c => c.Args.Contains("-t"));
    }

    [Theory]
    [InlineData(Busy)]
    [InlineData("""{"device":{"protocol":"SCSI"}}""")]
    public async Task Un_disque_occupe_ou_sans_autotest_n_est_pas_teste(string status)
    {
        var (service, run) = Service(status);
        await Assert.ThrowsAsync<OperationException>(() => service.StartSelfTestAsync(new SelfTestRequest("A1", SelfTestType.Short)));
        Assert.DoesNotContain(run.Calls, c => c.Args.Contains("-t"));
    }

    [Fact]
    public async Task Deux_disques_au_meme_numero_de_serie_sont_refuses()
    {
        var (service, _) = Service(Idle, null, ("sda", "X"), ("sdb", "X"));
        await Assert.ThrowsAsync<OperationException>(() => service.StartSelfTestAsync(new SelfTestRequest("X", SelfTestType.Short)));
    }

    [Fact]
    public async Task Le_refus_de_smartctl_est_rapporte()
    {
        var (service, _) = Service(Idle, new CommandResult(4, """{"smartctl":{"messages":[{"string":"Command refused"}]}}""", ""));
        var e = await Assert.ThrowsAsync<OperationException>(() => service.StartSelfTestAsync(new SelfTestRequest("A1", SelfTestType.Short)));
        Assert.Contains("Command refused", e.Message);
    }

    [Fact]
    public void Les_avertissements_de_sante_n_annulent_pas_le_lancement() =>
        Assert.Null(SmartParser.StartError(new CommandResult(64, "{}", "")));
}
