using System.Text.Json;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Relit les réglages d'un conteneur dans la sortie de « docker inspect » (conteneur et image). Seuls les réglages que le formulaire
/// de lancement sait exprimer sont repris ; tout ce qu'il ne saurait pas recréer est listé dans <see cref="Unsupported"/>, et
/// l'agent refuse alors de recréer le conteneur plutôt que de le recréer amputé.
/// </summary>
public static class DockerInspect
{
    static JsonElement? P(JsonElement e, params string[] path)
    {
        foreach (var name in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.Null ? null : e;
    }

    static string Str(JsonElement e, params string[] path) => P(e, path) is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? "" : "";
    static long Num(JsonElement e, params string[] path) => P(e, path) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var n) ? n : 0;
    static bool Flag(JsonElement e, params string[] path) => P(e, path) is { ValueKind: JsonValueKind.True };

    static IEnumerable<JsonElement> Items(JsonElement e, params string[] path) =>
        P(e, path) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];

    static bool NonEmpty(JsonElement e, params string[] path) => P(e, path) switch
    {
        { ValueKind: JsonValueKind.Array } a => a.GetArrayLength() > 0,
        { ValueKind: JsonValueKind.Object } o => o.EnumerateObject().Any(),
        { ValueKind: JsonValueKind.String } s => s.GetString()?.Length > 0,
        _ => false,
    };

    public static string Name(JsonElement container) => Str(container, "Name").TrimStart('/');
    public static bool Running(JsonElement container) => Flag(container, "State", "Running");
    public static string? Stack(JsonElement container) => Str(container, "Config", "Labels", "com.docker.compose.project") is { Length: > 0 } s ? s : null;

    public static RunContainerRequest Settings(JsonElement c, JsonElement image)
    {
        var restart = Str(c, "HostConfig", "RestartPolicy", "Name");
        if (!Rules.RestartPolicies.Contains(restart)) restart = "no";

        var ports = new List<string>();
        if (P(c, "HostConfig", "PortBindings") is { ValueKind: JsonValueKind.Object } bindings)
            foreach (var b in bindings.EnumerateObject())
            {
                var parts = b.Name.Split('/');
                var proto = parts.Length > 1 && parts[1] == "udp" ? "/udp" : "";
                if (b.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var one in b.Value.EnumerateArray())
                {
                    var host = Str(one, "HostPort");
                    if (host.Length == 0) continue;
                    var ip = Str(one, "HostIp");
                    var prefix = ip is "" or "0.0.0.0" or "::" || ip.Contains(':') ? "" : ip + ":"; // seules les adresses IPv4 précises sont reprises
                    ports.Add($"{prefix}{host}:{parts[0]}{proto}");
                }
            }

        var imageEnv = Items(image, "Config", "Env").Select(e => e.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
        var env = Items(c, "Config", "Env").Select(e => e.GetString() ?? "").Where(e => e.Length > 0 && !imageEnv.Contains(e)).ToList();

        var volumes = new List<string>();
        foreach (var m in Items(c, "Mounts"))
        {
            var type = Str(m, "Type");
            var source = type == "volume" ? Str(m, "Name") : type == "bind" ? Str(m, "Source") : "";
            if (source.Length == 0) continue;
            volumes.Add($"{source}:{Str(m, "Destination")}" + (Flag(m, "RW") ? "" : ":ro"));
        }

        var networks = P(c, "NetworkSettings", "Networks") is { ValueKind: JsonValueKind.Object } n ? n.EnumerateObject().Select(x => x.Name).ToList() : [];
        var network = networks.FirstOrDefault(x => x != "bridge") ?? "";

        return new RunContainerRequest(Str(c, "Config", "Image"), Name(c), restart, ports, env, volumes, network);
    }

    /// <summary>Réglages présents sur le conteneur que le formulaire ne saurait pas recréer (textes courts, traduits par l'interface).</summary>
    public static IReadOnlyList<string> Unsupported(JsonElement c, JsonElement image)
    {
        var list = new List<string>();
        void Add(bool condition, string what) { if (condition && !list.Contains(what)) list.Add(what); }

        Add(Flag(c, "HostConfig", "Privileged"), "privileged mode");
        Add(NonEmpty(c, "HostConfig", "Devices"), "devices");
        Add(NonEmpty(c, "HostConfig", "CapAdd") || NonEmpty(c, "HostConfig", "CapDrop"), "added or removed capabilities");
        Add(Num(c, "HostConfig", "Memory") > 0 || Num(c, "HostConfig", "NanoCpus") > 0 || Num(c, "HostConfig", "CpuShares") > 0, "memory or CPU limits");
        Add(NonEmpty(c, "HostConfig", "Dns") || NonEmpty(c, "HostConfig", "ExtraHosts"), "custom DNS or hosts");
        Add(NonEmpty(c, "HostConfig", "Sysctls"), "kernel parameters");
        Add(NonEmpty(c, "HostConfig", "PidMode") || Str(c, "HostConfig", "NetworkMode").StartsWith("container:", StringComparison.Ordinal), "shared process or network space");
        Add(Items(c, "Mounts").Any(m => Str(m, "Type") is not ("bind" or "volume")), "temporary filesystems");
        Add(P(c, "NetworkSettings", "Networks") is { ValueKind: JsonValueKind.Object } n && n.EnumerateObject().Count() > 1, "several networks");
        Add(Raw(c, "Config", "Cmd") != Raw(image, "Config", "Cmd") || Raw(c, "Config", "Entrypoint") != Raw(image, "Config", "Entrypoint"), "custom command");
        Add(Str(c, "Config", "User") != Str(image, "Config", "User"), "custom user");
        Add(Str(c, "Config", "WorkingDir") != Str(image, "Config", "WorkingDir"), "custom working folder");
        return list;
    }

    static string Raw(JsonElement e, params string[] path) => P(e, path)?.GetRawText() ?? "null";
}
