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
public sealed record NetSnapshot(long SampleMs, IReadOnlyList<NetIface> Interfaces);

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
