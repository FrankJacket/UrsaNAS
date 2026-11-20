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
            default:
                return Ok(); // chgrp, chmod, smbpasswd, smbcontrol, smbcacls, testparm…
        }
    }

    // ---- Docker simulé : quelques conteneurs, images, volumes et réseaux qui réagissent aux commandes.
    sealed record FakeContainer(string Id, string Name, string Image, string State, string Ports, string? Project);
    readonly List<FakeContainer> _containers =
    [
        new("a1b2c3d4e5f6", "jellyfin", "jellyfin/jellyfin:latest", "running", "0.0.0.0:8096->8096/tcp", null),
        new("b2c3d4e5f6a1", "old-test", "alpine:3.20", "exited", "", null),
    ];
    readonly List<string> _images = ["jellyfin/jellyfin:latest", "alpine:3.20", "nginx:latest"];
    readonly List<string> _volumes = ["jellyfin-config"];
    readonly List<string> _networks = ["bridge", "host", "none"];

    static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    CommandResult FakeDocker(IReadOnlyList<string> a)
    {
        switch (a[0])
        {
            case "version": return Ok("27.3.1\n");
            case "compose" when a[1] == "version": return Ok("2.29.7\n");
            case "compose":
                var project = a[2];
                var verb = a.Skip(3).First(x => x is "up" or "down" or "pull" or "config");
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
                _containers.Add(new(Guid.NewGuid().ToString("N")[..12], name, a[^1], a[0] == "run" ? "running" : "created", "", null));
                return Ok();
            case "rename":
                var k = _containers.FindIndex(c => c.Id == a[1] || c.Name == a[1]);
                if (k < 0) return Fail($"Error response from daemon: No such container: {a[1]}");
                _containers[k] = _containers[k] with { Name = a[2] };
                return Ok();
            case "update": return Ok(a[^1]);
            case "inspect":
                var ic = _containers.FirstOrDefault(c => c.Id == a[^1] || c.Name == a[^1]);
                if (ic is null) return Fail($"Error: No such object: {a[^1]}");
                return Ok(Json(new
                {
                    Name = "/" + ic.Name, Image = "sha256:fake",
                    State = new { Running = ic.State == "running" },
                    Config = new { Image = ic.Image, Env = new[] { "PATH=/usr/bin", "TZ=UTC" }, Labels = ic.Project is null ? new Dictionary<string, string>() : new() { ["com.docker.compose.project"] = ic.Project } },
                    HostConfig = new
                    {
                        RestartPolicy = new { Name = "unless-stopped" },
                        PortBindings = ic.Ports.Length == 0 ? new Dictionary<string, object[]>() : new() { ["8096/tcp"] = [new { HostIp = "0.0.0.0", HostPort = "8096" }] },
                    },
                    Mounts = ic.Ports.Length == 0 ? [] : new[] { new { Type = "bind", Source = "/srv/media", Destination = "/media", RW = true } },
                    NetworkSettings = new { Networks = new Dictionary<string, object> { ["bridge"] = new { } } },
                }) + "\n");
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
                return Ok(string.Concat(_networks.Select(n => Json(new { ID = Math.Abs(n.GetHashCode()).ToString("x8"), Name = n, Driver = n is "host" or "none" ? n : "bridge", Scope = "local" }) + "\n")));
            case "network" when a[1] == "create": _networks.Add(a[2]); return Ok(a[2]);
            case "network" when a[1] == "rm": _networks.Remove(a[2]); return Ok(a[2]);
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
