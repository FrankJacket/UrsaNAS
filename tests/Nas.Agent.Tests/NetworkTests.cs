using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class NetworkTests
{
    const string Addr = """
    [
     {"ifindex":1,"ifname":"lo","flags":["LOOPBACK","UP","LOWER_UP"],"mtu":65536,"link_type":"loopback","address":"00:00:00:00:00:00",
      "addr_info":[{"family":"inet","local":"127.0.0.1","prefixlen":8,"scope":"host"}]},
     {"ifindex":2,"ifname":"eth0","flags":["BROADCAST","MULTICAST","UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"00:15:5d:aa:bb:01",
      "addr_info":[{"family":"inet","local":"192.168.1.20","prefixlen":24,"scope":"global","dynamic":true},
                   {"family":"inet6","local":"2001:db8::20","prefixlen":64,"scope":"global"},
                   {"family":"inet6","local":"fe80::1","prefixlen":64,"scope":"link"}]},
     {"ifindex":3,"ifname":"eth1","flags":["BROADCAST","MULTICAST","UP"],"mtu":9000,"link_type":"ether","address":"00:15:5d:aa:bb:02","addr_info":[]},
     {"ifindex":4,"ifname":"br0","flags":["BROADCAST","MULTICAST","UP","LOWER_UP"],"mtu":1500,"link_type":"ether","address":"aa:bb:cc:dd:ee:ff",
      "linkinfo":{"info_kind":"bridge"},"addr_info":[{"family":"inet","local":"10.0.0.2","prefixlen":16,"scope":"global"}]},
     {"ifindex":5,"ifname":"docker0","flags":["UP"],"mtu":1500,"link_type":"ether","linkinfo":{"info_kind":"bridge"},"addr_info":[]},
     {"ifindex":6,"ifname":"vethabc","flags":["UP","LOWER_UP"],"mtu":1500,"link_type":"ether","linkinfo":{"info_kind":"veth"},"addr_info":[]},
     {"ifindex":7,"ifname":"virbr0","flags":["UP"],"mtu":1500,"link_type":"ether","linkinfo":{"info_kind":"bridge"},"addr_info":[]}
    ]
    """;

    const string Route4 = """[{"dst":"default","gateway":"192.168.1.1","dev":"eth0","protocol":"dhcp","metric":100},{"dst":"default","gateway":"10.0.0.1","dev":"br0","metric":200}]""";
    const string Route6 = """[{"dst":"default","gateway":"fe80::ffff","dev":"eth0","protocol":"ra","metric":1024}]""";

    static string? Sys(string iface, string file) => (iface, file) switch
    {
        ("eth0", "speed") => "1000",
        ("eth0", "duplex") => "full",
        ("eth0", "statistics/rx_bytes") => "5000",
        ("eth0", "statistics/tx_bytes") => "700",
        ("eth1", "speed") => "-1",
        ("eth1", "duplex") => "unknown",
        _ => null,
    };

    [Fact]
    public void ParsesInterfacesAndHidesTheOnesOfDockerAndLibvirt()
    {
        var list = NetworkService.Parse(Addr, Route4, Route6, ["1.1.1.1"], Sys);
        Assert.Equal(["eth0", "eth1", "br0"], list.Select(i => i.Name)); // lo, docker0, veth*, virbr0 sont cachés ; ponts après les cartes
    }

    [Fact]
    public void ReadsAddressesGatewayDnsAndLinkState()
    {
        var eth0 = NetworkService.Parse(Addr, Route4, Route6, ["1.1.1.1"], Sys).Single(i => i.Name == "eth0");
        Assert.True(eth0.Connected);
        Assert.Equal("ethernet", eth0.Kind);
        Assert.Equal("00:15:5d:aa:bb:01", eth0.Mac);
        Assert.Equal(1500, eth0.Mtu);
        Assert.Equal(1000, eth0.SpeedMbps);
        Assert.Equal("full", eth0.Duplex);
        Assert.Equal("192.168.1.20", eth0.Ipv4.Single().Address);
        Assert.Equal("255.255.255.0", eth0.Ipv4.Single().Mask);
        Assert.True(eth0.Dhcp4);
        Assert.Equal("192.168.1.1", eth0.Gateway4);
        Assert.Equal("fe80::ffff", eth0.Gateway6);
        Assert.Equal(["1.1.1.1"], eth0.Dns);
        Assert.Equal(2, eth0.Ipv6.Count);
        Assert.Equal(5000, eth0.RxBytes);
        Assert.Equal(700, eth0.TxBytes);
    }

    [Fact]
    public void AnInterfaceWithoutCarrierIsNotConnected()
    {
        var eth1 = NetworkService.Parse(Addr, Route4, Route6, [], Sys).Single(i => i.Name == "eth1");
        Assert.False(eth1.Connected);
        Assert.Null(eth1.SpeedMbps);   // -1 : lien coupé
        Assert.Null(eth1.Duplex);      // « unknown »
        Assert.Empty(eth1.Ipv4);
        Assert.Equal(9000, eth1.Mtu);
        Assert.Empty(eth1.Dns);        // pas de route par défaut sur cette carte
    }

    [Fact]
    public void ABridgeIsRecognisedAndStaticAddressIsNotDhcp()
    {
        var br0 = NetworkService.Parse(Addr, Route4, Route6, [], Sys).Single(i => i.Name == "br0");
        Assert.Equal("bridge", br0.Kind);
        Assert.False(br0.Dhcp4);
        Assert.Equal("10.0.0.1", br0.Gateway4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void UnreadableOutputGivesNoInterface(string json)
        => Assert.Empty(NetworkService.Parse(json, json, json, [], Sys));

    [Fact]
    public void DnsSkipsTheLocalStubAndFallsBackToTheNextFile()
    {
        var stub = "nameserver 127.0.0.53\noptions edns0\n";
        var real = "# resolved\nnameserver 192.168.1.1\nnameserver 8.8.8.8\nnameserver 192.168.1.1\nnameserver fe80::1%eth0\n";
        Assert.Equal(["192.168.1.1", "8.8.8.8", "fe80::1"], NetworkService.ParseResolvConf([stub, real]));
        Assert.Empty(NetworkService.ParseResolvConf([stub, null]));
    }

    [Theory]
    [InlineData(0, "0.0.0.0")]
    [InlineData(8, "255.0.0.0")]
    [InlineData(24, "255.255.255.0")]
    [InlineData(20, "255.255.240.0")]
    [InlineData(32, "255.255.255.255")]
    public void MaskOfPrefix(int prefix, string mask) => Assert.Equal(mask, NetMath.MaskOf(prefix));

    [Fact]
    public void RateBetweenTwoReadings()
    {
        Assert.Equal(1000, NetMath.Rate(0, 2000, 0, 2000));
        Assert.Equal(0, NetMath.Rate(5000, 100, 0, 1000));   // compteur remis à zéro
        Assert.Equal(0, NetMath.Rate(0, 100, 1000, 1000));   // pas de temps écoulé
    }
}
