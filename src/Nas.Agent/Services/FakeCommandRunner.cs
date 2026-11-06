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
            case "testparm" when a.Any(x => x.StartsWith("--section-name=", StringComparison.Ordinal)):
                return Ok(ShareDir(a.First(x => x.StartsWith("--section-name=", StringComparison.Ordinal))["--section-name=".Length..]) + "\n");
            case "smbclient":
                var share = a[0].Split('/').Last();
                var dir = Path.Combine(ShareDir(share), a[^1]["mkdir \"".Length..^1].TrimStart('/'));
                if (Directory.Exists(dir)) return Ok("NT_STATUS_OBJECT_NAME_COLLISION making remote directory");
                Directory.CreateDirectory(dir);
                return Ok();
            default:
                return Ok(); // chgrp, chmod, smbpasswd, smbcontrol, smbcacls, testparm…
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
