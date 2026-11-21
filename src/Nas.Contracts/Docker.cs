using System.Text.RegularExpressions;

namespace Nas.Contracts;

/// <summary>NotInstalled : pas de commande docker. Stopped : installé mais le service ne répond pas. Ready : utilisable.</summary>
public enum DockerState { NotInstalled, Stopped, Ready }

/// <summary><c>Version</c> : version du moteur ; <c>ComposeVersion</c> : null si le plugin « docker compose » est absent.</summary>
public sealed record DockerStatus(DockerState State, string? Version, string? ComposeVersion);

/// <summary><c>State</c> : running, exited, paused… <c>Project</c> : nom de la stack Compose, s'il y en a une.</summary>
public sealed record ContainerInfo(string Id, string Name, string Image, string State, string Status, string Ports, string? Project,
    IReadOnlyList<ContainerNet>? Networks = null);

/// <summary>Un réseau où le conteneur est branché, avec ses adresses (vides tant que le conteneur est arrêté) — « docker inspect ».</summary>
public sealed record ContainerNet(string Network, string? Ip, string? Ipv6, string? Gateway, string? Mac);

public sealed record ContainerStats(string Id, double CpuPercent, long MemUsedBytes, long MemLimitBytes);

public sealed record ImageInfo(string Id, string Repository, string Tag, long SizeBytes, string Created);

public sealed record DockerVolume(string Name, string Driver, string Mountpoint);

/// <summary>
/// Réseau Docker. Les champs après <c>Scope</c> viennent de « docker network inspect » : sous-réseaux et passerelles (séparés par « , »
/// s'il y en a plusieurs), plage d'adresses, IPv6, noms des conteneurs connectés, interface parente et mode (macvlan / ipvlan).
/// </summary>
public sealed record DockerNetwork(string Id, string Name, string Driver, string Scope, string? Subnet = null, string? Gateway = null,
    string? IpRange = null, bool Ipv6 = false, IReadOnlyList<string>? Containers = null, string? Parent = null, string? Mode = null);

/// <summary><c>Driver</c> : bridge, macvlan ou ipvlan. <c>Parent</c> (macvlan, ipvlan) : interface du NAS où brancher le réseau. <c>Mode</c> : voir <see cref="Rules.NetworkModes"/>.</summary>
public sealed record CreateNetworkRequest(string Name, string Driver = "bridge", string? Subnet = null, string? Gateway = null,
    string? IpRange = null, bool Ipv6 = false, string? Parent = null, string? Mode = null);

/// <summary><c>Action</c> : start, stop ou restart (le service systemd « docker »).</summary>
public sealed record DockerServiceRequest(string Action);

/// <summary>Une stack Compose enregistrée sur le NAS ; <c>Running</c>/<c>Total</c> comptent ses conteneurs.</summary>
public sealed record StackInfo(string Name, int Running, int Total);

public sealed record StackContent(string Name, string Content);

/// <summary>Ports « 8080:80 », variables « CLE=valeur », volumes « /hote:/conteneur[:ro] » ou « volume:/conteneur[:ro] ».</summary>
/// <summary><c>Network</c> : réseau Docker où brancher le conteneur ; créé s'il n'existe pas. Vide : réseau par défaut.</summary>
/// <remarks>
/// <c>Privileged</c> : « --privileged » (tous les droits, accès à tous les périphériques). <c>CapAdd</c> et <c>CapDrop</c> : « --cap-add » et « --cap-drop »
/// (noms sans « CAP_ », ou ALL) par rapport aux droits que Docker accorde d'office (<see cref="Rules.DefaultCapabilities"/>).
/// </remarks>
public sealed record RunContainerRequest(string Image, string? Name, string Restart, IReadOnlyList<string> Ports,
    IReadOnlyList<string> Env, IReadOnlyList<string> Volumes, string? Network = null,
    bool Privileged = false, IReadOnlyList<string>? CapAdd = null, IReadOnlyList<string>? CapDrop = null);

/// <summary>
/// Réglages actuels d'un conteneur, relus par « docker inspect » (variables de l'image exclues). <c>Stack</c> : stack Compose
/// qui le gère, s'il y en a une. <c>Unsupported</c> : réglages que le formulaire ne sait pas conserver si le conteneur est recréé.
/// </summary>
public sealed record ContainerSettings(RunContainerRequest Settings, bool Running, string? Stack, IReadOnlyList<string> Unsupported);

public sealed record PullImageRequest(string Image);
public sealed record DockerNameRequest(string Name);
public sealed record SaveStackRequest(string Content);
public sealed record ShareRoot(string Share, string Path);

public static partial class Rules
{
    public static readonly IReadOnlyList<string> RestartPolicies = ["no", "unless-stopped", "always", "on-failure"];
    public const int MaxStackBytes = 100 * 1024;

    /// <summary>Les droits (« capabilities ») que Docker accorde d'office à un conteneur non privilégié.</summary>
    public static readonly IReadOnlyList<string> DefaultCapabilities =
    [
        "AUDIT_WRITE", "CHOWN", "DAC_OVERRIDE", "FOWNER", "FSETID", "KILL", "MKNOD", "NET_BIND_SERVICE", "NET_RAW", "SETFCAP", "SETGID", "SETPCAP", "SETUID", "SYS_CHROOT",
    ];

    /// <summary>Tous les droits Linux que « --cap-add » et « --cap-drop » acceptent (documentation « Runtime privilege and Linux capabilities » de Docker).</summary>
    public static readonly IReadOnlyList<string> AllCapabilities =
    [
        "AUDIT_CONTROL", "AUDIT_READ", "AUDIT_WRITE", "BLOCK_SUSPEND", "BPF", "CHECKPOINT_RESTORE", "CHOWN", "DAC_OVERRIDE", "DAC_READ_SEARCH", "FOWNER", "FSETID",
        "IPC_LOCK", "IPC_OWNER", "KILL", "LEASE", "LINUX_IMMUTABLE", "MAC_ADMIN", "MAC_OVERRIDE", "MKNOD", "NET_ADMIN", "NET_BIND_SERVICE", "NET_BROADCAST", "NET_RAW",
        "PERFMON", "SETFCAP", "SETGID", "SETPCAP", "SETUID", "SYSLOG", "SYS_ADMIN", "SYS_BOOT", "SYS_CHROOT", "SYS_MODULE", "SYS_NICE", "SYS_PACCT", "SYS_PTRACE",
        "SYS_RAWIO", "SYS_RESOURCE", "SYS_TIME", "SYS_TTY_CONFIG", "WAKE_ALARM",
    ];

    /// <summary>Les droits réellement accordés : tout si privilégié ; sinon ceux d'office, moins <c>CapDrop</c> (ALL = tous), plus <c>CapAdd</c> (ALL = tous).</summary>
    public static IReadOnlySet<string> EffectiveCapabilities(bool privileged, IEnumerable<string>? add, IEnumerable<string>? drop)
    {
        var all = AllCapabilities.ToHashSet();
        if (privileged || (add ?? []).Contains("ALL")) return all;
        var set = DefaultCapabilities.ToHashSet();
        if ((drop ?? []).Contains("ALL")) set.Clear();
        else set.ExceptWith(drop ?? []);
        set.UnionWith(add ?? []);
        return set;
    }

    public static string? ValidateCapabilities(IEnumerable<string>? list) =>
        (list ?? []).FirstOrDefault(c => c != "ALL" && !AllCapabilities.Contains(c)) is { } bad ? Msg.F("Unknown capability “{0}”.", bad) : null;

    // 64 caractères : un volume anonyme porte un identifiant hexadécimal de 64 caractères.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex DockerNameRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex StackNameRegex();

    // Référence d'image : [registre[:port]/]chemin[:étiquette][@sha256:…]. Ne commence jamais par « - » (ce serait une option).
    [GeneratedRegex("^[a-z0-9][A-Za-z0-9._/:@-]{0,254}$")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"^(\d{1,3}(\.\d{1,3}){3}:)?\d{1,5}(-\d{1,5})?:\d{1,5}(-\d{1,5})?(/(tcp|udp))?$")]
    private static partial Regex PortRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,127}=")]
    private static partial Regex EnvRegex();

    /// <summary>Nom de conteneur, de volume, de réseau ou identifiant (hexadécimal) : lettres, chiffres, _ . -</summary>
    public static string? ValidateDockerName(string? name) =>
        string.IsNullOrEmpty(name) || !DockerNameRegex().IsMatch(name)
            ? Msg.F("Invalid name: 1 to 64 characters (letters, digits, _ . and -), starting with a letter or a digit.")
            : null;

    public static readonly IReadOnlyList<string> NetworkDrivers = ["bridge", "macvlan", "ipvlan"];

    /// <summary>Modes permis par pilote ; le premier est celui de Docker par défaut.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NetworkModes = new Dictionary<string, IReadOnlyList<string>>
    {
        ["macvlan"] = ["bridge", "private", "vepa", "passthru"],
        ["ipvlan"] = ["l2", "l3"],
    };

    // Nom d'interface Linux (15 caractères au plus ; « eth0.10 » pour un VLAN) ; ne commence jamais par « - ».
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.@-]{0,14}$")]
    private static partial Regex InterfaceRegex();

    static bool IsCidr(string s, bool allowV6) =>
        System.Net.IPNetwork.TryParse(s, out var n) && s.Contains('/')
        && (n.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork || (allowV6 && n.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6));

    /// <summary>Contrôle la création d'un réseau : pilote, interface parente et mode, sous-réseau / passerelle / plage en notation CIDR ou IP.</summary>
    public static string? ValidateCreateNetwork(CreateNetworkRequest r)
    {
        if (ValidateDockerName(r.Name) is { } e0) return e0;
        if (!NetworkDrivers.Contains(r.Driver)) return Msg.F("Unknown network driver.");
        if (r.Driver != "bridge")
        {
            if (string.IsNullOrEmpty(r.Parent) || !InterfaceRegex().IsMatch(r.Parent)) return Msg.F("Choose the network interface the network is attached to.");
            if (!NetworkModes[r.Driver].Contains(r.Mode ?? "")) return Msg.F("Unknown network mode.");
        }
        else if (!string.IsNullOrEmpty(r.Parent) || !string.IsNullOrEmpty(r.Mode)) return Msg.F("A bridge network has no interface or mode.");
        var subnet = r.Subnet?.Trim();
        var gateway = r.Gateway?.Trim();
        var range = r.IpRange?.Trim();
        if (!string.IsNullOrEmpty(subnet) && !IsCidr(subnet, true)) return Msg.F("Invalid subnet “{0}”: expected 172.20.0.0/16.", subnet);
        if (!string.IsNullOrEmpty(gateway) && !System.Net.IPAddress.TryParse(gateway, out _)) return Msg.F("Invalid gateway “{0}”.", gateway);
        if (!string.IsNullOrEmpty(range) && !IsCidr(range, true)) return Msg.F("Invalid IP range “{0}”: expected 172.20.10.0/24.", range);
        if ((!string.IsNullOrEmpty(gateway) || !string.IsNullOrEmpty(range)) && string.IsNullOrEmpty(subnet)) return Msg.F("A gateway or an IP range needs a subnet.");
        if (r.Driver != "bridge" && string.IsNullOrEmpty(subnet)) return Msg.F("A macvlan or ipvlan network needs a subnet.");
        return null;
    }

    public static string? ValidateStackName(string? name) =>
        string.IsNullOrEmpty(name) || !StackNameRegex().IsMatch(name)
            ? Msg.F("Invalid stack name: 1 to 40 characters (lowercase letters, digits, _ and -), starting with a letter or a digit.")
            : null;

    public static string? ValidateImageRef(string? image) =>
        string.IsNullOrEmpty(image) || !ImageRegex().IsMatch(image)
            ? Msg.F("Invalid image name (for example nginx:latest).")
            : null;

    public static string? ValidatePort(string? p) =>
        string.IsNullOrEmpty(p) || !PortRegex().IsMatch(p)
            ? Msg.F("Invalid port “{0}”: expected 8080:80 (or 8080:80/udp).", p ?? "")
            : null;

    public static string? ValidateEnv(string? e) =>
        string.IsNullOrEmpty(e) || !EnvRegex().IsMatch(e) || e.Any(c => c is '\n' or '\r' or '\0')
            ? Msg.F("Invalid variable “{0}”: expected NAME=value.", e ?? "")
            : null;

    /// <summary>« /hôte:/conteneur[:ro|rw] » (dossier du NAS) ou « nom:/conteneur[:ro|rw] » (volume Docker).</summary>
    public static string? ValidateVolumeMount(string? v)
    {
        var bad = Msg.F("Invalid volume “{0}”: expected /folder:/path/in/container (or volume:/path/in/container).", v ?? "");
        if (string.IsNullOrEmpty(v) || v.Length > 500 || v.Any(c => char.IsControl(c) || c == ',')) return bad;
        var parts = v.Split(':');
        if (parts.Length is < 2 or > 3) return bad;
        if (parts.Length == 3 && parts[2] is not ("ro" or "rw")) return bad;
        if (!parts[1].StartsWith('/')) return bad;
        var host = parts[0];
        if (host.StartsWith('/')) return host.Split('/').Contains("..") ? bad : null;
        return DockerNameRegex().IsMatch(host) ? null : bad;
    }

    public static string? ValidateRunContainer(RunContainerRequest r)
    {
        if (ValidateImageRef(r.Image) is { } e1) return e1;
        if (!string.IsNullOrEmpty(r.Name) && ValidateDockerName(r.Name) is { } e2) return e2;
        if (!string.IsNullOrEmpty(r.Network) && ValidateDockerName(r.Network) is { } e3) return e3;
        if (!RestartPolicies.Contains(r.Restart)) return Msg.F("Unknown restart policy.");
        if ((r.Ports?.Count ?? 0) + (r.Env?.Count ?? 0) + (r.Volumes?.Count ?? 0) > 100) return Msg.F("Too many ports, variables or volumes.");
        if (ValidateCapabilities(r.CapAdd) is { } c1) return c1;
        if (ValidateCapabilities(r.CapDrop) is { } c2) return c2;
        foreach (var p in r.Ports ?? []) if (ValidatePort(p) is { } e) return e;
        foreach (var x in r.Env ?? []) if (ValidateEnv(x) is { } e) return e;
        foreach (var v in r.Volumes ?? []) if (ValidateVolumeMount(v) is { } e) return e;
        return null;
    }

    public static string? ValidateStackContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return Msg.F("The compose file is empty.");
        if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxStackBytes) return Msg.F("The compose file is too large (100 KB at most).");
        if (content.Contains('\0')) return Msg.F("The compose file contains invalid characters.");
        return null;
    }
}
