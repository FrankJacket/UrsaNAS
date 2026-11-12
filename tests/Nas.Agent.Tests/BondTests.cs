using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class BondFilesTests
{
    static readonly NetEditRequest Bond = new("bond0",
        new NetIpConfig("manual", "192.168.1.50", "255.255.255.0", "192.168.1.1", "auto", []),
        new NetIpConfig("auto", null, null, null, "auto", []));

    [Fact]
    public void NetplanBondCarriesTheAddressingAndTheCardsAreMembers()
    {
        var y = NetplanConfig.GenerateBond(Bond, ["eth0", "eth1"], "active-backup", "00:15:5d:00:aa:01");
        Assert.Contains("  ethernets:\n    eth0:\n      dhcp4: false", y);
        Assert.Contains("    eth1:\n      dhcp4: false", y);
        Assert.Contains("  bonds:\n    bond0:\n      interfaces: [eth0, eth1]\n      macaddress: \"00:15:5d:00:aa:01\"\n      parameters:\n        mode: active-backup\n        mii-monitor-interval: 100", y);
        Assert.Contains("addresses: [192.168.1.50/24]", y);
    }

    [Fact]
    public void IfupdownBondBlockCanBeFoundAndRemoved()
    {
        var block = IfupdownConfig.GenerateBond(Bond, ["eth0", "eth1"], "802.3ad", "00:15:5d:00:aa:01");
        Assert.Contains("auto bond0\niface bond0 inet static\n    bond-slaves eth0 eth1\n    bond-mode 802.3ad", block);
        Assert.Contains("hwaddress ether 00:15:5d:00:aa:01", block);
        Assert.Contains("iface eth1 inet manual\n    bond-master bond0", block);

        var text = "auto lo\niface lo inet loopback\n\n" + block + "\nauto other\n";
        Assert.Equal([("bond0", "eth0,eth1")], IfupdownConfig.FindBlocks(text, "bond"));
        var rest = IfupdownConfig.RemoveBlock(text, "bond", "bond0");
        Assert.DoesNotContain("bond", rest);
        Assert.Contains("auto lo", rest);
        Assert.Contains("auto other", rest);
    }

    [Fact]
    public void AnEmptiedNetplanDefinitionBecomesAnEmptyMapping()
    {
        var yaml = "network:\n  version: 2\n  ethernets:\n    eth0:\n      dhcp4: true\n    eth1:\n      match:\n        name: eth1\n";
        var stripped = NetplanConfig.StripIface(yaml, "eth0");
        Assert.Contains("    eth0: {}", stripped);
        Assert.Contains("      match:", stripped);   // eth1 n'est pas touchée
        Assert.Equal(stripped, NetplanConfig.StripIface(stripped, "eth0"));   // une définition vide se laisse retraiter
    }

    [Theory]
    [InlineData(true, "bond0", "eth0,eth1", "active-backup", true)]
    [InlineData(true, "bond0", "eth0", "active-backup", false)]
    [InlineData(true, "bond0", "eth0,eth0", "active-backup", false)]
    [InlineData(true, "bond0", "eth0,eth1", "round-robin", false)]
    [InlineData(true, "bond0", "eth0,bad name", "802.3ad", false)]
    [InlineData(false, "bond0", "", "", true)]
    [InlineData(false, "bad name", "", "", false)]
    public void RequestsAreValidated(bool enable, string name, string members, string mode, bool ok)
        => Assert.Equal(ok, BondRequest.Validate(new BondRequest(enable, name, members.Split(',', StringSplitOptions.RemoveEmptyEntries), mode)) is null);
}

public class BondTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ursanas-bond-" + Guid.NewGuid().ToString("N"));

    public BondTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    string P(string p) => Path.Combine(root, p.TrimStart('/'));

    const string Eth0 = """{"ifname":"eth0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","addr_info":[{"family":"inet","local":"192.168.1.50","prefixlen":24,"scope":"global"}]}""";
    const string Eth1 = """{"ifname":"eth1","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:02","addr_info":[]}""";
    const string Eth0Member = """{"ifname":"eth0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","master":"bond0","addr_info":[]}""";
    const string Eth1Member = """{"ifname":"eth1","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","master":"bond0","addr_info":[]}""";
    const string BondIf = """{"ifname":"bond0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","linkinfo":{"info_kind":"bond"},"addr_info":[{"family":"inet","local":"192.168.1.50","prefixlen":24,"scope":"global"}]}""";
    const string Route4 = """[{"dst":"default","gateway":"192.168.1.1","dev":"%DEV%","metric":100}]""";
    const string Neighbor = """[{"dst":"192.168.1.1","dev":"%DEV%","lladdr":"aa:bb:cc:dd:ee:01","state":["REACHABLE"]}]""";

    sealed class Sim { public bool MembersEnrolled = true; }

    bool BondInPlace() => File.Exists(P("/etc/netplan/90-ursanas-bond-bond0.yaml")) || File.Exists(P("/etc/network/interfaces.d/ursanas-bond-bond0"))
        || (File.Exists(P("/etc/network/interfaces")) && File.ReadAllText(P("/etc/network/interfaces")).Contains("# ursanas-bond bond0"));

    (NetworkChangeService Svc, FakeRunner Runner) Make(Sim? sim = null)
    {
        sim ??= new Sim();
        var runner = new FakeRunner
        {
            Handler = c =>
            {
                var dev = BondInPlace() ? "bond0" : "eth0";
                if (c.File == "ip" && c.Args.Contains("neigh")) return new CommandResult(0, Neighbor.Replace("%DEV%", dev), "");
                if (c.File == "ip" && c.Args.Contains("route")) return new CommandResult(0, c.Args.Contains("-4") ? Route4.Replace("%DEV%", dev) : "[]", "");
                if (c.File == "ip" && c.Args.Contains("addr") && c.Args.Contains("show"))
                    return new CommandResult(0, "[" + (BondInPlace() ? (sim.MembersEnrolled ? Eth0Member + "," + Eth1Member : Eth0 + "," + Eth1) + "," + BondIf : Eth0 + "," + Eth1) + "]", "");
                return new CommandResult(0, "[]", "");
            },
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Network:Root"] = root, ["Network:PendingFile"] = P("/state/pending.json"), ["Network:SysClassNet"] = P("/sys/class/net"),
            ["Network:ResolvConf:0"] = P("/none"), ["Network:VerifySeconds"] = "1", ["Network:VerifyPollMs"] = "50",
        }).Build();
        return (new NetworkChangeService(runner, new NetworkService(runner, cfg), cfg, NullLogger<NetworkChangeService>.Instance), runner);
    }

    const string NetplanOriginal = "network:\n  version: 2\n  ethernets:\n    eth0:\n      addresses: [192.168.1.50/24]\n      routes:\n        - to: 0.0.0.0/0\n          via: 192.168.1.1\n    eth1:\n      dhcp4: true\n";

    void NetplanSetup()
    {
        Directory.CreateDirectory(P("/etc/netplan"));
        Directory.CreateDirectory(P("/usr/sbin"));
        File.WriteAllText(P("/usr/sbin/netplan"), "");
        File.WriteAllText(P("/etc/netplan/50-cloud-init.yaml"), NetplanOriginal);
    }

    void IfupdownSetup(string interfaces, bool ifenslave = true)
    {
        Directory.CreateDirectory(P("/etc/network/interfaces.d"));
        Directory.CreateDirectory(P("/sbin"));
        File.WriteAllText(P("/sbin/ifup"), "");
        if (ifenslave) File.WriteAllText(P("/sbin/ifenslave"), "");
        File.WriteAllText(P("/etc/network/interfaces"), interfaces);
    }

    static readonly BondRequest Create = new(true, "", ["eth0", "eth1"], "active-backup");

    static bool Called(FakeRunner r, string file, params string[] args) => r.Calls.Any(c => c.File == file && c.Args.SequenceEqual(args));

    [Fact]
    public async Task NetplanCardsAreAggregatedAndTheAddressingMoves()
    {
        NetplanSetup();
        var (svc, runner) = Make();
        await svc.BondAsync(Create);
        var ours = File.ReadAllText(P("/etc/netplan/90-ursanas-bond-bond0.yaml"));
        Assert.Contains("interfaces: [eth0, eth1]", ours);
        Assert.Contains("macaddress: \"00:15:5d:00:aa:01\"", ours);   // l'adresse MAC de eth0, qui portait la passerelle
        Assert.Contains("192.168.1.50/24", ours);
        var original = File.ReadAllText(P("/etc/netplan/50-cloud-init.yaml"));
        Assert.DoesNotContain("addresses", original);
        Assert.DoesNotContain("dhcp4: true", original);
        Assert.Contains("eth0: {}", original);
        Assert.True(Called(runner, "netplan", "apply"));
        Assert.Null(svc.GetPending());
    }

    [Fact]
    public async Task MembersThatAreNotEnrolledMeanRollback()
    {
        NetplanSetup();
        var (svc, runner) = Make(new Sim { MembersEnrolled = false });
        var e = await Assert.ThrowsAsync<OperationException>(() => svc.BondAsync(Create));
        Assert.Contains("is not a member of bond0", e.Message);
        Assert.Equal(NetplanOriginal, File.ReadAllText(P("/etc/netplan/50-cloud-init.yaml")));
        Assert.False(File.Exists(P("/etc/netplan/90-ursanas-bond-bond0.yaml")));
        Assert.True(Called(runner, "ip", "link", "delete", "bond0"));
    }

    [Fact]
    public async Task OnlyFreeEthernetCardsCanBeAggregated()
    {
        NetplanSetup();
        var (svc, _) = Make();
        await Assert.ThrowsAsync<OperationException>(() => svc.BondAsync(new BondRequest(true, "", ["eth0", "eth9"], "active-backup")));
        await Assert.ThrowsAsync<OperationException>(() => svc.BondAsync(new BondRequest(true, "", ["eth0"], "active-backup")));
    }

    [Fact]
    public async Task NetplanAggregationIsRemovedAndTheFirstCardGetsTheAddressingBack()
    {
        NetplanSetup();
        var (svc, runner) = Make();
        await svc.BondAsync(Create);
        runner.Calls.Clear();
        await svc.BondAsync(new BondRequest(false, "bond0", [], ""));
        Assert.False(File.Exists(P("/etc/netplan/90-ursanas-bond-bond0.yaml")));
        var back = File.ReadAllText(P("/etc/netplan/90-ursanas-eth0.yaml"));
        Assert.Contains("192.168.1.50/24", back);
        Assert.True(Called(runner, "ip", "link", "delete", "bond0"));
        Assert.Null(svc.GetPending());
    }

    [Fact]
    public async Task IfupdownAggregationUsesItsOwnFileAndIfenslave()
    {
        IfupdownSetup("source /etc/network/interfaces.d/*\n\nauto lo\niface lo inet loopback\n\nauto eth0\niface eth0 inet static\n    address 192.168.1.50/24\n    gateway 192.168.1.1\n\nauto eth1\niface eth1 inet dhcp\n");
        var (svc, runner) = Make();
        await svc.BondAsync(Create);
        var main = File.ReadAllText(P("/etc/network/interfaces"));
        Assert.DoesNotContain("iface eth0", main);
        Assert.DoesNotContain("iface eth1", main);
        var ours = File.ReadAllText(P("/etc/network/interfaces.d/ursanas-bond-bond0"));
        Assert.Contains("# ursanas-bond bond0 eth0,eth1", ours);
        Assert.Contains("bond-slaves eth0 eth1", ours);
        Assert.Contains("iface bond0 inet static", ours);
        Assert.True(Called(runner, "ifup", "bond0"));
        Assert.DoesNotContain(runner.Calls, c => c.File == "ifup" && c.Args[0] is "eth0" or "eth1");   // ifenslave enrôle les membres
        Assert.DoesNotContain(runner.Calls, c => c.File == "env");   // ifenslave était déjà là
    }

    [Fact]
    public async Task IfenslaveIsInstalledWhenMissing()
    {
        IfupdownSetup("source /etc/network/interfaces.d/*\n\nauto eth0\niface eth0 inet dhcp\n", ifenslave: false);
        var (svc, runner) = Make();
        await svc.BondAsync(Create);
        var install = Assert.Single(runner.Calls, c => c.File == "env");
        Assert.Contains("ifenslave", install.Args);
    }

    [Fact]
    public async Task IfupdownFailureRestoresTheFiles()
    {
        const string original = "source /etc/network/interfaces.d/*\n\nauto eth0\niface eth0 inet static\n    address 192.168.1.50/24\n    gateway 192.168.1.1\n";
        IfupdownSetup(original);
        var (svc, runner) = Make(new Sim { MembersEnrolled = false });
        await Assert.ThrowsAsync<OperationException>(() => svc.BondAsync(Create));
        Assert.Equal(original, File.ReadAllText(P("/etc/network/interfaces")));
        Assert.False(File.Exists(P("/etc/network/interfaces.d/ursanas-bond-bond0")));
        Assert.True(Called(runner, "ip", "link", "delete", "bond0"));
    }

    [Fact]
    public async Task IfupdownBlockInTheMainFileIsRemovedWithTheAggregation()
    {
        IfupdownSetup("auto lo\niface lo inet loopback\n\nauto eth0\niface eth0 inet static\n    address 192.168.1.50/24\n    gateway 192.168.1.1\n");
        var (svc, runner) = Make();
        await svc.BondAsync(Create);
        Assert.Contains("# ursanas-bond bond0 eth0,eth1", File.ReadAllText(P("/etc/network/interfaces")));

        runner.Calls.Clear();
        await svc.BondAsync(new BondRequest(false, "bond0", [], ""));
        var main = File.ReadAllText(P("/etc/network/interfaces"));
        Assert.DoesNotContain("bond", main);
        Assert.Contains("iface eth0 inet static", main);
        Assert.True(Called(runner, "ifdown", "--force", "bond0"));
    }
}
