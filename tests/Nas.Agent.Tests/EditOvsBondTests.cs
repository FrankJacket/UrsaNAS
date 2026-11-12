using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

/// <summary>Changer l'adressage IPv4/IPv6 d'un pont Open vSwitch ou d'une agrégation de liens créés par UrsaNAS.</summary>
public class EditOvsBondTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ursanas-editovsbond-" + Guid.NewGuid().ToString("N"));

    public EditOvsBondTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    string P(string p) => Path.Combine(root, p.TrimStart('/'));

    const string Mac = "00:15:5d:00:aa:01";
    const string Route4 = """[{"dst":"default","gateway":"192.168.1.1","dev":"%DEV%","metric":100}]""";
    const string Neighbor = """[{"dst":"192.168.1.1","dev":"%DEV%","lladdr":"aa:bb:cc:dd:ee:01","state":["REACHABLE"]}]""";

    static NetEditRequest Req(string iface, string address) => new(iface,
        new NetIpConfig("manual", address, "255.255.255.0", "192.168.1.1", "auto", []),
        new NetIpConfig("auto", null, null, null, "auto", []));

    static string Card(string name, string master)
        => $$"""{"ifname":"{{name}}","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"{{Mac}}","master":"{{master}}","addr_info":[]}""";

    static string Device(string name, string kind, string? master, string address)
        => $$"""{"ifname":"{{name}}","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"{{Mac}}",{{(master is null ? "" : $"\"master\":\"{master}\",")}}"linkinfo":{"info_kind":"{{kind}}"},"addr_info":[{"family":"inet","local":"{{address}}","prefixlen":24,"scope":"global"}]}""";

    /// <summary>Une adresse est « appliquée » dès qu'un fichier de configuration la contient.</summary>
    string Current() => Directory.GetFiles(P("/etc"), "*", SearchOption.AllDirectories).Any(f => File.ReadAllText(f).Contains("192.168.1.60")) ? "192.168.1.60" : "192.168.1.50";

    (NetworkChangeService Svc, FakeRunner Runner) Make(string dev, Func<string, string> devices, string ports)
    {
        var runner = new FakeRunner
        {
            Handler = c =>
            {
                if (c.File == "ovs-vsctl")
                {
                    if (c.Args.Contains("list-br")) return new CommandResult(0, "ovsbr0\n", "");
                    if (c.Args.Contains("list-ports")) return new CommandResult(0, ports, "");
                    return new CommandResult(0, "", "");
                }
                if (c.File == "ip" && c.Args.Contains("neigh")) return new CommandResult(0, Neighbor.Replace("%DEV%", dev), "");
                if (c.File == "ip" && c.Args.Contains("route")) return new CommandResult(0, c.Args.Contains("-4") ? Route4.Replace("%DEV%", dev) : "[]", "");
                if (c.File == "ip" && c.Args.Contains("addr") && c.Args.Contains("show")) return new CommandResult(0, "[" + devices(Current()) + "]", "");
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

    void Netplan()
    {
        Directory.CreateDirectory(P("/etc/netplan"));
        Directory.CreateDirectory(P("/usr/sbin"));
        File.WriteAllText(P("/usr/sbin/netplan"), "");
    }

    void Ifupdown()
    {
        Directory.CreateDirectory(P("/etc/network/interfaces.d"));
        Directory.CreateDirectory(P("/sbin"));
        File.WriteAllText(P("/sbin/ifup"), "");
        File.WriteAllText(P("/etc/network/interfaces"), "source /etc/network/interfaces.d/*\n\nauto lo\niface lo inet loopback\n");
    }

    string Bridges(string a) => Card("eth0", "ovs-system") + "," + Device("ovsbr0", "openvswitch", null, a);
    string Bonds(string a) => Card("eth0", "bond0") + "," + Card("eth1", "bond0") + "," + Device("bond0", "bond", null, a);

    [Fact]
    public async Task NetplanOvsBridgeAddressCanBeChanged()
    {
        Netplan();
        File.WriteAllText(P("/etc/netplan/90-ursanas-ovs-ovsbr0.yaml"), NetplanConfig.GenerateOvsBridge(Req("ovsbr0", "192.168.1.50"), "eth0", Mac));
        var (svc, _) = Make("ovsbr0", Bridges, "eth0\n");

        await svc.EditAsync(Req("ovsbr0", "192.168.1.60"));

        var text = File.ReadAllText(P("/etc/netplan/90-ursanas-ovs-ovsbr0.yaml"));
        Assert.Contains("192.168.1.60/24", text);
        Assert.DoesNotContain("192.168.1.50", text);
        Assert.Contains("openvswitch: {}", text);
        Assert.Contains("interfaces: [eth0]", text);
        Assert.Contains($"macaddress: \"{Mac}\"", text);
        Assert.Null(svc.GetPending());
    }

    [Fact]
    public async Task IfupdownOvsBridgeAddressCanBeChanged()
    {
        Ifupdown();
        File.WriteAllText(P("/etc/network/interfaces.d/ursanas-ovs-ovsbr0"), IfupdownConfig.GenerateOvsBridge(Req("ovsbr0", "192.168.1.50"), "eth0", Mac));
        var (svc, runner) = Make("ovsbr0", Bridges, "eth0\n");

        await svc.EditAsync(Req("ovsbr0", "192.168.1.60"));

        var text = File.ReadAllText(P("/etc/network/interfaces.d/ursanas-ovs-ovsbr0"));
        Assert.Contains("address 192.168.1.60/24", text);
        Assert.DoesNotContain("192.168.1.50", text);
        Assert.Contains("ovs_ports eth0", text);
        Assert.Single(text.Split('\n'), l => l == "# end ursanas-ovs ovsbr0");
        Assert.Contains(runner.Calls, c => c.File == "ifdown" && c.Args.SequenceEqual(["--force", "ovsbr0"]));
        Assert.Contains(runner.Calls, c => c.File == "ifup" && c.Args.SequenceEqual(["ovsbr0"]));
    }

    [Fact]
    public async Task FailedOvsEditGoesBack()
    {
        Ifupdown();
        var old = IfupdownConfig.GenerateOvsBridge(Req("ovsbr0", "192.168.1.50"), "eth0", Mac);
        File.WriteAllText(P("/etc/network/interfaces.d/ursanas-ovs-ovsbr0"), old);
        var (svc, _) = Make("ovsbr0", _ => Bridges("192.168.1.50"), "eth0\n");   // l'adresse demandée n'apparaît jamais

        await Assert.ThrowsAsync<OperationException>(() => svc.EditAsync(Req("ovsbr0", "192.168.1.60")));

        Assert.Equal(old, File.ReadAllText(P("/etc/network/interfaces.d/ursanas-ovs-ovsbr0")));
    }

    [Fact]
    public async Task NetplanBondAddressCanBeChanged()
    {
        Netplan();
        File.WriteAllText(P("/etc/netplan/90-ursanas-bond-bond0.yaml"), NetplanConfig.GenerateBond(Req("bond0", "192.168.1.50"), ["eth0", "eth1"], "802.3ad", Mac));
        var (svc, _) = Make("bond0", Bonds, "");

        await svc.EditAsync(Req("bond0", "192.168.1.60"));

        var text = File.ReadAllText(P("/etc/netplan/90-ursanas-bond-bond0.yaml"));
        Assert.Contains("192.168.1.60/24", text);
        Assert.DoesNotContain("192.168.1.50", text);
        Assert.Contains("interfaces: [eth0, eth1]", text);
        Assert.Contains("mode: 802.3ad", text);
    }

    [Fact]
    public async Task IfupdownBondAddressCanBeChanged()
    {
        Ifupdown();
        File.WriteAllText(P("/etc/network/interfaces.d/ursanas-bond-bond0"), IfupdownConfig.GenerateBond(Req("bond0", "192.168.1.50"), ["eth0", "eth1"], "balance-alb", Mac));
        var (svc, runner) = Make("bond0", Bonds, "");

        await svc.EditAsync(Req("bond0", "192.168.1.60"));

        var text = File.ReadAllText(P("/etc/network/interfaces.d/ursanas-bond-bond0"));
        Assert.Contains("address 192.168.1.60/24", text);
        Assert.DoesNotContain("192.168.1.50", text);
        Assert.Contains("bond-slaves eth0 eth1", text);
        Assert.Contains("bond-mode balance-alb", text);
        Assert.Contains("iface eth1 inet manual\n    bond-master bond0", text);
        Assert.Contains(runner.Calls, c => c.File == "ifup" && c.Args.SequenceEqual(["bond0"]));
    }

    [Fact]
    public async Task BondThatIsAPortCannotBeEditedDirectly()
    {
        Ifupdown();
        var (svc, _) = Make("ovsbr0", _ => Card("eth0", "bond0") + "," + Device("bond0", "bond", "ovs-system", "192.168.1.50"), "bond0\n");

        await Assert.ThrowsAsync<OperationException>(() => svc.EditAsync(Req("bond0", "192.168.1.60")));
    }
}
