using System.Text;
using System.Text.RegularExpressions;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Lecture et écriture des fichiers de configuration réseau (fonctions pures, testées). UrsaNAS ne réécrit jamais un fichier en entier :
/// il retire seulement les réglages d'adressage de l'interface modifiée, puis écrit les nouveaux dans un fichier à lui.
/// </summary>
public static partial class NetplanConfig
{
    // Réglages d'adressage que nous possédons ; le reste de l'interface (match, set-name, mtu, optional…) n'est jamais touché.
    static readonly HashSet<string> Managed = ["dhcp4", "dhcp6", "accept-ra", "addresses", "routes", "gateway4", "gateway6", "nameservers", "dhcp4-overrides", "dhcp6-overrides", "link-local"];

    [GeneratedRegex(@"^(\s*)([A-Za-z0-9_.-]+):(\s.*)?$")]
    private static partial Regex KeyLine();

    static int Indent(string line) => line.Length - line.TrimStart(' ').Length;
    static bool Blank(string line) => line.Trim().Length == 0 || line.TrimStart().StartsWith('#');

    /// <summary>Section de netplan (ethernets, bridges…) qui définit l'interface dans ce texte, ou null si elle n'y est pas.</summary>
    public static string? SectionOf(string yaml, string iface)
    {
        string? section = null;
        foreach (var line in yaml.Replace("\r", "").Split('\n'))
        {
            if (Blank(line)) continue;
            var m = KeyLine().Match(line);
            if (!m.Success) continue;
            var indent = m.Groups[1].Length;
            var key = m.Groups[2].Value;
            if (indent == 2) section = key;
            else if (indent == 4 && key == iface) return section;
        }
        return null;
    }

    /// <summary>
    /// Retire les réglages d'adressage de l'interface (dhcp4, addresses, routes, nameservers…), avec leurs lignes imbriquées.
    /// Lève <see cref="OperationException"/> si l'interface est écrite en style « flux » ({…}), que l'on ne sait pas modifier sans risque.
    /// </summary>
    public static string StripIface(string yaml, string iface)
    {
        var lines = yaml.Replace("\r", "").Split('\n').ToList();
        var output = new List<string>();
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            var m = KeyLine().Match(line);
            if (m.Success && m.Groups[1].Length == 4 && m.Groups[2].Value == iface)
            {
                var rest = m.Groups[3].Value.Trim();
                if (rest.StartsWith('{') || rest.StartsWith('[') )
                    throw new OperationException(Msg.F("The network configuration of {0} is written in a style UrsaNAS cannot edit safely.", iface));
                output.Add(line);
                i++;
                // Bloc de l'interface : lignes plus indentées que « iface: ».
                var childIndent = -1;
                while (i < lines.Count && (Blank(lines[i]) || Indent(lines[i]) > 4))
                {
                    var l = lines[i];
                    if (Blank(l)) { output.Add(l); i++; continue; }
                    if (childIndent < 0) childIndent = Indent(l);
                    var km = KeyLine().Match(l);
                    if (Indent(l) == childIndent && km.Success && Managed.Contains(km.Groups[2].Value))
                    {
                        i++;
                        // ses lignes imbriquées, ou les éléments « - x » alignés sur la clé
                        while (i < lines.Count && (Blank(lines[i]) || Indent(lines[i]) > childIndent
                               || (Indent(lines[i]) == childIndent && lines[i].TrimStart().StartsWith("- ", StringComparison.Ordinal)))) i++;
                        continue;
                    }
                    output.Add(l);
                    i++;
                }
                continue;
            }
            output.Add(line);
            i++;
        }
        return string.Join("\n", output);
    }

    /// <summary>Fichier d'UrsaNAS pour l'interface : tout l'adressage, que netplan fusionne avec le reste de la définition.</summary>
    public static string Generate(NetEditRequest r, string section, string topExtra = "", string ifaceExtra = "")
    {
        var sb = new StringBuilder();
        sb.Append("# Written by UrsaNAS. Edit the addressing from the Network page.\nnetwork:\n  version: 2\n").Append(topExtra)
            .Append("  ").Append(section).Append(":\n    ").Append(r.Iface).Append(":\n").Append(ifaceExtra);
        var v4 = r.Ipv4;
        var v6 = r.Ipv6;
        sb.Append("      dhcp4: ").Append(v4.Mode == "auto" ? "true" : "false").Append('\n');
        sb.Append("      dhcp6: ").Append(v6.Mode == "auto" ? "true" : "false").Append('\n');
        sb.Append("      accept-ra: ").Append(v6.Mode == "auto" ? "true" : "false").Append('\n');
        if (v4.Mode == "auto" && v4.DnsMode == "manual") sb.Append("      dhcp4-overrides:\n        use-dns: false\n");
        if (v6.Mode == "auto" && v6.DnsMode == "manual") sb.Append("      dhcp6-overrides:\n        use-dns: false\n");

        var addresses = new List<string>();
        if (v4.Mode == "manual") addresses.Add($"{v4.Address}/{NetRules.PrefixOf4(v4.Netmask)}");
        if (v6.Mode == "manual") addresses.Add($"{v6.Address}/{v6.Netmask}");
        if (addresses.Count > 0) sb.Append("      addresses: [").Append(string.Join(", ", addresses)).Append("]\n");

        var routes = new List<(string To, string Via)>();
        if (v4.Mode == "manual" && !string.IsNullOrWhiteSpace(v4.Gateway)) routes.Add(("0.0.0.0/0", v4.Gateway!));
        if (v6.Mode == "manual" && !string.IsNullOrWhiteSpace(v6.Gateway)) routes.Add(("\"::/0\"", v6.Gateway!));
        if (routes.Count > 0)
        {
            sb.Append("      routes:\n");
            foreach (var (to, via) in routes) sb.Append("        - to: ").Append(to).Append("\n          via: ").Append(via).Append('\n');
        }

        var dns = new List<string>();
        if (v4.DnsMode == "manual") dns.AddRange(v4.Dns);
        if (v6.DnsMode == "manual") dns.AddRange(v6.Dns);
        if (dns.Count > 0) sb.Append("      nameservers:\n        addresses: [").Append(string.Join(", ", dns)).Append("]\n");
        return sb.ToString();
    }

    /// <summary>
    /// Pont Open vSwitch <c>bridge.Iface</c> qui porte l'adressage, avec la carte <paramref name="port"/> comme sortie. Le pont reprend l'adresse MAC de la carte
    /// (le bail DHCP et les réservations du routeur restent valables). La carte n'a plus d'adressage : elle n'est qu'un port.
    /// </summary>
    public static string GenerateOvsBridge(NetEditRequest bridge, string port, string? mac)
        => Generate(bridge, "bridges",
            topExtra: $"  ethernets:\n    {port}:\n      dhcp4: false\n      dhcp6: false\n      accept-ra: false\n",
            ifaceExtra: $"      openvswitch: {{}}\n      interfaces: [{port}]\n" + (string.IsNullOrEmpty(mac) ? "" : $"      macaddress: \"{mac}\"\n"));

    public static string OvsFileName(string bridge) => $"90-ursanas-ovs-{bridge}.yaml";
}

public static partial class IfupdownConfig
{
    static readonly string[] StanzaStarts = ["auto", "allow-", "iface", "mapping", "source", "source-directory", "no-auto-down", "no-scripts", "template"];

    static bool StartsStanza(string line)
    {
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#') return false;
        var word = line.Split(' ', '\t')[0];
        return StanzaStarts.Any(s => word == s || (s.EndsWith('-') && word.StartsWith(s, StringComparison.Ordinal)));
    }

    /// <summary>Retire les blocs « iface &lt;nom&gt; inet|inet6 … » de l'interface, avec leurs options. « auto » et « allow-hotplug » restent.</summary>
    public static string Strip(string text, string iface)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var output = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var parts = lines[i].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (!lines[i].StartsWith(' ') && parts.Length >= 3 && parts[0] == "iface" && parts[1] == iface)
            {
                // le bloc se termine au prochain mot-clé de bloc (les options sont les lignes qui suivent)
                while (i + 1 < lines.Length && !StartsStanza(lines[i + 1])) i++;
                continue;
            }
            output.Add(lines[i]);
        }
        return string.Join("\n", output);
    }

    /// <summary>L'interface est-elle démarrée au boot par ce texte (« auto » ou « allow-hotplug ») ?</summary>
    public static bool HasAuto(string text, string iface) => text.Replace("\r", "").Split('\n').Any(l =>
    {
        var p = l.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return p.Length >= 2 && !l.StartsWith(' ') && (p[0] == "auto" || p[0] == "allow-hotplug") && p.Skip(1).Contains(iface);
    });

    /// <summary>Blocs ifupdown de l'interface. Le DNS manuel passe par <c>dns-nameservers</c> (pris en charge avec resolvconf ou systemd-resolved).</summary>
    public static string Generate(NetEditRequest r, bool addAuto, string afterAuto = "", string v4Options = "")
    {
        var sb = new StringBuilder("# Written by UrsaNAS. Edit the addressing from the Network page.\n");
        if (addAuto) sb.Append("auto ").Append(r.Iface).Append('\n');
        sb.Append(afterAuto);
        var v4 = r.Ipv4;
        if (v4.Mode == "auto") sb.Append("iface ").Append(r.Iface).Append(" inet dhcp\n").Append(v4Options);
        else
        {
            sb.Append("iface ").Append(r.Iface).Append(" inet static\n").Append(v4Options).Append("    address ").Append(v4.Address).Append('/').Append(NetRules.PrefixOf4(v4.Netmask)).Append('\n');
            if (!string.IsNullOrWhiteSpace(v4.Gateway)) sb.Append("    gateway ").Append(v4.Gateway).Append('\n');
            if (v4.DnsMode == "manual") sb.Append("    dns-nameservers ").Append(string.Join(' ', v4.Dns)).Append('\n');
        }
        var v6 = r.Ipv6;
        if (v6.Mode == "auto") sb.Append("iface ").Append(r.Iface).Append(" inet6 auto\n");
        else
        {
            sb.Append("iface ").Append(r.Iface).Append(" inet6 static\n    address ").Append(v6.Address).Append('/').Append(v6.Netmask).Append('\n');
            if (!string.IsNullOrWhiteSpace(v6.Gateway)) sb.Append("    gateway ").Append(v6.Gateway).Append('\n');
            if (v6.DnsMode == "manual") sb.Append("    dns-nameservers ").Append(string.Join(' ', v6.Dns)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Pont Open vSwitch <c>bridge.Iface</c> avec la carte <paramref name="port"/> pour sortie (paquet openvswitch-switch : options <c>ovs_*</c> d'ifupdown).
    /// Le bloc est encadré de repères pour qu'UrsaNAS le retrouve et le retire, où qu'il soit écrit.
    /// </summary>
    public static string GenerateOvsBridge(NetEditRequest bridge, string port, string? mac)
    {
        var options = $"    ovs_type OVSBridge\n    ovs_ports {port}\n" + (string.IsNullOrEmpty(mac) ? "" : $"    ovs_extra set bridge ${{IFACE}} other-config:hwaddr={mac}\n");
        var b = bridge.Iface;
        return $"# ursanas-ovs {b} {port}\n"
            + Generate(bridge, addAuto: true, afterAuto: $"allow-ovs {b}\n", v4Options: options)
            + $"\nallow-{b} {port}\niface {port} inet manual\n    ovs_bridge {b}\n    ovs_type OVSPort\n# end ursanas-ovs {b}\n";
    }

    /// <summary>Les montages Open vSwitch d'UrsaNAS écrits dans ce texte : (pont, carte).</summary>
    public static IReadOnlyList<(string Bridge, string Port)> FindOvs(string text)
        => text.Replace("\r", "").Split('\n').Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 4 && p[0] == "#" && p[1] == "ursanas-ovs").Select(p => (p[2], p[3])).ToList();

    /// <summary>Retire le bloc écrit par <see cref="GenerateOvsBridge"/> (repère de début à repère de fin).</summary>
    public static string RemoveOvs(string text, string bridge)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var output = new List<string>();
        var skipping = false;
        foreach (var l in lines)
        {
            if (!skipping && l.StartsWith($"# ursanas-ovs {bridge} ", StringComparison.Ordinal)) { skipping = true; continue; }
            if (skipping) { if (l == $"# end ursanas-ovs {bridge}") skipping = false; continue; }
            output.Add(l);
        }
        return string.Join("\n", output);
    }

    /// <summary>Contenu de /etc/resolv.conf pour un DNS manuel (ifupdown seul ne le fait pas sans resolvconf).</summary>
    public static string ResolvConf(NetEditRequest r)
    {
        var dns = new List<string>();
        if (r.Ipv4.DnsMode == "manual") dns.AddRange(r.Ipv4.Dns);
        if (r.Ipv6.DnsMode == "manual") dns.AddRange(r.Ipv6.Dns);
        return "# Written by UrsaNAS\n" + string.Concat(dns.Select(d => $"nameserver {d}\n"));
    }
}
