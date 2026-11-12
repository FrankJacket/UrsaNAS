using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

/// <summary>Une agrégation de liens devient la sortie d'un pont Open vSwitch (et inversement).</summary>
public class BondOvsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ursanas-bondovs-" + Guid.NewGuid().ToString("N"));

    public BondOvsTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    string P(string p) => Path.Combine(root, p.TrimStart('/'));

    const string Eth0 = """{"ifname":"eth0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","master":"bond0","addr_info":[]}""";
    const string Eth1 = """{"ifname":"eth1","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","master":"bond0","addr_info":[]}""";
    const string BondFree = """{"ifname":"bond0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","linkinfo":{"info_kind":"bond"},"addr_info":[{"family":"inet","local":"192.168.1.50","prefixlen":24,"scope":"global"}]}""";
    const string BondPort = """{"ifname":"bond0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","master":"ovs-system","linkinfo":{"info_kind":"bond"},"addr_info":[]}""";
    const string Bridge = """{"ifname":"ovsbr0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","linkinfo":{"info_kind":"openvswitch"},"addr_info":[{"family":"inet","local":"192.168.1.50","prefixlen":24,"scope":"global"}]}""";
    const string Route4 = """[{"dst":"default","gateway":"192.168.1.1","dev":"%DEV%","metric":100}]""";
    const string Neighbor = """[{"dst":"192.168.1.1","dev":"%DEV%","lladdr":"aa:bb:cc:dd:ee:01","state":["REACHABLE"]}]""";

    static readonly NetEditRequest BondReq = new("bond0",
        new NetIpConfig("manual", "192.168.1.50", "255.255.255.0", "192.168.1.1", "auto", []),
        new NetIpConfig("auto", null, null, null, "auto", []));

    bool BridgeInPlace() => File.Exists(P("/etc/netplan/90-ursanas-ovs-ovsbr0.yaml")) || File.Exists(P("/etc/network/interfaces.d/ursanas-ovs-ovsbr0"));

    (NetworkChangeService Svc, FakeRunner Runner) Make()
    {
        var runner = new FakeRunner
        {
            Handler = c =>
            {
                var dev = BridgeInPlace() ? "ovsbr0" : "bond0";
                if (c.File == "ovs-vsctl")
                {
                    if (c.Args.Contains("list-br")) return new CommandResult(0, BridgeInPlace() ? "ovsbr0\n" : "", "");
                    if (c.Args.Contains("list-ports")) return new CommandResult(0, BridgeInPlace() ? "bond0\n" : "", "");
                    return new CommandResult(0, "", "");
                }
                if (c.File == "ip" && c.Args.Contains("neigh")) return new CommandResult(0, Neighbor.Replace("%DEV%", dev), "");
                if (c.File == "ip" && c.Args.Contains("route")) return new CommandResult(0, c.Args.Contains("-4") ? Route4.Replace("%DEV%", dev) : "[]", "");
                if (c.File == "ip" && c.Args.Contains("addr") && c.Args.Contains("show"))
                    return new CommandResult(0, "[" + Eth0 + "," + Eth1 + "," + (BridgeInPlace() ? BondPort + "," + Bridge : BondFree) + "]", "");
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

    [Fact]
    public async Task NetplanBondBecomesThePortOfANewBridgeAndComesBack()
    {
        Directory.CreateDirectory(P("/etc/netplan"));
        Directory.CreateDirectory(P("/usr/sbin"));
        File.WriteAllText(P("/usr/sbin/netplan"), "");
        File.WriteAllText(P("/etc/netplan/90-ursanas-bond-bond0.yaml"), NetplanConfig.GenerateBond(BondReq, ["eth0", "eth1"], "active-backup", "00:15:5d:00:aa:01"));
        var (svc, runner) = Make();

        await svc.UplinkAsync(new OvsUplinkRequest(true, "bond0"));
        var ovs = File.ReadAllText(P("/etc/netplan/90-ursanas-ovs-ovsbr0.yaml"));
        Assert.Contains("  bonds:\n    bond0:\n      dhcp4: false", ovs);
        Assert.Contains("interfaces: [bond0]", ovs);
        Assert.Contains("192.168.1.50/24", ovs);
        var bond = File.ReadAllText(P("/etc/netplan/90-ursanas-bond-bond0.yaml"));
        Assert.DoesNotContain("addresses", bond);
        Assert.Contains("mode: active-backup", bond);   // l'agrégation elle-même reste

        // tant que l'agrégation est un port, elle ne peut pas être retirée
        await Assert.ThrowsAsync<OperationException>(() => svc.BondAsync(new BondRequest(false, "bond0", [], "")));

        await svc.UplinkAsync(new OvsUplinkRequest(false, "ovsbr0"));
        Assert.False(File.Exists(P("/etc/netplan/90-ursanas-ovs-ovsbr0.yaml")));
        var back = File.ReadAllText(P("/etc/netplan/90-ursanas-bond0.yaml"));
        Assert.Contains("  bonds:\n    bond0:", back);
        Assert.Contains("192.168.1.50/24", back);
        Assert.Contains(runner.Calls, c => c.File == "ovs-vsctl" && c.Args.SequenceEqual(["--if-exists", "del-br", "ovsbr0"]));
    }

    [Fact]
    public async Task IfupdownBondKeepsItsOptionsWhileItIsAPort()
    {
        Directory.CreateDirectory(P("/etc/network/interfaces.d"));
        Directory.CreateDirectory(P("/sbin"));
        File.WriteAllText(P("/sbin/ifup"), "");
        File.WriteAllText(P("/etc/network/interfaces"), "source /etc/network/interfaces.d/*\n\nauto lo\niface lo inet loopback\n");
        File.WriteAllText(P("/etc/network/interfaces.d/ursanas-bond-bond0"), IfupdownConfig.GenerateBond(BondReq, ["eth0", "eth1"], "active-backup", "00:15:5d:00:aa:01"));
        var (svc, _) = Make();

        await svc.UplinkAsync(new OvsUplinkRequest(true, "bond0"));
        var ovs = File.ReadAllText(P("/etc/network/interfaces.d/ursanas-ovs-ovsbr0"));
        Assert.Contains("iface bond0 inet manual\n    bond-slaves eth0 eth1\n    bond-mode active-backup\n    bond-miimon 100\n    hwaddress ether 00:15:5d:00:aa:01\n    ovs_bridge ovsbr0", ovs);
        var bond = File.ReadAllText(P("/etc/network/interfaces.d/ursanas-bond-bond0"));
        Assert.DoesNotContain("iface bond0 inet", bond);
        Assert.Contains("iface eth0 inet manual\n    bond-master bond0", bond);   // les membres restent

        await svc.UplinkAsync(new OvsUplinkRequest(false, "ovsbr0"));
        var back = File.ReadAllText(P("/etc/network/interfaces.d/ursanas-bond0"));
        Assert.Contains("iface bond0 inet static\n    bond-slaves eth0 eth1\n    bond-mode active-backup", back);
        Assert.Contains("address 192.168.1.50/24", back);
    }
}
