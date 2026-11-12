using System.Diagnostics;
using System.Text.Json;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Lecture seule de l'état des interfaces réseau : <c>ip -j -d addr</c> (adresses, MAC, MTU, type), <c>ip -j route</c> (passerelles),
/// <c>/sys/class/net</c> (vitesse, duplex, compteurs) et <c>resolv.conf</c> (DNS). Ne modifie jamais la configuration réseau.
/// </summary>
public class NetworkService(ICommandRunner runner, IConfiguration cfg)
{
    string SysRoot => cfg["Network:SysClassNet"] ?? "/sys/class/net";
    string[] ResolvFiles => cfg.GetSection("Network:ResolvConf").Get<string[]>() ?? ["/etc/resolv.conf", "/run/systemd/resolve/resolv.conf"];

    public async Task<NetSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var ms = Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;
        if (cfg["URSANAS_FAKE"] == "1") return Fake(ms);
        var addr = await runner.RunAsync("ip", ["-j", "-d", "addr", "show"], ct: ct);
        var r4 = await runner.RunAsync("ip", ["-j", "-4", "route", "show", "default"], ct: ct);
        var r6 = await runner.RunAsync("ip", ["-j", "-6", "route", "show", "default"], ct: ct);
        var dns = ParseResolvConf(ResolvFiles.Select(f => File.Exists(f) ? File.ReadAllText(f) : null));
        return new NetSnapshot(ms, Parse(addr.StdOut, r4.StdOut, r6.StdOut, dns, ReadSys), NetBackend.Detect(cfg));
    }

    /// <summary>Lit un fichier de <c>/sys/class/net/&lt;nom&gt;/</c> (null s'il n'existe pas ou n'est pas lisible : lien coupé, interface virtuelle).</summary>
    string? ReadSys(string iface, string file)
    {
        try
        {
            var path = Path.Combine(SysRoot, iface, file);
            return File.Exists(path) || Directory.Exists(path) ? (File.Exists(path) ? File.ReadAllText(path).Trim() : "") : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // Les interfaces gérées par Docker et libvirt (ponts, paires veth, cartes des machines) ne sont pas des cartes du NAS.
    static bool Hidden(string name, string? kind)
        => name is "lo" or "ovs-system" || kind is "veth" or "tun" or "tap"
           || name.StartsWith("docker", StringComparison.Ordinal) || name.StartsWith("br-", StringComparison.Ordinal)
           || name.StartsWith("virbr", StringComparison.Ordinal) || name.StartsWith("vnet", StringComparison.Ordinal)
           || name.StartsWith("veth", StringComparison.Ordinal) || name.StartsWith("tap", StringComparison.Ordinal);

    /// <summary>Assemble les interfaces à partir des sorties de <c>ip</c> et des fichiers de <c>/sys</c> (fonction pure, testée).</summary>
    public static IReadOnlyList<NetIface> Parse(string addrJson, string route4Json, string route6Json, IReadOnlyList<string> dns, Func<string, string, string?> sys)
    {
        var gw4 = Gateways(route4Json);
        var gw6 = Gateways(route6Json);
        var result = new List<NetIface>();
        JsonElement root;
        try { root = JsonDocument.Parse(string.IsNullOrWhiteSpace(addrJson) ? "[]" : addrJson).RootElement; }
        catch (JsonException) { return result; }
        if (root.ValueKind != JsonValueKind.Array) return result;

        foreach (var e in root.EnumerateArray())
        {
            var name = Str(e, "ifname");
            if (name is null) continue;
            string? kind = e.TryGetProperty("linkinfo", out var li) ? Str(li, "info_kind") : null;
            if (Hidden(name, kind)) continue;

            var v4 = new List<NetAddress>();
            var v6 = new List<NetAddress>();
            if (e.TryGetProperty("addr_info", out var infos) && infos.ValueKind == JsonValueKind.Array)
                foreach (var a in infos.EnumerateArray())
                {
                    var local = Str(a, "local");
                    if (local is null || !a.TryGetProperty("prefixlen", out var pl)) continue;
                    var prefix = pl.GetInt32();
                    var dyn = a.TryGetProperty("dynamic", out var d) && d.ValueKind == JsonValueKind.True;
                    var scope = Str(a, "scope") ?? "global";
                    if (Str(a, "family") == "inet") v4.Add(new NetAddress(local, prefix, NetMath.MaskOf(prefix), dyn, scope));
                    else if (Str(a, "family") == "inet6") v6.Add(new NetAddress(local, prefix, null, dyn, scope));
                }

            var flags = e.TryGetProperty("flags", out var fl) && fl.ValueKind == JsonValueKind.Array
                ? fl.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];
            var type = kind switch
            {
                "bridge" => "bridge", "bond" => "bond", "vlan" => "vlan", "openvswitch" => "ovs",
                null when sys(name, "wireless") is not null || sys(name, "phy80211") is not null => "wifi",
                null => "ethernet",
                _ => "virtual",
            };
            int? speed = int.TryParse(sys(name, "speed"), out var sp) && sp > 0 ? sp : null;
            var duplex = sys(name, "duplex") is { Length: > 0 } du && du != "unknown" ? du : null;
            long.TryParse(sys(name, "statistics/rx_bytes"), out var rx);
            long.TryParse(sys(name, "statistics/tx_bytes"), out var tx);
            var hasRoute = gw4.ContainsKey(name) || gw6.ContainsKey(name);
            var dhcp = v4.Any(a => a.Dynamic) || gw4.GetValueOrDefault(name) is { Dhcp: true };

            result.Add(new NetIface(name, type, flags.Contains("LOWER_UP"), Str(e, "address"), e.TryGetProperty("mtu", out var mtu) ? mtu.GetInt32() : 0,
                speed, duplex, v4, v6, gw4.GetValueOrDefault(name)?.Via, gw6.GetValueOrDefault(name)?.Via, hasRoute ? dns : [],
                dhcp, Str(e, "master"), rx, tx));
        }
        return result.OrderBy(i => i.Kind is "bridge" or "ovs" or "bond" or "vlan" ? 1 : 0).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    sealed record Gateway(string Via, bool Dhcp);

    static Dictionary<string, Gateway> Gateways(string json)
    {
        var map = new Dictionary<string, Gateway>();
        try
        {
            var root = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json).RootElement;
            if (root.ValueKind != JsonValueKind.Array) return map;
            foreach (var r in root.EnumerateArray())
                if (Str(r, "dev") is { } dev && Str(r, "gateway") is { } via && !map.ContainsKey(dev)) // la première route (la plus prioritaire) gagne
                    map[dev] = new Gateway(via, Str(r, "protocol") == "dhcp");
        }
        catch (JsonException) { }
        return map;
    }

    /// <summary>Serveurs DNS du premier <c>resolv.conf</c> qui en donne autre chose que le relais local de systemd-resolved (127.0.0.53).</summary>
    public static IReadOnlyList<string> ParseResolvConf(IEnumerable<string?> files)
    {
        foreach (var text in files)
        {
            if (text is null) continue;
            var servers = text.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("nameserver", StringComparison.Ordinal))
                .Select(l => l["nameserver".Length..].Trim().Split('%')[0])
                .Where(s => s.Length > 0 && !s.StartsWith("127.", StringComparison.Ordinal) && s != "::1").Distinct().ToList();
            if (servers.Count > 0) return servers;
        }
        return [];
    }

    static string? Str(JsonElement e, string prop) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static NetSnapshot Fake(long ms)
    {
        var t = ms / 1000;
        return new NetSnapshot(ms,
        [
            new NetIface("eth0", "ethernet", true, "00:15:5d:00:aa:01", 1500, 1000, "full",
                [new NetAddress("192.168.1.20", 24, "255.255.255.0", true, "global")], [new NetAddress("fe80::215:5dff:fe00:aa01", 64, null, false, "link")],
                "192.168.1.1", null, ["192.168.1.1", "1.1.1.1"], true, null, t * 120_000, t * 45_000),
            new NetIface("eth1", "ethernet", false, "00:15:5d:00:aa:02", 1500, null, null, [], [], null, null, [], false, null, 0, 0),
        ], "netplan");
    }
}
