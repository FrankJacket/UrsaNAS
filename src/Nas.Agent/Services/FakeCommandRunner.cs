using System.Globalization;

namespace Nas.Agent.Services;

/// <summary>
/// Remplace les commandes système (URSANAS_FAKE=1) pour développer l'interface sans Linux : utilisateurs, groupes, partages et
/// permissions vivent en mémoire. Les vrais services tournent tels quels au-dessus ; seules les commandes sont simulées.
/// </summary>
public sealed class FakeCommandRunner : ICommandRunner
{
    readonly object _lock = new();
    readonly List<string> _users = ["alice", "bob", "carol"];
    bool _service; // le compte de service n'est pas dans nasusers : il ne figure pas dans la liste des utilisateurs
    readonly Dictionary<string, List<string>> _groups = new() { ["nasusers"] = [], ["nasg-staff"] = ["alice", "bob"] };
    readonly string _shares = Path.Combine(Path.GetTempPath(), "ursanas-fake-shares");
    readonly FakeVirt _virt = new(); // machines virtuelles simulées (libvirt)

    static CommandResult Ok(string output = "") => new(0, output, "");
    static CommandResult Fail(string err = "") => new(1, "", err);

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult(Run(fileName, args));
    }

    int Gid(string group) => 900 + _groups.Keys.ToList().IndexOf(group);

    CommandResult Run(string cmd, IReadOnlyList<string> a)
    {
        switch (cmd)
        {
            case "getent" when a[0] == "group" && a.Count == 1:
                return Ok(string.Concat(_groups.Select(g => $"{g.Key}:x:{Gid(g.Key)}:{string.Join(',', g.Value)}\n")));
            case "getent" when a[0] == "group":
                return _groups.TryGetValue(a[1], out var m) ? Ok($"{a[1]}:x:{Gid(a[1])}:{string.Join(',', m)}\n") : new CommandResult(2, "", "");
            case "getent" when a[0] == "passwd":
                return Ok(string.Concat(_users.Select((u, i) => $"{u}:x:{1000 + i}:{Gid("nasusers")}::/nonexistent:/usr/sbin/nologin\n")));
            case "id":
                return _users.Contains(a[^1]) || (_service && a[^1] == AclService.ServiceUser) ? Ok("1000") : Fail();
            case "useradd":
                if (a[^1] == AclService.ServiceUser) _service = true; else _users.Add(a[^1]);
                return Ok();
            case "userdel":
                _users.Remove(a[^1]);
                return Ok();
            case "groupadd":
                _groups[a[^1]] = [];
                return Ok();
            case "groupdel":
                _groups.Remove(a[^1]);
                return Ok();
            case "gpasswd":
                _groups[a[^1]] = a[1].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                return Ok();
            case "pdbedit":
                var i = _users.IndexOf(a[1]);
                return i < 0 && a[1] != AclService.ServiceUser ? Fail() : Ok($"Unix username:  {a[1]}\nUser SID:       S-1-5-21-1-2-3-{1000 + Math.Max(i, 0) + (i < 0 ? 99 : 0)}\n");
            case "testparm" when a.Contains("--parameter-name=vfs objects"):
                return Ok("acl_xattr\n"); // en mode simulé, tous les partages ont les permissions avancées
            case "testparm" when a.Any(x => x.StartsWith("--section-name=", StringComparison.Ordinal)):
                return Ok(ShareDir(a.First(x => x.StartsWith("--section-name=", StringComparison.Ordinal))["--section-name=".Length..]) + "\n");
            case "smbclient":
                var share = a[0].Split('/').Last();
                var dir = Path.Combine(ShareDir(share), a[^1]["mkdir \"".Length..^1].TrimStart('/'));
                if (Directory.Exists(dir)) return Ok("NT_STATUS_OBJECT_NAME_COLLISION making remote directory");
                Directory.CreateDirectory(dir);
                return Ok();
            case "virsh":
                return _virt.Virsh(a);
            case "virt-install":
                return _virt.Install(a);
            case "qemu-img":
                return _virt.QemuImg(a);
            case "docker-missing": // Docker:Binary=docker-missing simule une machine sans Docker
                throw new System.ComponentModel.Win32Exception(2);
            case "docker":
                return FakeDocker(a);
            case "systemctl":
                return FakeSystemctl(a);
            default:
                return Ok(); // chgrp, chmod, smbpasswd, smbcontrol, smbcacls, testparm…
        }
    }

    // ---- Docker simulé : quelques conteneurs, images, volumes et réseaux qui réagissent aux commandes.
    sealed record FakeContainer(string Id, string Name, string Image, string State, string Ports, string? Project, string Net = "bridge");
    readonly List<FakeContainer> _containers =
    [
        new("a1b2c3d4e5f6", "jellyfin", "jellyfin/jellyfin:latest", "running", "0.0.0.0:8096->8096/tcp", null),
        new("b2c3d4e5f6a1", "old-test", "alpine:3.20", "exited", "", null),
    ];
    readonly List<string> _images = ["jellyfin/jellyfin:latest", "alpine:3.20", "nginx:latest"];
    readonly List<string> _volumes = ["jellyfin-config"];
    sealed record FakeNet(string Name, string Driver, string? Subnet = null, string? Gateway = null, string? Range = null, bool Ipv6 = false, string? Parent = null, string? Mode = null)
    {
        public string Short => Math.Abs(Name.GetHashCode()).ToString("x8");
    }
    readonly List<FakeNet> _networks =
    [
        new("bridge", "bridge", "172.17.0.0/16", "172.17.0.1"), new("host", "host"), new("none", "null"),
        new("media", "bridge", "172.20.0.0/16", "172.20.0.1"),
    ];
    bool _dockerDown; // « systemctl stop docker » : le démon ne répond plus

    static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    CommandResult FakeSystemctl(IReadOnlyList<string> a)
    {
        if (!a.Contains("docker.service")) return Ok();
        _dockerDown = a[0] == "stop";
        return Ok();
    }

    CommandResult FakeDocker(IReadOnlyList<string> a)
    {
        if (_dockerDown) return Fail("Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?");
        switch (a[0])
        {
            case "version": return Ok("27.3.1\n");
            case "compose" when a[1] == "version": return Ok("2.29.7\n");
            case "compose":
                var project = a[2];
                var verb = a.Skip(3).First(x => x is "up" or "down" or "pull" or "config");
                if (verb == "config" && a.ToList().IndexOf("-f") is var fi and >= 0 && File.Exists(a[fi + 1]))
                {
                    // Contrôle minimal du fichier simulé : un vrai « docker compose config » en fait beaucoup plus.
                    var text = File.ReadAllText(a[fi + 1]);
                    if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"^services:\s*$", System.Text.RegularExpressions.RegexOptions.Multiline))
                        return Fail($"{a[fi + 1]}: services must be a mapping");
                    var unknown = System.Text.RegularExpressions.Regex.Match(text, @"^\s{4}(?!(?:image|ports|restart|environment|volumes|command|networks|container_name|depends_on|entrypoint):)(\w+):", System.Text.RegularExpressions.RegexOptions.Multiline);
                    if (unknown.Success) return Fail($"validating {a[fi + 1]}: services.app Additional property {unknown.Groups[1].Value} is not allowed");
                    if (a.Contains("--images"))
                        return Ok(string.Concat(System.Text.RegularExpressions.Regex.Matches(text, @"image:\s*(\S+)").Select(m => m.Groups[1].Value + "\n")));
                    return Ok();
                }
                if (verb == "up" && !_containers.Any(c => c.Project == project))
                    foreach (var svc in new[] { "web", "db" })
                        _containers.Add(new(Guid.NewGuid().ToString("N")[..12], $"{project}-{svc}-1", svc == "web" ? "nginx:latest" : "postgres:16", "running", "", project));
                if (verb == "down") _containers.RemoveAll(c => c.Project == project);
                return Ok();
            case "ps":
                return Ok(string.Concat(_containers.Select(c => Json(new
                {
                    ID = c.Id, Names = c.Name, c.Image, c.State, Status = c.State == "running" ? "Up 2 hours" : "Exited (0) 3 days ago", c.Ports,
                    Labels = c.Project is null ? "" : $"com.docker.compose.project={c.Project}",
                }) + "\n")));
            case "stats":
                return Ok(string.Concat(_containers.Where(c => c.State == "running").Select(c =>
                    Json(new { ID = c.Id, Name = c.Name, CPUPerc = "1.25%", MemUsage = "182.4MiB / 7.7GiB" }) + "\n")));
            case "logs":
                return Ok($"2026-01-01T10:00:00Z {a[^1]}: started\n2026-01-01T10:00:01Z {a[^1]}: listening\n");
            case "start" or "stop" or "restart":
                var i = _containers.FindIndex(c => c.Id == a[1] || c.Name == a[1]);
                if (i < 0) return Fail($"Error response from daemon: No such container: {a[1]}");
                _containers[i] = _containers[i] with { State = a[0] == "stop" ? "exited" : "running" };
                return Ok(a[1]);
            case "rm":
                var force = a.Contains("-f");
                var j = _containers.FindIndex(c => c.Id == a[^1] || c.Name == a[^1]);
                if (j < 0) return force ? Ok() : Fail($"Error response from daemon: No such container: {a[^1]}");
                if (_containers[j].State == "running" && !force) return Fail("Error response from daemon: cannot remove a running container");
                _containers.RemoveAt(j);
                return Ok(a[^1]);
            case "run" or "create":
                var name = a.Contains("--name") ? a[a.ToList().IndexOf("--name") + 1] : $"fake-{_containers.Count}";
                if (_containers.Any(c => c.Name == name)) return Fail($"Error response from daemon: Conflict. The container name \"/{name}\" is already in use");
                var net = a.ToList().IndexOf("--network") is var ni and >= 0 ? a[ni + 1] : "bridge";
                _containers.Add(new(Guid.NewGuid().ToString("N")[..12], name, a[^1], a[0] == "run" ? "running" : "created", "", null, net));
                return Ok();
            case "rename":
                var k = _containers.FindIndex(c => c.Id == a[1] || c.Name == a[1]);
                if (k < 0) return Fail($"Error response from daemon: No such container: {a[1]}");
                _containers[k] = _containers[k] with { Name = a[2] };
                return Ok();
            case "update": return Ok(a[^1]);
            case "inspect" when a.Count > 2: // plusieurs identifiants : seul le réseau nous intéresse (liste des conteneurs)
                return Ok(Json(_containers.Where(c => a.Skip(1).Contains(c.Id)).Select((c, i) => new
                {
                    Id = c.Id.PadRight(64, '0'),
                    NetworkSettings = new
                    {
                        Networks = new Dictionary<string, object>
                        {
                            [c.Net] = c.State == "running"
                                ? new { IPAddress = $"172.17.0.{i + 2}", GlobalIPv6Address = "", Gateway = "172.17.0.1", MacAddress = $"02:42:ac:11:00:{i + 2:x2}" }
                                : new { IPAddress = "", GlobalIPv6Address = "", Gateway = "", MacAddress = "" },
                        },
                    },
                }).ToList()) + "\n");
            case "inspect":
                var ic = _containers.FirstOrDefault(c => c.Id == a[^1] || c.Name == a[^1]);
                if (ic is null) return Fail($"Error: No such object: {a[^1]}");
                return Ok(Json(new
                {
                    Id = ic.Id.PadRight(64, '0'), Name = "/" + ic.Name, Image = "sha256:fake",
                    State = new { Running = ic.State == "running" },
                    Config = new { Image = ic.Image, Env = new[] { "PATH=/usr/bin", "TZ=UTC" }, Labels = ic.Project is null ? new Dictionary<string, string>() : new() { ["com.docker.compose.project"] = ic.Project } },
                    HostConfig = new
                    {
                        RestartPolicy = new { Name = "unless-stopped" },
                        PortBindings = ic.Ports.Length == 0 ? new Dictionary<string, object[]>() : new() { ["8096/tcp"] = [new { HostIp = "0.0.0.0", HostPort = "8096" }] },
                    },
                    Mounts = ic.Ports.Length == 0 ? [] : new[] { new { Type = "bind", Source = "/srv/media", Destination = "/media", RW = true } },
                    NetworkSettings = new { Networks = new Dictionary<string, object> { [ic.Net] = new { IPAddress = ic.State == "running" ? "172.17.0.2" : "", Gateway = "172.17.0.1", MacAddress = "" } } },
                }) + "\n");
            case "image" when a[1] == "inspect" && a.Contains("--format"): // « l'image est-elle sur le NAS ? »
                var wanted = a[^1].Contains(':') ? a[^1] : a[^1] + ":latest";
                return _images.Contains(wanted) ? Ok("sha256:fake\n") : Fail($"Error response from daemon: No such image: {a[^1]}");
            case "image" when a[1] == "inspect":
                return Ok(Json(new { Config = new { Env = new[] { "PATH=/usr/bin" } } }) + "\n");
            case "image" when a[1] == "ls":
                return Ok(string.Concat(_images.Select(m => Json(new
                {
                    ID = Math.Abs(m.GetHashCode()).ToString("x8"), Repository = m.Split(':')[0], Tag = m.Split(':')[1], Size = "187MB", CreatedSince = "3 weeks ago",
                }) + "\n")));
            case "image" when a[1] == "prune": return Ok("Total reclaimed space: 0B\n");
            case "pull":
                var img = a[1].Contains(':') ? a[1] : a[1] + ":latest";
                if (!_images.Contains(img)) _images.Add(img);
                return Ok();
            case "rmi":
                _images.RemoveAll(m => m == a[1] || Math.Abs(m.GetHashCode()).ToString("x8") == a[1]);
                return Ok();
            case "volume" when a[1] == "ls":
                return Ok(string.Concat(_volumes.Select(v => Json(new { Name = v, Driver = "local", Mountpoint = $"/var/lib/docker/volumes/{v}/_data" }) + "\n")));
            case "volume" when a[1] == "create": _volumes.Add(a[2]); return Ok(a[2]);
            case "volume" when a[1] == "rm": _volumes.Remove(a[2]); return Ok(a[2]);
            case "network" when a[1] == "ls":
                return Ok(string.Concat(_networks.Select(n => Json(new { ID = n.Short, n.Name, n.Driver, Scope = "local" }) + "\n")));
            case "network" when a[1] == "inspect":
                return Ok(Json(_networks.Where(n => a.Skip(2).Contains(n.Short)).Select(n => new
                {
                    Id = n.Short.PadRight(64, '0'), n.Name, n.Driver,
                    EnableIPv6 = n.Ipv6,
                    IPAM = new { Config = n.Subnet is null ? [] : new[] { new Dictionary<string, string> { ["Subnet"] = n.Subnet, ["Gateway"] = n.Gateway ?? "", ["IPRange"] = n.Range ?? "" }.Where(p => p.Value.Length > 0).ToDictionary(p => p.Key, p => p.Value) } },
                    Options = new[] { ("parent", n.Parent), (n.Driver + "_mode", n.Mode) }.Where(p => p.Item2 is not null).ToDictionary(p => p.Item1, p => p.Item2),
                    Containers = n.Name == "bridge" ? new Dictionary<string, object> { ["a1b2c3d4e5f6"] = new { Name = "jellyfin" } } : new Dictionary<string, object>(),
                }).ToList()) + "\n");
            case "network" when a[1] == "create":
                string? Opt(string flag) => a.ToList().IndexOf(flag) is var ix and >= 0 ? a[ix + 1] : null;
                var po = a.Select((x, ix) => (x, ix)).Where(t => t.x == "-o").Select(t => a[t.ix + 1].Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
                if (_networks.Any(n => n.Name == a[^1])) return Fail($"Error response from daemon: network with name {a[^1]} already exists");
                _networks.Add(new(a[^1], Opt("--driver") ?? "bridge", Opt("--subnet"), Opt("--gateway"), Opt("--ip-range"), a.Contains("--ipv6"),
                    po.GetValueOrDefault("parent"), po.GetValueOrDefault("macvlan_mode") ?? po.GetValueOrDefault("ipvlan_mode")));
                return Ok(a[^1]);
            case "network" when a[1] == "rm": _networks.RemoveAll(n => n.Name == a[2]); return Ok(a[2]);
            default: return Ok();
        }
    }

    string ShareDir(string share)
    {
        var root = Path.Combine(_shares, share);
        if (!Directory.Exists(root))
            foreach (var p in new[] { "Projects/2026", "Projects/Archive", "Finance/Invoices", "HR" })
                Directory.CreateDirectory(Path.Combine(root, p));
        return root;
    }
}
