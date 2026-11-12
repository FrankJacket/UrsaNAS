namespace Nas.Contracts;

/// <summary>Une adresse IP d'une interface. <c>Mask</c> n'existe que pour IPv4 ; <c>Dynamic</c> : obtenue par DHCP / autoconfiguration.</summary>
public sealed record NetAddress(string Address, int PrefixLength, string? Mask, bool Dynamic, string Scope);

/// <summary>
/// Une interface réseau de la machine. <c>Kind</c> : ethernet, wifi, bridge, bond, vlan ou virtual. <c>Connected</c> : un câble (ou le lien) est détecté.
/// Les octets sont cumulés depuis le démarrage : l'interface en déduit le débit entre deux lectures.
/// </summary>
public sealed record NetIface(string Name, string Kind, bool Connected, string? Mac, int Mtu, int? SpeedMbps, string? Duplex,
    IReadOnlyList<NetAddress> Ipv4, IReadOnlyList<NetAddress> Ipv6, string? Gateway4, string? Gateway6, IReadOnlyList<string> Dns,
    bool Dhcp4, string? Master, long RxBytes, long TxBytes);

/// <summary><c>SampleMs</c> : horloge monotone de l'agent, pour calculer les débits entre deux instantanés.</summary>
/// <param name="Backend">Outil qui écrit la configuration réseau de cette machine : <c>netplan</c>, <c>ifupdown</c> ou <c>none</c> (non pris en charge : lecture seule).</param>
public sealed record NetSnapshot(long SampleMs, IReadOnlyList<NetIface> Interfaces, string Backend = "none");

public static class NetMath
{
    /// <summary>Masque de sous-réseau IPv4 en notation pointée (24 → 255.255.255.0).</summary>
    public static string MaskOf(int prefix)
    {
        prefix = Math.Clamp(prefix, 0, 32);
        var m = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return $"{m >> 24}.{(m >> 16) & 255}.{(m >> 8) & 255}.{m & 255}";
    }

    /// <summary>Octets par seconde entre deux lectures d'un compteur ; 0 si le compteur a reculé (redémarrage) ou si le temps n'a pas avancé.</summary>
    public static double Rate(long before, long after, long beforeMs, long afterMs)
        => afterMs <= beforeMs || after < before ? 0 : (after - before) * 1000.0 / (afterMs - beforeMs);
}

/// <summary>
/// Réglage IPv4 ou IPv6 d'une interface. <c>Mode</c> : <c>auto</c> (DHCP / autoconfiguration) ou <c>manual</c>.
/// <c>Netmask</c> : masque pointé ou longueur de préfixe pour IPv4 ; longueur de préfixe pour IPv6. <c>DnsMode</c> : <c>auto</c> ou <c>manual</c>.
/// </summary>
public sealed record NetIpConfig(string Mode, string? Address, string? Netmask, string? Gateway, string DnsMode, IReadOnlyList<string> Dns);

/// <summary>Modification de l'adressage d'une interface. Elle s'applique tout de suite, puis revient en arrière seule si elle n'est pas confirmée.</summary>
public sealed record NetEditRequest(string Iface, NetIpConfig Ipv4, NetIpConfig Ipv6);

/// <summary>Une modification appliquée, en attente de confirmation : sans confirmation avant <c>DeadlineUtc</c>, l'ancienne configuration revient.</summary>
public sealed record NetPending(string Iface, DateTime DeadlineUtc, int SecondsLeft);

public static class NetRules
{
    public const int ConfirmSeconds = 60;
    public const int MaxDns = 3;

    public static bool IsIfaceName(string? s) => !string.IsNullOrEmpty(s) && s.Length <= 15 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');

    public static bool TryIPv4(string? s, out uint value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s)) return false;
        var parts = s.Split('.');
        if (parts.Length != 4) return false;
        foreach (var p in parts)
        {
            if (p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit) || !int.TryParse(p, out var n) || n > 255) return false;
            value = value << 8 | (uint)n;
        }
        return true;
    }

    public static bool TryIPv6(string? s, out System.Net.IPAddress? ip)
    {
        ip = null;
        if (string.IsNullOrEmpty(s) || s.Any(c => !(char.IsAsciiHexDigit(c) || c is ':' or '.'))) return false; // pas de zone (%eth0), pas de crochets
        return System.Net.IPAddress.TryParse(s, out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
    }

    /// <summary>Longueur de préfixe d'un masque pointé contigu (255.255.255.0 → 24) ou d'un nombre (« 24 ») ; null si invalide.</summary>
    public static int? PrefixOf4(string? mask)
    {
        if (string.IsNullOrWhiteSpace(mask)) return null;
        mask = mask.Trim();
        if (!mask.Contains('.')) return int.TryParse(mask, out var n) && n is >= 1 and <= 32 ? n : null;
        if (!TryIPv4(mask, out var m) || m == 0) return null;
        var ones = System.Numerics.BitOperations.PopCount(m);
        return m == uint.MaxValue << (32 - ones) ? ones : null; // les 1 doivent être contigus
    }

    static bool UsableV4(uint a) => a >> 24 is not (0 or 127 or >= 224);

    /// <summary>Contrôle une demande : adresses valides, masque contigu, passerelle dans le sous-réseau, DNS valides. Renvoie le message d'erreur ou null.</summary>
    public static string? Validate(NetEditRequest r)
    {
        if (!IsIfaceName(r.Iface)) return Msg.F("Invalid network interface name.");
        if (ValidateV4(r.Ipv4) is { } e4) return e4;
        return ValidateV6(r.Ipv6);
    }

    static string? ValidateDns(NetIpConfig c, bool v6)
    {
        if (c.DnsMode is not ("auto" or "manual")) return Msg.F("Unknown setting.");
        if (c.DnsMode == "auto") return null;
        if (c.Dns.Count == 0 || c.Dns.Count > MaxDns) return Msg.F("Enter between 1 and {0} DNS servers.", MaxDns);
        foreach (var d in c.Dns)
            if (v6 ? !TryIPv6(d, out _) : !TryIPv4(d, out _)) return Msg.F("Invalid DNS server: {0}.", d);
        return null;
    }

    static string? ValidateV4(NetIpConfig c)
    {
        if (c.Mode is not ("auto" or "manual")) return Msg.F("Unknown setting.");
        if (c.Mode == "manual")
        {
            if (!TryIPv4(c.Address, out var addr) || !UsableV4(addr)) return Msg.F("Invalid IPv4 address.");
            if (PrefixOf4(c.Netmask) is not { } prefix) return Msg.F("Invalid subnet mask.");
            if (!string.IsNullOrWhiteSpace(c.Gateway))
            {
                if (!TryIPv4(c.Gateway, out var gw) || !UsableV4(gw)) return Msg.F("Invalid gateway.");
                var mask = uint.MaxValue << (32 - prefix);
                if ((gw & mask) != (addr & mask) || gw == addr) return Msg.F("The gateway must be another address of the same network.");
            }
        }
        return ValidateDns(c, false);
    }

    static string? ValidateV6(NetIpConfig c)
    {
        if (c.Mode is not ("auto" or "manual")) return Msg.F("Unknown setting.");
        if (c.Mode == "manual")
        {
            if (!TryIPv6(c.Address, out var ip) || ip!.IsIPv6LinkLocal || ip.IsIPv6Multicast || System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.IPv6Any))
                return Msg.F("Invalid IPv6 address.");
            if (!int.TryParse(c.Netmask, out var prefix) || prefix is < 1 or > 128) return Msg.F("Invalid IPv6 prefix length (1 to 128).");
            if (!string.IsNullOrWhiteSpace(c.Gateway) && (!TryIPv6(c.Gateway, out var gw) || gw!.IsIPv6Multicast || gw.Equals(System.Net.IPAddress.IPv6Any)))
                return Msg.F("Invalid gateway.");
        }
        return ValidateDns(c, true);
    }
}
