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
