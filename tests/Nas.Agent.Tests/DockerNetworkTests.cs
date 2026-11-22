using Microsoft.Extensions.Configuration;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class DockerNetworkTests
{
    static DockerService Svc(FakeRunner r) => new(r, new ConfigurationBuilder().Build());

    [Fact]
    public void Un_reseau_bridge_simple_ne_passe_que_le_nom()
    {
        Assert.Equal(["network", "create", "--driver", "bridge", "media"], DockerService.NetworkCreateArgs(new CreateNetworkRequest("media")));
    }

    [Fact]
    public void Les_options_du_reseau_sont_transmises_a_docker()
    {
        var args = DockerService.NetworkCreateArgs(new CreateNetworkRequest("lan", "macvlan", "192.168.1.0/24", "192.168.1.1", "192.168.1.128/25", true, "eth0", "bridge"));
        Assert.Equal(["network", "create", "--driver", "macvlan", "--subnet", "192.168.1.0/24", "--gateway", "192.168.1.1", "--ip-range", "192.168.1.128/25",
            "--ipv6", "-o", "parent=eth0", "-o", "macvlan_mode=bridge", "lan"], args);
        Assert.Contains("ipvlan_mode=l3", DockerService.NetworkCreateArgs(new CreateNetworkRequest("v", "ipvlan", "10.0.0.0/24", Parent: "eth1", Mode: "l3")));
    }

    [Theory]
    [InlineData("bridge", null, null, null, null, true)]
    [InlineData("bridge", "172.20.0.0/16", "172.20.0.1", "172.20.10.0/24", null, true)]
    [InlineData("bridge", "fd00::/64", "fd00::1", null, null, true)]
    [InlineData("bridge", "172.20.0.0", null, null, null, false)]      // le sous-réseau veut un masque
    [InlineData("bridge", "172.20.0.0/16", "pas-une-ip", null, null, false)]
    [InlineData("bridge", null, "172.20.0.1", null, null, false)]      // une passerelle sans sous-réseau
    [InlineData("bridge", "172.20.0.0/16", null, "--rm", null, false)]
    [InlineData("macvlan", "192.168.1.0/24", null, null, "eth0", true)]
    [InlineData("macvlan", null, null, null, "eth0", false)]           // macvlan veut un sous-réseau
    [InlineData("macvlan", "192.168.1.0/24", null, null, null, false)] // et une interface
    [InlineData("macvlan", "192.168.1.0/24", null, null, "-x", false)]
    [InlineData("overlay", null, null, null, null, false)]
    public void Valide_la_creation_d_un_reseau(string driver, string? subnet, string? gateway, string? range, string? parent, bool ok)
    {
        var mode = driver == "macvlan" ? "bridge" : null;
        Assert.Equal(ok, Rules.ValidateCreateNetwork(new CreateNetworkRequest("net1", driver, subnet, gateway, range, false, parent, mode)) is null);
    }

    [Fact]
    public void Chaque_pilote_a_ses_modes()
    {
        Assert.Null(Rules.ValidateCreateNetwork(new CreateNetworkRequest("n", "ipvlan", "10.0.0.0/24", Parent: "eth0", Mode: "l2")));
        Assert.NotNull(Rules.ValidateCreateNetwork(new CreateNetworkRequest("n", "ipvlan", "10.0.0.0/24", Parent: "eth0", Mode: "bridge")));
        Assert.NotNull(Rules.ValidateCreateNetwork(new CreateNetworkRequest("n", "bridge", Mode: "l2")));
        Assert.NotNull(Rules.ValidateCreateNetwork(new CreateNetworkRequest("-bad")));
    }

    [Fact]
    public async Task Un_reseau_invalide_n_atteint_pas_docker()
    {
        var runner = new FakeRunner();
        await Assert.ThrowsAsync<OperationException>(() => Svc(runner).CreateNetworkAsync(new CreateNetworkRequest("n", "bridge", "oups")));
        Assert.Empty(runner.Calls);
    }

    const string Inspect = """
        [
          {"Id":"aaaaaaaaaaaa1111","Name":"media","Driver":"bridge","EnableIPv6":false,
           "IPAM":{"Config":[{"Subnet":"172.20.0.0/16","Gateway":"172.20.0.1"}]},
           "Containers":{"c1":{"Name":"zeta"},"c2":{"Name":"alpha"}},"Options":{}},
          {"Id":"bbbbbbbbbbbb2222","Name":"lan","Driver":"macvlan","EnableIPv6":true,
           "IPAM":{"Config":[{"Subnet":"192.168.1.0/24","IPRange":"192.168.1.128/25"},{"Subnet":"fd00::/64"}]},
           "Containers":{},"Options":{"parent":"eth0","macvlan_mode":"vepa"}},
          {"Id":"cccccccccccc3333","Name":"host","Driver":"host","IPAM":{"Config":[]},"Containers":{}}
        ]
        """;

    [Fact]
    public void Les_details_viennent_de_docker_network_inspect()
    {
        var basic = new[]
        {
            new DockerNetwork("aaaaaaaaaaaa", "media", "bridge", "local"), new DockerNetwork("bbbbbbbbbbbb", "lan", "macvlan", "local"),
            new DockerNetwork("cccccccccccc", "host", "host", "local"), new DockerNetwork("dddddddddddd", "absent", "bridge", "local"),
        };
        var r = DockerParser.MergeNetworkDetails(basic, Inspect);
        Assert.Equal(("172.20.0.0/16", "172.20.0.1", false), (r[0].Subnet, r[0].Gateway, r[0].Ipv6));
        Assert.Equal(["alpha", "zeta"], r[0].Containers!);
        Assert.Equal(("192.168.1.0/24, fd00::/64", "192.168.1.128/25", true, "eth0", "vepa"), (r[1].Subnet, r[1].IpRange, r[1].Ipv6, r[1].Parent, r[1].Mode));
        Assert.Null(r[2].Subnet);
        Assert.Empty(r[2].Containers!);
        Assert.Equal(basic[3], r[3]); // absent de la réponse : inchangé
    }

    [Fact]
    public async Task La_liste_garde_les_reseaux_si_inspect_echoue()
    {
        var runner = new FakeRunner
        {
            Handler = c => c.Args[1] == "ls"
                ? new CommandResult(0, "{\"ID\":\"n1\",\"Name\":\"bridge\",\"Driver\":\"bridge\",\"Scope\":\"local\"}\n", "")
                : new CommandResult(1, "", "boom"),
        };
        var list = await Svc(runner).NetworksAsync();
        Assert.Equal("bridge", Assert.Single(list).Name);
    }

    [Theory]
    [InlineData("start", new[] { "start", "docker.service" })]
    [InlineData("stop", new[] { "stop", "docker.socket", "docker.service" })]
    [InlineData("restart", new[] { "restart", "docker.service" })]
    public async Task Le_service_Docker_se_demarre_s_arrete_et_redemarre(string action, string[] expected)
    {
        var runner = new FakeRunner();
        await Svc(runner).ServiceAsync(action);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("systemctl", call.File);
        Assert.Equal(expected, call.Args);
    }

    [Fact]
    public async Task Une_action_inconnue_ou_un_echec_de_systemctl_est_signale()
    {
        var runner = new FakeRunner { Handler = _ => new CommandResult(5, "", "Unit docker.service not found.") };
        await Assert.ThrowsAsync<OperationException>(() => Svc(runner).ServiceAsync("disable"));
        var e = await Assert.ThrowsAsync<OperationException>(() => Svc(runner).ServiceAsync("restart"));
        Assert.Contains("not found", e.Message);
    }
}

public class DockerContainerNetworkTests
{
    [Fact]
    public void Les_adresses_IP_viennent_de_docker_inspect()
    {
        var list = new[]
        {
            new ContainerInfo("aaaaaaaaaaaa", "ng", "nginx", "running", "Up", "0.0.0.0:8090->80/tcp", null),
            new ContainerInfo("bbbbbbbbbbbb", "macng", "nginx", "running", "Up", "", null),
            new ContainerInfo("cccccccccccc", "absent", "nginx", "exited", "Exited", "", null),
        };
        var r = DockerParser.MergeContainerNetworks(list, """
            [
              {"Id":"aaaaaaaaaaaa1111","NetworkSettings":{"Networks":{"bridge":{"IPAddress":"172.17.0.2","Gateway":"172.17.0.1","MacAddress":"02:42:ac:11:00:02","GlobalIPv6Address":"fd00::2"}}}},
              {"Id":"bbbbbbbbbbbb2222","NetworkSettings":{"Networks":{"lan":{"IPAddress":"192.168.1.50","Gateway":"192.168.1.1","MacAddress":"02:42:c0:a8:01:32"},"b":{"IPAddress":""}}}}
            ]
            """);
        var ng = Assert.Single(r[0].Networks!);
        Assert.Equal(("bridge", "172.17.0.2", "fd00::2", "172.17.0.1"), (ng.Network, ng.Ip, ng.Ipv6, ng.Gateway));
        Assert.Equal(["b", "lan"], r[1].Networks!.Select(n => n.Network));
        Assert.Null(r[1].Networks![0].Ip);          // arrêté ou sans adresse : vide
        Assert.Equal("192.168.1.50", r[1].Networks![1].Ip);
        Assert.Null(r[2].Networks);                  // absent de la réponse : inchangé
    }
}

public class StackValidationTests
{
    static DockerService Svc(FakeRunner r) => new(r, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Docker:StacksDir"] = Path.Combine(Path.GetTempPath(), "ursanas-none-" + Guid.NewGuid().ToString("N")) }).Build());

    static FakeRunner Docker(Func<FakeRunner.Call, CommandResult> compose) => new()
    {
        Handler = c => c.Args[0] switch
        {
            "version" => new CommandResult(0, "27.0\n", ""),
            "compose" when c.Args[1] == "version" => new CommandResult(0, "2.29\n", ""),
            "compose" => compose(c),
            _ => c.Args[0] == "image" && c.Args[1] == "inspect" ? new CommandResult(1, "", "No such image") : new CommandResult(0, "", ""),
        },
    };

    [Fact]
    public async Task Un_fichier_valide_est_accepte_et_les_images_absentes_sont_signalees()
    {
        var r = Docker(c => c.Args.Contains("--images") ? new CommandResult(0, "nginx:latest\nghcr.io/x/y:1\n--rm\n", "") : new CommandResult(0, "", ""));
        var v = await Svc(r).ValidateStackAsync("blog", "services:\n  app:\n    image: nginx:latest\n");
        Assert.True(v.Checked);
        Assert.Null(v.Error);
        Assert.Equal(["nginx:latest", "ghcr.io/x/y:1"], v.MissingImages); // « --rm » n'est jamais passé à docker
        Assert.DoesNotContain(r.Calls, c => c.Args.Contains("--rm"));
    }

    [Fact]
    public async Task Un_fichier_invalide_renvoie_le_message_de_compose_sans_le_chemin_temporaire()
    {
        var r = Docker(c => new CommandResult(1, "", $"validating {c.Args[Array.IndexOf(c.Args, "-f") + 1]}: services.app Additional property imagee is not allowed"));
        var v = await Svc(r).ValidateStackAsync(null, "services:\n  app:\n    imagee: x\n");
        Assert.Contains("imagee is not allowed", v.Error);
        Assert.Contains("validating compose.yml:", v.Error);
        Assert.DoesNotContain("ursanas-compose-", v.Error);
        Assert.DoesNotContain(r.Calls, c => c.Args.Contains("--images")); // inutile quand le fichier est refusé
    }

    [Fact]
    public async Task Sans_Docker_ou_sans_Compose_rien_n_est_verifie_et_rien_n_est_bloque()
    {
        var down = new FakeRunner { Handler = _ => new CommandResult(1, "", "Cannot connect") };
        var v = await Svc(down).ValidateStackAsync("a", "services:\n  app:\n    image: x\n");
        Assert.False(v.Checked);
        Assert.Null(v.Error);
    }

    [Fact]
    public async Task Un_fichier_vide_ou_un_nom_invalide_est_refuse_avant_docker()
    {
        var r = Docker(_ => new CommandResult(0, "", ""));
        Assert.NotNull((await Svc(r).ValidateStackAsync("a", "  ")).Error);
        await Assert.ThrowsAsync<OperationException>(() => Svc(r).ValidateStackAsync("-x", "services: {}"));
        Assert.Empty(r.Calls);
    }
}

public class AuditCsvTests
{
    static Nas.Web.Data.AuditRow Row(string actor, string action, string? detail, int minute = 0) =>
        new() { Actor = actor, Action = action, Detail = detail, At = new DateTime(2026, 11, 22, 14, minute, 5, DateTimeKind.Unspecified) };

    [Fact]
    public void Le_CSV_a_un_en_tete_des_dates_ISO_en_UTC_et_des_fins_de_ligne_CRLF()
    {
        var csv = Nas.Web.Data.AuditCsv.Build([Row("alice", "Share created", "docs"), Row("system", "Alert", null, 1)]);
        Assert.Equal("Date (UTC),Author,Action,Detail\r\n" +
                     "2026-11-22T14:00:05Z,alice,Share created,docs\r\n" +
                     "2026-11-22T14:01:05Z,system,Alert,\r\n", csv);
    }

    [Fact]
    public void Les_virgules_guillemets_et_retours_a_la_ligne_sont_proteges()
    {
        var csv = Nas.Web.Data.AuditCsv.Build([Row("a", "x", "un, deux \"trois\"\nquatre")]);
        Assert.Contains("\"un, deux \"\"trois\"\"\nquatre\"", csv);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2", "'-2")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("normal", "normal")]
    public void Un_texte_ne_peut_pas_devenir_une_formule_de_tableur(string detail, string expected) =>
        Assert.EndsWith("," + expected + "\r\n", Nas.Web.Data.AuditCsv.Build([Row("a", "x", detail)]));

    [Fact]
    public void Le_fichier_est_en_UTF8_avec_marque_d_ordre_des_octets()
    {
        var bytes = Nas.Web.Data.AuditCsv.Bytes([Row("é", "x", null)]);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Contains("é", System.Text.Encoding.UTF8.GetString(bytes[3..]));
    }
}
