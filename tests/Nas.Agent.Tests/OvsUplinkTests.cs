using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class OvsUplinkFilesTests
{
    static readonly NetEditRequest Bridge = new("ovsbr0",
        new NetIpConfig("manual", "192.168.1.50", "255.255.255.0", "192.168.1.1", "auto", []),
        new NetIpConfig("auto", null, null, null, "auto", []));

    [Fact]
    public void NetplanBridgeCarriesTheAddressingAndTheCardIsOnlyAPort()
    {
        var y = NetplanConfig.GenerateOvsBridge(Bridge, "eth0", "00:15:5d:00:aa:01");
        Assert.Contains("  ethernets:\n    eth0:\n      dhcp4: false", y);
        Assert.Contains("  bridges:\n    ovsbr0:\n      openvswitch: {}\n      interfaces: [eth0]\n      macaddress: \"00:15:5d:00:aa:01\"", y);
        Assert.Contains("addresses: [192.168.1.50/24]", y);
        Assert.Contains("via: 192.168.1.1", y);
    }

    [Fact]
    public void IfupdownBridgeBlockCanBeFoundAndRemoved()
    {
        var block = IfupdownConfig.GenerateOvsBridge(Bridge, "eth0", "00:15:5d:00:aa:01");
        Assert.Contains("allow-ovs ovsbr0", block);
        Assert.Contains("ovs_type OVSBridge\n    ovs_ports eth0", block);
        Assert.Contains("ovs_extra set bridge ${IFACE} other-config:hwaddr=00:15:5d:00:aa:01", block);
        Assert.Contains("allow-ovsbr0 eth0\niface eth0 inet manual\n    ovs_bridge ovsbr0\n    ovs_type OVSPort", block);

        var text = "auto lo\niface lo inet loopback\n\n" + block + "\nauto other\n";
        Assert.Equal([("ovsbr0", "eth0")], IfupdownConfig.FindOvs(text));
        var rest = IfupdownConfig.RemoveOvs(text, "ovsbr0");
        Assert.DoesNotContain("ovs", rest);
        Assert.Contains("auto lo", rest);
        Assert.Contains("auto other", rest);
        Assert.Empty(IfupdownConfig.FindOvs(rest));
    }

    [Fact]
    public void OvsBridgesAreRecognisedAndTheDatapathIsHidden()
    {
        const string ip = """
            [{"ifname":"ovs-system","flags":["UP"],"mtu":1500,"link_type":"ether","linkinfo":{"info_kind":"openvswitch"},"addr_info":[]},
             {"ifname":"ovsbr0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","linkinfo":{"info_kind":"openvswitch"},"addr_info":[]},
             {"ifname":"eth0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","master":"ovs-system","addr_info":[]}]
            """;
        var list = NetworkService.Parse(ip, "[]", "[]", [], (_, _) => null);
        Assert.Equal(["eth0", "ovsbr0"], list.Select(i => i.Name));
        Assert.Equal("ovs", list.Single(i => i.Name == "ovsbr0").Kind);
        Assert.Equal("ethernet", list.Single(i => i.Name == "eth0").Kind);
    }

    [Fact]
    public void AddressingIsCopiedFromTheInterface()
    {
        var card = new NetIface("eth0", "ethernet", true, "00:15:5d:00:aa:01", 1500, null, null,
            [new NetAddress("192.168.1.50", 24, "255.255.255.0", false, "global")], [], "192.168.1.1", null, ["1.1.1.1"], false, null, 0, 0);
        var r = NetworkChangeService.Addressing(card, "ovsbr0");
        Assert.Equal("ovsbr0", r.Iface);
        Assert.Equal(("manual", "192.168.1.50", "255.255.255.0", "192.168.1.1", "manual"), (r.Ipv4.Mode, r.Ipv4.Address, r.Ipv4.Netmask, r.Ipv4.Gateway, r.Ipv4.DnsMode));
        Assert.Equal(["1.1.1.1"], r.Ipv4.Dns);
        Assert.Equal("auto", r.Ipv6.Mode);
        Assert.Null(NetRules.Validate(r));
    }
}

public class OvsUplinkTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ursanas-ovs-" + Guid.NewGuid().ToString("N"));

    public OvsUplinkTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    string P(string p) => Path.Combine(root, p.TrimStart('/'));

    const string Mac = "00:15:5d:00:aa:01";
    const string Card = """{"ifname":"eth0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","addr_info":[{"family":"inet","local":"192.168.1.50","prefixlen":24,"scope":"global"}]}""";
    const string CardAsPort = """{"ifname":"eth0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","master":"ovs-system","addr_info":[]}""";
    const string Bridge = """{"ifname":"ovsbr0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","linkinfo":{"info_kind":"openvswitch"},"addr_info":[{"family":"inet","local":"192.168.1.50","prefixlen":24,"scope":"global"}]}""";
    const string Route4 = """[{"dst":"default","gateway":"192.168.1.1","dev":"%DEV%","metric":100}]""";
    const string Neighbor = """[{"dst":"192.168.1.1","dev":"%DEV%","lladdr":"aa:bb:cc:dd:ee:01","state":["REACHABLE"]}]""";

    sealed class Sim { public bool OvsRunning = true, PortOk = true; }

    bool BridgeInPlace() => File.Exists(P("/etc/netplan/90-ursanas-ovs-ovsbr0.yaml")) || File.Exists(P("/etc/network/interfaces.d/ursanas-ovs-ovsbr0"))
        || (File.Exists(P("/etc/network/interfaces")) && File.ReadAllText(P("/etc/network/interfaces")).Contains("# ursanas-ovs ovsbr0"));

    (NetworkChangeService Svc, FakeRunner Runner) Make(Sim? sim = null)
    {
        sim ??= new Sim();
        var runner = new FakeRunner
        {
            Handler = c =>
            {
                var dev = BridgeInPlace() ? "ovsbr0" : "eth0";
                if (c.File == "ovs-vsctl")
                {
                    if (!sim.OvsRunning) return new CommandResult(1, "", "not running");
                    if (c.Args.Contains("list-br")) return new CommandResult(0, BridgeInPlace() ? "ovsbr0\n" : "", "");
                    if (c.Args.Contains("list-ports")) return new CommandResult(0, BridgeInPlace() && sim.PortOk ? "eth0\n" : "", "");
                    return new CommandResult(0, "", "");
                }
                if (c.File == "ip" && c.Args.Contains("neigh")) return new CommandResult(0, Neighbor.Replace("%DEV%", dev), "");
                if (c.File == "ip" && c.Args.Contains("route")) return new CommandResult(0, c.Args.Contains("-4") ? Route4.Replace("%DEV%", dev) : "[]", "");
                if (c.File == "ip" && c.Args.Contains("addr") && c.Args.Contains("show"))
                    return new CommandResult(0, "[" + (BridgeInPlace() ? CardAsPort + "," + Bridge : Card) + "]", "");
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

    const string NetplanOriginal = "network:\n  version: 2\n  ethernets:\n    eth0:\n      addresses: [192.168.1.50/24]\n      routes:\n        - to: 0.0.0.0/0\n          via: 192.168.1.1\n";

    void NetplanSetup()
    {
        Directory.CreateDirectory(P("/etc/netplan"));
        Directory.CreateDirectory(P("/usr/sbin"));
        File.WriteAllText(P("/usr/sbin/netplan"), "");
        File.WriteAllText(P("/etc/netplan/50-cloud-init.yaml"), NetplanOriginal);
    }

    void IfupdownSetup(string interfaces)
    {
        Directory.CreateDirectory(P("/etc/network/interfaces.d"));
        Directory.CreateDirectory(P("/sbin"));
        File.WriteAllText(P("/sbin/ifup"), "");
        File.WriteAllText(P("/etc/network/interfaces"), interfaces);
    }

    static bool Called(FakeRunner r, string file, params string[] args) => r.Calls.Any(c => c.File == file && c.Args.SequenceEqual(args));

    [Fact]
    public async Task NetplanCardBecomesTheUplinkOfANewBridge()
    {
        NetplanSetup();
        var (svc, runner) = Make();
        await svc.UplinkAsync(new OvsUplinkRequest(true, "eth0"));
        var ours = File.ReadAllText(P("/etc/netplan/90-ursanas-ovs-ovsbr0.yaml"));
        Assert.Contains("openvswitch: {}", ours);
        Assert.Contains("interfaces: [eth0]", ours);
        Assert.Contains($"macaddress: \"{Mac}\"", ours);
        Assert.Contains("192.168.1.50/24", ours);
        var original = File.ReadAllText(P("/etc/netplan/50-cloud-init.yaml"));
        Assert.DoesNotContain("addresses", original);   // l'adressage a quitté la carte
        Assert.DoesNotContain("via:", original);
        Assert.True(Called(runner, "netplan", "apply"));
        Assert.Null(svc.GetPending());
        Assert.False(File.Exists(P("/state/pending.json")));
    }

    [Fact]
    public async Task ACardThatDoesNotJoinTheBridgeIsRolledBack()
    {
        NetplanSetup();
        var (svc, runner) = Make(new Sim { PortOk = false });
        var e = await Assert.ThrowsAsync<OperationException>(() => svc.UplinkAsync(new OvsUplinkRequest(true, "eth0")));
        Assert.Contains("not a port of the bridge ovsbr0", e.Message);
        Assert.Equal(NetplanOriginal, File.ReadAllText(P("/etc/netplan/50-cloud-init.yaml")));
        Assert.False(File.Exists(P("/etc/netplan/90-ursanas-ovs-ovsbr0.yaml")));
        Assert.True(Called(runner, "ovs-vsctl", "--if-exists", "del-br", "ovsbr0"));
    }

    [Fact]
    public async Task WithoutOpenVSwitchRunningNothingIsTouched()
    {
        NetplanSetup();
        var (svc, runner) = Make(new Sim { OvsRunning = false });
        var e = await Assert.ThrowsAsync<OperationException>(() => svc.UplinkAsync(new OvsUplinkRequest(true, "eth0")));
        Assert.Contains("not enabled", e.Message);
        Assert.Equal(NetplanOriginal, File.ReadAllText(P("/etc/netplan/50-cloud-init.yaml")));
        Assert.DoesNotContain(runner.Calls, c => c.File == "netplan");
    }

    [Fact]
    public async Task OnlyAFreeEthernetCardCanBeAnUplink()
    {
        NetplanSetup();
        var (svc, _) = Make();
        await Assert.ThrowsAsync<OperationException>(() => svc.UplinkAsync(new OvsUplinkRequest(true, "eth9")));
        await Assert.ThrowsAsync<OperationException>(() => svc.UplinkAsync(new OvsUplinkRequest(true, "bad name")));
    }

    [Fact]
    public async Task NetplanBridgeIsRemovedAndTheCardGetsItsAddressingBack()
    {
        NetplanSetup();
        var (svc, runner) = Make();
        await svc.UplinkAsync(new OvsUplinkRequest(true, "eth0"));
        runner.Calls.Clear();
        await svc.UplinkAsync(new OvsUplinkRequest(false, "ovsbr0"));
        Assert.False(File.Exists(P("/etc/netplan/90-ursanas-ovs-ovsbr0.yaml")));
        var back = File.ReadAllText(P("/etc/netplan/90-ursanas-eth0.yaml"));
        Assert.Contains("192.168.1.50/24", back);
        Assert.Contains("via: 192.168.1.1", back);
        Assert.True(Called(runner, "ovs-vsctl", "--if-exists", "del-br", "ovsbr0"));
        Assert.Null(svc.GetPending());
    }

    [Fact]
    public async Task ABridgeUrsaNasDidNotCreateIsLeftAlone()
    {
        NetplanSetup();
        File.WriteAllText(P("/etc/netplan/60-mine.yaml"), "network:\n  version: 2\n  bridges:\n    ovsbr0:\n      openvswitch: {}\n");
        var (svc, runner) = Make();
        // la maquette dit « pont en place » dès qu'un fichier d'UrsaNAS existe : ici il n'y en a pas, donc le pont n'apparaît pas dans l'instantané
        var e = await Assert.ThrowsAsync<OperationException>(() => svc.UplinkAsync(new OvsUplinkRequest(false, "ovsbr0")));
        Assert.DoesNotContain(runner.Calls, c => c.File == "netplan");
        Assert.NotEmpty(e.Message);
    }

    [Fact]
    public async Task IfupdownCardBecomesTheUplinkInItsOwnFile()
    {
        IfupdownSetup("source /etc/network/interfaces.d/*\n\nauto lo\niface lo inet loopback\n\nauto eth0\niface eth0 inet static\n    address 192.168.1.50/24\n    gateway 192.168.1.1\n");
        var (svc, runner) = Make();
        await svc.UplinkAsync(new OvsUplinkRequest(true, "eth0"));
        var main = File.ReadAllText(P("/etc/network/interfaces"));
        Assert.DoesNotContain("iface eth0", main);
        var ours = File.ReadAllText(P("/etc/network/interfaces.d/ursanas-ovs-ovsbr0"));
        Assert.Contains("# ursanas-ovs ovsbr0 eth0", ours);
        Assert.Contains("iface ovsbr0 inet static", ours);
        Assert.Contains("ovs_ports eth0", ours);
        Assert.Contains("iface eth0 inet manual", ours);
        // l'ancien bloc est coupé avant d'être réécrit, puis le pont monte avant sa carte
        var order = runner.Calls.Where(c => c.File is "ifdown" or "ifup").Select(c => c.File + " " + c.Args[^1]).ToList();
        Assert.Equal("ifdown eth0", order[0]);
        Assert.True(order.IndexOf("ifup ovsbr0") < order.LastIndexOf("ifup eth0"));
    }

    [Fact]
    public async Task IfupdownFailureRestoresTheFiles()
    {
        const string original = "source /etc/network/interfaces.d/*\n\nauto eth0\niface eth0 inet static\n    address 192.168.1.50/24\n    gateway 192.168.1.1\n";
        IfupdownSetup(original);
        var (svc, runner) = Make(new Sim { PortOk = false });
        await Assert.ThrowsAsync<OperationException>(() => svc.UplinkAsync(new OvsUplinkRequest(true, "eth0")));
        Assert.Equal(original, File.ReadAllText(P("/etc/network/interfaces")));
        Assert.False(File.Exists(P("/etc/network/interfaces.d/ursanas-ovs-ovsbr0")));
        Assert.True(Called(runner, "ovs-vsctl", "--if-exists", "del-br", "ovsbr0"));
    }

    [Fact]
    public async Task IfupdownBlockInTheMainFileIsRemovedWithTheBridge()
    {
        IfupdownSetup("auto lo\niface lo inet loopback\n\nauto eth0\niface eth0 inet static\n    address 192.168.1.50/24\n    gateway 192.168.1.1\n");
        var (svc, runner) = Make();
        await svc.UplinkAsync(new OvsUplinkRequest(true, "eth0"));
        Assert.Contains("# ursanas-ovs ovsbr0 eth0", File.ReadAllText(P("/etc/network/interfaces")));

        runner.Calls.Clear();
        await svc.UplinkAsync(new OvsUplinkRequest(false, "ovsbr0"));
        var main = File.ReadAllText(P("/etc/network/interfaces"));
        Assert.DoesNotContain("ovs", main);
        Assert.Contains("iface eth0 inet static", main);   // pas de « source » : le bloc de la carte revient dans le fichier principal
        Assert.DoesNotContain("inet manual", main);
        Assert.True(Called(runner, "ifdown", "--force", "ovsbr0"));
    }
}
