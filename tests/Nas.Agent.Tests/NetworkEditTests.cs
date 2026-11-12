using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class NetworkEditRulesTests
{
    static NetIpConfig Auto(string dns = "auto", params string[] servers) => new("auto", null, null, null, dns, servers);
    static NetIpConfig Manual4(string addr = "192.168.1.20", string mask = "255.255.255.0", string? gw = "192.168.1.1") => new("manual", addr, mask, gw, "auto", []);
    static NetIpConfig Manual6(string addr = "2001:db8::20", string prefix = "64", string? gw = "fe80::1") => new("manual", addr, prefix, gw, "auto", []);
    static NetEditRequest Req(NetIpConfig v4, NetIpConfig? v6 = null) => new("eth0", v4, v6 ?? Auto());

    [Fact] public void AutoIsValid() => Assert.Null(NetRules.Validate(Req(Auto())));
    [Fact] public void ManualIsValid() => Assert.Null(NetRules.Validate(Req(Manual4(), Manual6())));
    [Fact] public void PrefixLengthIsAcceptedAsMask() => Assert.Null(NetRules.Validate(Req(Manual4(mask: "24"))));
    [Fact] public void GatewayIsOptional() => Assert.Null(NetRules.Validate(Req(Manual4(gw: null))));

    [Theory]
    [InlineData("192.168.1")]
    [InlineData("192.168.1.256")]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("192.168.1.20; reboot")]
    [InlineData("")]
    public void InvalidIPv4Address(string a) => Assert.NotNull(NetRules.Validate(Req(Manual4(addr: a))));

    [Theory]
    [InlineData("255.0.255.0")]
    [InlineData("255.255.255")]
    [InlineData("0")]
    [InlineData("33")]
    [InlineData("0.0.0.0")]
    public void InvalidMask(string m) => Assert.NotNull(NetRules.Validate(Req(Manual4(mask: m))));

    [Theory]
    [InlineData("192.168.2.1")]   // autre réseau
    [InlineData("192.168.1.20")]  // l'adresse elle-même
    [InlineData("abc")]
    public void InvalidGateway(string g) => Assert.NotNull(NetRules.Validate(Req(Manual4(gw: g))));

    [Theory]
    [InlineData("fe80::1")]       // lien local
    [InlineData("::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::zz")]
    [InlineData("2001:db8::1%eth0")]
    public void InvalidIPv6Address(string a) => Assert.NotNull(NetRules.Validate(Req(Auto(), Manual6(addr: a))));

    [Theory]
    [InlineData("0")]
    [InlineData("129")]
    [InlineData("x")]
    public void InvalidIPv6Prefix(string p) => Assert.NotNull(NetRules.Validate(Req(Auto(), Manual6(prefix: p))));

    [Fact] public void ManualDnsNeedsServers() => Assert.NotNull(NetRules.Validate(Req(Auto("manual"))));
    [Fact] public void ManualDnsIsChecked() => Assert.NotNull(NetRules.Validate(Req(Auto("manual", "1.1.1.1", "nope"))));
    [Fact] public void ManualDnsAtMostThree() => Assert.NotNull(NetRules.Validate(Req(Auto("manual", "1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4"))));
    [Fact] public void ManualDnsIsValid() => Assert.Null(NetRules.Validate(Req(Auto("manual", "1.1.1.1", "8.8.8.8"))));
    [Fact] public void UnknownMode() => Assert.NotNull(NetRules.Validate(Req(new NetIpConfig("static", null, null, null, "auto", []))));

    [Theory]
    [InlineData("eth0", true)]
    [InlineData("enp5s0.10", true)]
    [InlineData("", false)]
    [InlineData("eth0;ls", false)]
    [InlineData("a b", false)]
    [InlineData("0123456789abcdef", false)]
    public void InterfaceName(string name, bool ok) => Assert.Equal(ok, NetRules.IsIfaceName(name));
}

public class NetworkConfigFilesTests
{
    static readonly NetEditRequest Static = new("eth0",
        new NetIpConfig("manual", "192.168.1.20", "255.255.255.0", "192.168.1.1", "manual", ["1.1.1.1", "8.8.8.8"]),
        new NetIpConfig("manual", "2001:db8::20", "64", "fe80::1", "auto", []));
    static readonly NetEditRequest Auto = new("eth0",
        new NetIpConfig("auto", null, null, null, "auto", []), new NetIpConfig("auto", null, null, null, "auto", []));

    const string Cloud = """
        # This file is generated from information provided by the datasource.
        network:
          version: 2
          ethernets:
            eth0:
              match:
                macaddress: 00:15:5d:00:aa:01
              set-name: eth0
              dhcp4: true
              addresses:
              - 10.9.9.9/24
              routes:
                - to: default
                  via: 10.9.9.1
              nameservers:
                addresses: [9.9.9.9]
              mtu: 1500
            eth1:
              dhcp4: true
        """;

    [Fact]
    public void NetplanStripKeepsEverythingElse()
    {
        var s = NetplanConfig.StripIface(Cloud, "eth0");
        Assert.Contains("match:", s);
        Assert.Contains("set-name: eth0", s);
        Assert.Contains("mtu: 1500", s);
        Assert.DoesNotContain("dhcp4: true\n      addresses", s);
        Assert.DoesNotContain("10.9.9.9", s);
        Assert.DoesNotContain("10.9.9.1", s);
        Assert.DoesNotContain("9.9.9.9", s);
        Assert.DoesNotContain("routes:", s);
        Assert.Contains("eth1:\n      dhcp4: true", s);       // l'autre interface n'est pas touchée
        Assert.Equal(1, s.Split("dhcp4: true").Length - 1);    // seul celui d'eth1 reste
    }

    [Fact]
    public void NetplanSectionOf()
    {
        Assert.Equal("ethernets", NetplanConfig.SectionOf(Cloud, "eth1"));
        Assert.Null(NetplanConfig.SectionOf(Cloud, "eth9"));
        Assert.Equal("bridges", NetplanConfig.SectionOf("network:\n  version: 2\n  bridges:\n    br0:\n      dhcp4: true\n", "br0"));
    }

    [Fact]
    public void NetplanFlowStyleIsRefused()
        => Assert.Throws<OperationException>(() => NetplanConfig.StripIface("network:\n  ethernets:\n    eth0: {dhcp4: true}\n", "eth0"));

    [Fact]
    public void NetplanGenerateStatic()
    {
        var y = NetplanConfig.Generate(Static, "ethernets");
        Assert.Contains("    eth0:\n", y);
        Assert.Contains("dhcp4: false", y);
        Assert.Contains("addresses: [192.168.1.20/24, 2001:db8::20/64]", y);
        Assert.Contains("- to: 0.0.0.0/0\n          via: 192.168.1.1", y);
        Assert.Contains("- to: \"::/0\"\n          via: fe80::1", y);
        Assert.Contains("nameservers:\n        addresses: [1.1.1.1, 8.8.8.8]", y);
    }

    [Fact]
    public void NetplanGenerateAuto()
    {
        var y = NetplanConfig.Generate(Auto, "ethernets");
        Assert.Contains("dhcp4: true", y);
        Assert.Contains("dhcp6: true", y);
        Assert.DoesNotContain("addresses", y);
        Assert.DoesNotContain("routes", y);
    }

    [Fact]
    public void NetplanAutoWithManualDnsIgnoresTheDnsOfDhcp()
    {
        var req = Auto with { Ipv4 = new NetIpConfig("auto", null, null, null, "manual", ["1.1.1.1"]) };
        var y = NetplanConfig.Generate(req, "ethernets");
        Assert.Contains("dhcp4-overrides:\n        use-dns: false", y);
        Assert.Contains("addresses: [1.1.1.1]", y);
    }

    const string Interfaces = """
        # This file describes the network interfaces
        source /etc/network/interfaces.d/*

        auto lo
        iface lo inet loopback

        allow-hotplug eth0
        iface eth0 inet dhcp
        iface eth0 inet6 auto

        auto eth1
        iface eth1 inet static
            address 10.0.0.2/24
            gateway 10.0.0.1
        """;

    [Fact]
    public void IfupdownStripKeepsTheRest()
    {
        var s = IfupdownConfig.Strip(Interfaces, "eth0");
        Assert.DoesNotContain("iface eth0", s);
        Assert.Contains("allow-hotplug eth0", s);   // le démarrage au boot reste
        Assert.Contains("iface eth1 inet static\n    address 10.0.0.2/24\n    gateway 10.0.0.1", s);
        Assert.Contains("iface lo inet loopback", s);
        Assert.True(IfupdownConfig.HasAuto(Interfaces, "eth0"));
        Assert.True(IfupdownConfig.HasAuto(Interfaces, "eth1"));
        Assert.False(IfupdownConfig.HasAuto(Interfaces, "eth2"));
    }

    [Fact]
    public void IfupdownStripRemovesTheOptionsOfTheStanza()
    {
        var s = IfupdownConfig.Strip(Interfaces, "eth1");
        Assert.DoesNotContain("10.0.0.2", s);
        Assert.DoesNotContain("gateway", s);
        Assert.Contains("iface eth0 inet dhcp", s);
    }

    [Fact]
    public void IfupdownGenerate()
    {
        var y = IfupdownConfig.Generate(Static, addAuto: true);
        Assert.StartsWith("# Written by UrsaNAS", y);
        Assert.Contains("auto eth0\n", y);
        Assert.Contains("iface eth0 inet static\n    address 192.168.1.20/24\n    gateway 192.168.1.1\n    dns-nameservers 1.1.1.1 8.8.8.8\n", y);
        Assert.Contains("iface eth0 inet6 static\n    address 2001:db8::20/64\n    gateway fe80::1\n", y);
        var a = IfupdownConfig.Generate(Auto, addAuto: false);
        Assert.DoesNotContain("auto eth0", a);
        Assert.Contains("iface eth0 inet dhcp", a);
        Assert.Contains("iface eth0 inet6 auto", a);
        Assert.DoesNotContain("dhcp 1", a); // un client DHCPv6 n'est pas installé par défaut : autoconfiguration (RA) seulement
        Assert.Equal("# Written by UrsaNAS\nnameserver 1.1.1.1\nnameserver 8.8.8.8\n", IfupdownConfig.ResolvConf(Static));
    }
}

public class NetworkChangeTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ursanas-net-" + Guid.NewGuid().ToString("N"));

    public NetworkChangeTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    string P(string p) => Path.Combine(root, p.TrimStart('/'));

    const string OldIp = """[{"ifname":"eth0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","addr_info":[{"family":"inet","local":"192.168.1.20","prefixlen":24,"scope":"global","dynamic":true}]}]""";
    const string NewIp = """[{"ifname":"eth0","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:00:aa:01","addr_info":[{"family":"inet","local":"192.168.1.50","prefixlen":24,"scope":"global"}]}]""";
    const string Route = """[{"dst":"default","gateway":"192.168.1.1","dev":"eth0","metric":100}]""";
    const string Neighbor = """[{"dst":"192.168.1.1","dev":"eth0","lladdr":"aa:bb:cc:dd:ee:01","state":["REACHABLE"]}]""";

    static readonly NetEditRequest ToStatic = new("eth0",
        new NetIpConfig("manual", "192.168.1.50", "255.255.255.0", "192.168.1.1", "auto", []),
        new NetIpConfig("auto", null, null, null, "auto", []));

    /// <summary>L'état simulé du système : « nouvelle configuration » dès que le fichier d'UrsaNAS (ou son bloc ifupdown) est en place.</summary>
    bool NewConfigInPlace() => File.Exists(P("/etc/netplan/90-ursanas-eth0.yaml")) || File.Exists(P("/etc/network/interfaces.d/ursanas-eth0"))
        || (File.Exists(P("/etc/network/interfaces")) && File.ReadAllText(P("/etc/network/interfaces")).Contains("inet static"));

    sealed class Sim { public bool AddressApplies = true, GatewayAnswers = true, PingMissing; }

    (NetworkChangeService Svc, FakeRunner Runner) Make(Sim? sim = null, Func<FakeRunner.Call, CommandResult?>? extra = null, int verifySeconds = 1)
    {
        sim ??= new Sim();
        var runner = new FakeRunner
        {
            Handler = c =>
            {
                if (extra?.Invoke(c) is { } custom) return custom;
                if (c.File == "ping") return sim.PingMissing ? throw new System.ComponentModel.Win32Exception("not found") : new CommandResult(sim.GatewayAnswers ? 0 : 1, "", "");
                if (c.File == "ip" && c.Args.Contains("neigh")) return new CommandResult(0, sim.GatewayAnswers ? Neighbor : """[{"dst":"192.168.1.1","dev":"eth0","state":["FAILED"]}]""", "");
                if (c.File == "ip" && c.Args.Contains("route")) return new CommandResult(0, NewConfigInPlace() && sim.AddressApplies ? Route : "[]", "");
                if (c.File == "ip" && c.Args.Contains("addr") && c.Args.Contains("show")) return new CommandResult(0, NewConfigInPlace() && sim.AddressApplies ? NewIp : OldIp, "");
                return new CommandResult(0, "[]", "");
            },
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Network:Root"] = root, ["Network:PendingFile"] = P("/state/pending.json"), ["Network:SysClassNet"] = P("/sys/class/net"),
            ["Network:VerifySeconds"] = verifySeconds.ToString(), ["Network:VerifyPollMs"] = "50",
        }).Build();
        return (new NetworkChangeService(runner, new NetworkService(runner, cfg), cfg, NullLogger<NetworkChangeService>.Instance), runner);
    }

    void NetplanSetup(string original)
    {
        Directory.CreateDirectory(P("/etc/netplan"));
        Directory.CreateDirectory(P("/usr/sbin"));
        File.WriteAllText(P("/usr/sbin/netplan"), "");
        File.WriteAllText(P("/etc/netplan/50-cloud-init.yaml"), original);
    }

    const string Original = "network:\n  version: 2\n  ethernets:\n    eth0:\n      dhcp4: true\n";

    int Applies(FakeRunner r) => r.Calls.Count(c => c.File == "netplan" && c.Args.SequenceEqual(["apply"]));

    [Fact]
    public async Task NetplanEditIsAppliedVerifiedAndKept()
    {
        NetplanSetup(Original);
        var (svc, runner) = Make();
        await svc.EditAsync(ToStatic);
        Assert.Null(svc.GetPending());
        Assert.Contains("192.168.1.50/24", File.ReadAllText(P("/etc/netplan/90-ursanas-eth0.yaml")));
        Assert.DoesNotContain("dhcp4: true", File.ReadAllText(P("/etc/netplan/50-cloud-init.yaml")));
        Assert.Contains(runner.Calls, c => c.File == "netplan" && c.Args.SequenceEqual(["generate"]));
        Assert.Equal(1, Applies(runner));
        Assert.Contains(runner.Calls, c => c.File == "ping" && c.Args.Contains("192.168.1.1"));
        Assert.False(File.Exists(P("/state/pending.json")));   // plus rien à restaurer
    }

    [Fact]
    public async Task WhenTheAddressNeverAppearsTheOldSettingsComeBack()
    {
        NetplanSetup(Original);
        var (svc, runner) = Make(new Sim { AddressApplies = false });
        var e = await Assert.ThrowsAsync<OperationException>(() => svc.EditAsync(ToStatic));
        Assert.Contains("could not be verified", e.Message);
        Assert.Contains("192.168.1.50", e.Message);
        Assert.Null(svc.GetPending());
        Assert.Equal(Original, File.ReadAllText(P("/etc/netplan/50-cloud-init.yaml")));
        Assert.False(File.Exists(P("/etc/netplan/90-ursanas-eth0.yaml")));
        Assert.False(File.Exists(P("/state/pending.json")));
        Assert.Equal(2, Applies(runner));   // appliquer, puis réappliquer l'ancienne configuration
    }

    [Fact]
    public async Task AGatewayThatDoesNotAnswerMeansRollback()
    {
        NetplanSetup(Original);
        var (svc, _) = Make(new Sim { GatewayAnswers = false });
        var e = await Assert.ThrowsAsync<OperationException>(() => svc.EditAsync(ToStatic));
        Assert.Contains("does not answer", e.Message);
        Assert.Equal(Original, File.ReadAllText(P("/etc/netplan/50-cloud-init.yaml")));
        Assert.False(File.Exists(P("/etc/netplan/90-ursanas-eth0.yaml")));
    }

    [Fact]
    public async Task WithoutPingTheNeighbourTableIsEnough()
    {
        NetplanSetup(Original);
        var (svc, _) = Make(new Sim { PingMissing = true });
        await svc.EditAsync(ToStatic);
        Assert.True(File.Exists(P("/etc/netplan/90-ursanas-eth0.yaml")));
    }

    [Fact]
    public async Task AutoAddressNeedsALeaseNotAGateway()
    {
        NetplanSetup("network:\n  version: 2\n  ethernets:\n    eth0:\n      addresses: [192.168.1.50/24]\n");
        var (svc, _) = Make(extra: c => c.File == "ip" && c.Args.Contains("addr") && c.Args.Contains("show") ? new CommandResult(0, OldIp, "") : null);
        var auto = new NetEditRequest("eth0", new NetIpConfig("auto", null, null, null, "auto", []), new NetIpConfig("auto", null, null, null, "auto", []));
        await svc.EditAsync(auto);   // OldIp porte une adresse globale : le bail DHCP est là
        Assert.Contains("dhcp4: true", File.ReadAllText(P("/etc/netplan/90-ursanas-eth0.yaml")));
        Assert.DoesNotContain("192.168.1.50", File.ReadAllText(P("/etc/netplan/50-cloud-init.yaml")));
    }

    [Fact]
    public async Task AFailingApplyRestoresAtOnce()
    {
        NetplanSetup(Original);
        var (svc, _) = Make(extra: c => c.File == "netplan" && c.Args.SequenceEqual(["generate"]) && File.Exists(P("/etc/netplan/90-ursanas-eth0.yaml"))
            && File.ReadAllText(P("/etc/netplan/90-ursanas-eth0.yaml")).Contains("192.168.1.50") ? new CommandResult(1, "", "bad yaml\nline 2") : null);
        var e = await Assert.ThrowsAsync<OperationException>(() => svc.EditAsync(ToStatic));
        Assert.Contains("bad yaml", e.Message);
        Assert.Null(svc.GetPending());
        Assert.Equal(Original, File.ReadAllText(P("/etc/netplan/50-cloud-init.yaml")));
        Assert.False(File.Exists(P("/etc/netplan/90-ursanas-eth0.yaml")));
    }

    [Fact]
    public async Task InvalidRequestsAreRefusedBeforeAnyWrite()
    {
        NetplanSetup(Original);
        var (svc, runner) = Make();
        var bad = ToStatic with { Ipv4 = ToStatic.Ipv4 with { Gateway = "10.0.0.1" } };
        await Assert.ThrowsAsync<OperationException>(() => svc.EditAsync(bad));
        await Assert.ThrowsAsync<OperationException>(() => svc.EditAsync(ToStatic with { Iface = "eth9" }));
        Assert.DoesNotContain(runner.Calls, c => c.File == "netplan");
        Assert.Equal(Original, File.ReadAllText(P("/etc/netplan/50-cloud-init.yaml")));
    }

    [Fact]
    public async Task NoSupportedToolMeansNoEdit()
    {
        var (svc, _) = Make(); // ni netplan ni ifupdown dans la racine de test
        var e = await Assert.ThrowsAsync<OperationException>(() => svc.EditAsync(ToStatic));
        Assert.Contains("cannot be edited", e.Message);
    }

    [Fact]
    public async Task CloudInitIsDisabledWhenItsFileIsTouched()
    {
        NetplanSetup(Original);
        Directory.CreateDirectory(P("/etc/cloud/cloud.cfg.d"));
        var (svc, _) = Make();
        await svc.EditAsync(ToStatic);
        Assert.True(File.Exists(P("/etc/cloud/cloud.cfg.d/99-disable-network-config.cfg")));
    }

    [Fact]
    public async Task CloudInitIsLeftAloneWhenTheChangeFails()
    {
        NetplanSetup(Original);
        Directory.CreateDirectory(P("/etc/cloud/cloud.cfg.d"));
        var (svc, _) = Make(new Sim { AddressApplies = false });
        await Assert.ThrowsAsync<OperationException>(() => svc.EditAsync(ToStatic));
        Assert.False(File.Exists(P("/etc/cloud/cloud.cfg.d/99-disable-network-config.cfg")));
    }

    void IfupdownSetup(string interfaces)
    {
        Directory.CreateDirectory(P("/etc/network/interfaces.d"));
        Directory.CreateDirectory(P("/sbin"));
        File.WriteAllText(P("/sbin/ifup"), "");
        File.WriteAllText(P("/etc/network/interfaces"), interfaces);
    }

    [Fact]
    public async Task IfupdownEditUsesAnInterfacesDFileAndRestartsTheInterface()
    {
        IfupdownSetup("source /etc/network/interfaces.d/*\n\nauto lo\niface lo inet loopback\n\nallow-hotplug eth0\niface eth0 inet dhcp\n");
        var (svc, runner) = Make();
        await svc.EditAsync(ToStatic);
        var main = File.ReadAllText(P("/etc/network/interfaces"));
        Assert.DoesNotContain("iface eth0", main);
        var ours = File.ReadAllText(P("/etc/network/interfaces.d/ursanas-eth0"));
        Assert.Contains("iface eth0 inet static", ours);
        Assert.DoesNotContain("auto eth0", ours);   // allow-hotplug existe déjà
        Assert.Contains(runner.Calls, c => c.File == "ifdown" && c.Args.SequenceEqual(["--force", "eth0"]));
        Assert.Contains(runner.Calls, c => c.File == "ifup" && c.Args.SequenceEqual(["eth0"]));
    }

    [Fact]
    public async Task IfupdownFailureRestoresTheMainFile()
    {
        IfupdownSetup("source /etc/network/interfaces.d/*\n\nallow-hotplug eth0\niface eth0 inet dhcp\n");
        var before = File.ReadAllText(P("/etc/network/interfaces"));
        var (svc, _) = Make(new Sim { GatewayAnswers = false });
        await Assert.ThrowsAsync<OperationException>(() => svc.EditAsync(ToStatic));
        Assert.Equal(before, File.ReadAllText(P("/etc/network/interfaces")));
        Assert.False(File.Exists(P("/etc/network/interfaces.d/ursanas-eth0")));
    }

    [Fact]
    public async Task IfupdownWithoutSourceAppendsToTheMainFile()
    {
        IfupdownSetup("auto eth0\niface eth0 inet dhcp\n");
        var (svc, _) = Make();
        await svc.EditAsync(ToStatic);
        var main = File.ReadAllText(P("/etc/network/interfaces"));
        Assert.Contains("auto eth0", main);
        Assert.Contains("iface eth0 inet static", main);
        Assert.DoesNotContain("dhcp\n", main);
        Assert.False(File.Exists(P("/etc/network/interfaces.d/ursanas-eth0")));
    }

    [Fact]
    public async Task IfupdownRefusesManualDnsWithDhcp()
    {
        IfupdownSetup("auto eth0\niface eth0 inet dhcp\n");
        var (svc, _) = Make();
        var req = ToStatic with { Ipv4 = new NetIpConfig("auto", null, null, null, "manual", ["1.1.1.1"]) };
        await Assert.ThrowsAsync<OperationException>(() => svc.EditAsync(req));
    }

    [Fact]
    public async Task AnExpiredChangeIsRevertedWhenTheAgentStarts()
    {
        // L'agent s'est arrêté en pleine modification : le fichier d'état garde les originaux, l'échéance est passée.
        NetplanSetup(Original);
        File.WriteAllText(P("/etc/netplan/50-cloud-init.yaml"), "network:\n  version: 2\n");   // état « modifié »
        File.WriteAllText(P("/etc/netplan/90-ursanas-eth0.yaml"), "half written");
        var state = new NetworkChangeService.PendingState("eth0", "netplan", DateTime.UtcNow.AddSeconds(-5), [
            new NetworkChangeService.FileBackup(P("/etc/netplan/50-cloud-init.yaml"), Original),
            new NetworkChangeService.FileBackup(P("/etc/netplan/90-ursanas-eth0.yaml"), null)]);
        Directory.CreateDirectory(P("/state"));
        File.WriteAllText(P("/state/pending.json"), System.Text.Json.JsonSerializer.Serialize(state));

        var (svc, runner) = Make();
        await svc.RestoreOnStartupAsync();
        Assert.Equal(Original, File.ReadAllText(P("/etc/netplan/50-cloud-init.yaml")));
        Assert.False(File.Exists(P("/etc/netplan/90-ursanas-eth0.yaml")));
        Assert.False(File.Exists(P("/state/pending.json")));
        Assert.Equal(1, Applies(runner));
        Assert.Null(svc.GetPending());
    }
}
