using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>Ce qu'il faut savoir d'un stockage en ligne (OneDrive, Dropbox) pour le remonter ; gardé dans &lt;nom&gt;.json à côté du fichier rclone.</summary>
public sealed record CloudDef(string Name, string Type, string Path, bool ReadOnly, string Cache, int CacheGb, int BwLimitMbps);

/// <summary>
/// Stockages distants, montés en permanence sous <c>/srv/remote/&lt;nom&gt;</c>.
/// <list type="bullet">
/// <item><b>SMB et NFS</b> : une ligne de /etc/fstab marquée « # ursanas-remote » par connexion (pas de base de données). Le mot de passe SMB n'est que
/// dans /var/lib/ursanas/remote/&lt;nom&gt;.cred (root seulement), que la ligne désigne par <c>credentials=</c>.</item>
/// <item><b>OneDrive et Dropbox</b> : <c>rclone mount</c> lancé par une unité systemd par connexion (ursanas-rclone-&lt;nom&gt;.service). Le jeton est dans
/// &lt;nom&gt;.rclone.conf (root seulement) ; &lt;nom&gt;.json garde les réglages.</item>
/// </list>
/// Rien ne passe par un shell : les programmes reçoivent des arguments séparés, tous validés par <see cref="Rules.ValidateRemote"/>.
/// </summary>
public sealed class RemoteService(ICommandRunner run, IConfiguration cfg, IDriveLookup? lookup = null)
{
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly IDriveLookup _lookup = lookup ?? new GraphDriveLookup();

    string Fstab => cfg["Disks:Fstab"] ?? "/etc/fstab";
    string MountRoot => (cfg["Remote:MountRoot"] ?? "/srv/remote").TrimEnd('/');
    string CredDir => (cfg["Remote:CredDir"] ?? "/var/lib/ursanas/remote").TrimEnd('/');
    string UnitDir => (cfg["Remote:UnitDir"] ?? "/etc/systemd/system").TrimEnd('/');
    string CacheDir => (cfg["Remote:CacheDir"] ?? "/var/cache/ursanas-rclone").TrimEnd('/');
    string MountInfoPath => cfg["Remote:MountInfo"] ?? "/proc/self/mountinfo";
    string[] BinDirs => cfg["Remote:BinDirs"]?.Split(':') ?? ["/usr/sbin", "/sbin", "/usr/bin", "/bin"];
    const string Marker = "# ursanas-remote";
    const string CommonOptions = "_netdev,nofail,x-systemd.mount-timeout=30";

    sealed record Entry(string Name, string Type, string Host, string Path, string Options, string Target);

    // ---- Outils disponibles

    static readonly Dictionary<string, string[]> Helpers = new()
    {
        ["smb"] = ["mount.cifs"], ["nfs"] = ["mount.nfs"], ["onedrive"] = ["rclone", "fusermount3"], ["dropbox"] = ["rclone", "fusermount3"],
    };

    string? ToolPath(string tool) => BinDirs.Select(d => $"{d.TrimEnd('/')}/{tool}").FirstOrDefault(File.Exists);

    public RemoteTypes Types() => new(Rules.RemoteKinds.Where(k => Helpers[k].All(t => ToolPath(t) is not null)).ToList());

    void RequireTool(string type)
    {
        foreach (var t in Helpers[type])
            if (ToolPath(t) is null) throw new OperationException(Msg.F("The program {0} is not installed on the NAS.", t));
    }

    // ---- Lecture

    static string Unescape(string s) => s.Replace("\\040", " ");
    static string Escape(string s) => s.Replace(" ", "\\040");

    static string Device(string type, string host, string path)
    {
        var h = host.Contains(':') ? $"[{host}]" : host;
        return type == "smb" ? $"//{h}/{path}" : $"{h}:{path}";
    }

    static (string Host, string Path) ParseDevice(string type, string device)
    {
        var d = type == "smb" ? device.TrimStart('/') : device;
        string host, rest;
        if (d.StartsWith('['))
        {
            var close = d.IndexOf(']');
            if (close < 0) return (d, "");
            host = d[1..close];
            rest = d[(close + 1)..];
        }
        else
        {
            var cut = type == "smb" ? d.IndexOf('/') : d.IndexOf(':');
            if (cut < 0) return (d, "");
            host = d[..cut];
            rest = d[cut..];
        }
        return (host, type == "smb" ? rest.TrimStart('/') : rest.TrimStart(':'));
    }

    List<Entry> Entries()
    {
        var list = new List<Entry>();
        if (!File.Exists(Fstab)) return list;
        foreach (var line in File.ReadAllLines(Fstab))
        {
            if (line.TrimStart().StartsWith('#') || !line.Contains(Marker, StringComparison.Ordinal)) continue;
            var f = line.Split((char)32, (char)9, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 4) continue;
            var type = f[2] switch { "cifs" => "smb", "nfs" or "nfs4" => "nfs", _ => null };
            var target = Unescape(f[1]);
            if (type is null || !target.StartsWith(MountRoot + "/", StringComparison.Ordinal)) continue;
            var (host, path) = ParseDevice(type, Unescape(f[0]));
            list.Add(new Entry(target[(MountRoot.Length + 1)..], type, host, path, f[3], target));
        }
        return list;
    }

    List<CloudDef> Clouds()
    {
        var list = new List<CloudDef>();
        if (!Directory.Exists(CredDir)) return list;
        foreach (var file in Directory.GetFiles(CredDir, "*.json"))
        {
            if (System.IO.Path.GetFileName(file).StartsWith('.')) continue;
            try
            {
                var d = JsonSerializer.Deserialize<CloudDef>(File.ReadAllText(file));
                if (d is not null && Rules.IsCloud(d.Type) && Rules.ValidateMountName(d.Name) is null) list.Add(d);
            }
            catch (JsonException) { }
        }
        return list;
    }

    static string? Option(string options, string key) =>
        options.Split(',').FirstOrDefault(o => o.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];

    string CredFile(string name) => $"{CredDir}/{name}.cred";
    string RcloneConf(string name) => $"{CredDir}/{name}.rclone.conf";
    string DefFile(string name) => $"{CredDir}/{name}.json";
    string UnitName(string name) => $"ursanas-rclone-{name}.service";
    string UnitFile(string name) => $"{UnitDir}/{UnitName(name)}";
    string TargetOf(string name) => $"{MountRoot}/{name}";

    (string? User, string? Domain) ReadCred(string name)
    {
        var file = CredFile(name);
        if (!File.Exists(file)) return (null, null);
        string? user = null, domain = null;
        foreach (var l in File.ReadAllLines(file))
        {
            if (l.StartsWith("username=", StringComparison.Ordinal)) user = l[9..];
            else if (l.StartsWith("domain=", StringComparison.Ordinal)) domain = l[7..];
        }
        return (user, domain);
    }

    bool IsMounted(string target) =>
        File.Exists(MountInfoPath) && MountTable.Parse(File.ReadAllText(MountInfoPath)).Any(m => m.MountPoint == target);

    /// <summary>Espace du volume ; un serveur qui ne répond plus ne doit pas bloquer l'agent, d'où le délai.</summary>
    static (long Total, long Free) Space(string target)
    {
        var task = Task.Run(() =>
        {
            try { var d = new DriveInfo(target); return (d.TotalSize, d.AvailableFreeSpace); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return (0L, 0L); }
        });
        return task.Wait(TimeSpan.FromSeconds(3)) ? task.Result : (0, 0);
    }

    public IReadOnlyList<RemoteStorage> List()
    {
        var all = new List<RemoteStorage>();
        foreach (var e in Entries())
        {
            var mounted = IsMounted(e.Target);
            var (total, free) = mounted ? Space(e.Target) : (0L, 0L);
            var (user, domain) = e.Type == "smb" ? ReadCred(e.Name) : (null, null);
            all.Add(new RemoteStorage(e.Name, e.Type, e.Host, e.Path, user, domain, Option(e.Options, "vers"),
                e.Options.Split(',').Contains("ro"), e.Target, mounted, total, free));
        }
        foreach (var c in Clouds())
        {
            var target = TargetOf(c.Name);
            var mounted = IsMounted(target);
            var (total, free) = mounted ? Space(target) : (0L, 0L);
            all.Add(new RemoteStorage(c.Name, c.Type, "", c.Path, null, null, null, c.ReadOnly, target, mounted, total, free, c.Cache, c.CacheGb, c.BwLimitMbps));
        }
        return all.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- Commandes

    async Task<string> Ok(string file, IReadOnlyList<string> args, string what, CancellationToken ct, params string[] secrets)
    {
        CommandResult r;
        try { r = await run.RunAsync(file, args, ct: ct); }
        catch (Win32Exception) { throw new OperationException(Msg.F("The program {0} is not installed on the NAS.", file)); }
        if (r.ExitCode == 0) return r.StdOut;
        var msg = (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim();
        foreach (var s in secrets.Where(s => s.Length > 0)) msg = msg.Replace(s, "***"); // un message d'erreur ne doit jamais redonner le jeton
        throw new OperationException(Msg.F("{0} failed: {1}", what, msg));
    }

    async Task Mutate(Func<Task> action)
    {
        await _gate.WaitAsync();
        try { await action(); }
        finally { _gate.Release(); }
    }

    async Task<string?> NasGidAsync(CancellationToken ct)
    {
        try
        {
            var r = await run.RunAsync("getent", ["group", UserService.Group], ct: ct);
            var f = r.StdOut.Trim().Split(':');
            return r.ExitCode == 0 && f.Length > 2 && f[2].All(char.IsDigit) ? f[2] : null;
        }
        catch (Win32Exception) { return null; }
    }

    static void TryDelete(Action a)
    {
        try { a(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static void WritePrivate(string file, string content)
    {
        using (File.Create(file)) { }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); // avant d'y écrire le secret
        File.WriteAllText(file, content);
    }

    void EnsureCredDir()
    {
        Directory.CreateDirectory(CredDir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(CredDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    bool Taken(string name) =>
        Entries().Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
        || Clouds().Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
        || IsMounted(TargetOf(name));

    // ---- SMB et NFS

    /// <summary>Les options de montage. En SMB, les fichiers appartiennent à root et au groupe du NAS (les droits réels sont ceux du serveur).</summary>
    async Task<string> OptionsAsync(AddRemoteRequest r, string? credFile, CancellationToken ct)
    {
        var o = new List<string>();
        if (r.Type == "smb")
        {
            o.Add("uid=0");
            if (await NasGidAsync(ct) is { } gid) o.Add($"gid={gid}");
            o.Add("file_mode=0660");
            o.Add("dir_mode=0770");
            o.Add(credFile is null ? "guest" : $"credentials={Escape(credFile)}");
        }
        if (!string.IsNullOrEmpty(r.Version)) o.Add("vers=" + r.Version);
        if (r.ReadOnly) o.Add("ro");
        o.Add(CommonOptions);
        return string.Join(',', o);
    }

    string WriteCred(string name, AddRemoteRequest r)
    {
        EnsureCredDir();
        var file = CredFile(name);
        WritePrivate(file, $"username={r.User}\npassword={r.Password}\n" + (string.IsNullOrEmpty(r.Domain) ? "" : $"domain={r.Domain}\n"));
        return file;
    }

    Task MountNow(AddRemoteRequest r, string target, string options, CancellationToken ct) =>
        Ok("mount", ["-t", r.Type == "smb" ? "cifs" : "nfs", Device(r.Type, r.Host, r.Path), target, "-o", options], "Mounting", ct);

    async Task TestKernelAsync(AddRemoteRequest r, CancellationToken ct)
    {
        var id = ".test-" + Guid.NewGuid().ToString("N")[..8];
        var target = $"{MountRoot}/{id}";
        string? cred = null;
        var mounted = false;
        try
        {
            if (r.Type == "smb" && !string.IsNullOrEmpty(r.User)) cred = WriteCred(id, r);
            Directory.CreateDirectory(target);
            await MountNow(r, target, await OptionsAsync(r, cred, ct), ct);
            mounted = true;
        }
        finally
        {
            if (mounted) await Ok("umount", [target], "Unmounting", CancellationToken.None);
            if (cred is not null) TryDelete(() => File.Delete(cred));
            TryDelete(() => Directory.Delete(target));
        }
    }

    async Task AddKernelAsync(AddRemoteRequest r, CancellationToken ct)
    {
        var target = TargetOf(r.Name);
        string? cred = null;
        var mounted = false;
        var done = false;
        try
        {
            if (r.Type == "smb" && !string.IsNullOrEmpty(r.User)) cred = WriteCred(r.Name, r);
            Directory.CreateDirectory(target);
            var options = await OptionsAsync(r, cred, ct);
            await MountNow(r, target, options, ct);
            mounted = true;
            var line = $"{Escape(Device(r.Type, r.Host, r.Path))} {Escape(target)} {(r.Type == "smb" ? "cifs" : "nfs")} {options} 0 0 {Marker}";
            FstabFile.Edit(Fstab, lines => [.. lines.Where(l => !IsOurs(l, target)), line]);
            done = true;
        }
        finally
        {
            if (!done) // rien ne doit rester d'une connexion qui n'a pas abouti
            {
                if (mounted) await Ok("umount", [target], "Unmounting", CancellationToken.None);
                if (cred is not null) TryDelete(() => File.Delete(cred));
                TryDelete(() => Directory.Delete(target));
            }
        }
    }

    static bool IsOurs(string line, string target)
    {
        var f = line.Split((char)32, (char)9, StringSplitOptions.RemoveEmptyEntries);
        return line.Contains(Marker, StringComparison.Ordinal) && f.Length > 1 && Unescape(f[1]) == target;
    }

    // ---- OneDrive et Dropbox (rclone)

    /// <summary>Le fichier de configuration de rclone : le jeton, et pour OneDrive l'identifiant du disque du compte.</summary>
    async Task<string> RcloneConfAsync(string section, AddRemoteRequest r, CancellationToken ct)
    {
        Rules.IsRcloneToken(r.Token, out var token);
        var conf = $"[{section}]\ntype = {r.Type}\ntoken = {token}\n";
        if (r.Type == "onedrive")
        {
            using var doc = JsonDocument.Parse(token);
            var (id, type) = await _lookup.OneDriveAsync(doc.RootElement.GetProperty("access_token").GetString()!, ct);
            conf += $"drive_id = {id}\ndrive_type = {type}\n";
        }
        return conf;
    }

    static string Quote(string s) => "\"" + s + "\"";

    /// <summary>L'unité systemd qui garde le montage rclone en vie (et le remonte après une panne ou un redémarrage).</summary>
    public static string RenderUnit(CloudDef d, string rclone, string fusermount, string conf, string target, string cacheDir, string? gid)
    {
        var args = new List<string>
        {
            "mount", Quote($"{d.Name}:{d.Path}"), Quote(target), "--config", Quote(conf), "--allow-other", "--umask", "007",
        };
        if (gid is not null) args.AddRange(["--gid", gid]);
        args.AddRange(["--vfs-cache-mode", d.Cache]);
        if (d.Cache == "full") args.AddRange(["--vfs-cache-max-size", d.CacheGb.ToString(CultureInfo.InvariantCulture) + "G"]);
        args.AddRange(["--cache-dir", Quote(cacheDir), "--dir-cache-time", "5m"]);
        if (d.ReadOnly) args.Add("--read-only");
        if (d.BwLimitMbps > 0) args.AddRange(["--bwlimit", d.BwLimitMbps.ToString(CultureInfo.InvariantCulture) + "M"]);
        return string.Join('\n',
            "# Géré par UrsaNAS : ne pas modifier à la main.",
            "[Unit]",
            $"Description=UrsaNAS remote storage {d.Name} ({d.Type})",
            "After=network-online.target",
            "Wants=network-online.target",
            "",
            "[Service]",
            "Type=notify",
            $"ExecStart={rclone} {string.Join(' ', args)}",
            $"ExecStop={fusermount} -uz {Quote(target)}",
            "Restart=on-failure",
            "RestartSec=15",
            "",
            "[Install]",
            "WantedBy=multi-user.target",
            "");
    }

    string RemoteSpec(string section, string path) => path.Length > 0 ? $"{section}:{path}" : $"{section}:";

    async Task RcloneLsdAsync(string section, string path, string conf, string token, CancellationToken ct) =>
        await Ok(ToolPath("rclone")!, ["lsd", RemoteSpec(section, path), "--config", conf, "--max-depth", "1", "--contimeout", "15s", "--timeout", "30s", "--retries", "1", "--low-level-retries", "1"],
            "Connection", ct, token);

    async Task TestCloudAsync(AddRemoteRequest r, CancellationToken ct)
    {
        var id = ".test-" + Guid.NewGuid().ToString("N")[..8];
        EnsureCredDir();
        var conf = RcloneConf(id);
        Rules.IsRcloneToken(r.Token, out var token);
        try
        {
            WritePrivate(conf, await RcloneConfAsync(id, r, ct));
            await RcloneLsdAsync(id, r.Path ?? "", conf, token, ct);
        }
        finally { TryDelete(() => File.Delete(conf)); }
    }

    async Task AddCloudAsync(AddRemoteRequest r, CancellationToken ct)
    {
        EnsureCredDir();
        var target = TargetOf(r.Name);
        var conf = RcloneConf(r.Name);
        var def = new CloudDef(r.Name, r.Type, r.Path ?? "", r.ReadOnly, r.Cache, r.CacheGb, r.BwLimitMbps);
        Rules.IsRcloneToken(r.Token, out var token);
        var done = false;
        var started = false;
        try
        {
            WritePrivate(conf, await RcloneConfAsync(r.Name, r, ct));
            await RcloneLsdAsync(r.Name, def.Path, conf, token, ct); // un compte inaccessible est refusé avant d'installer quoi que ce soit
            Directory.CreateDirectory(target);
            Directory.CreateDirectory($"{CacheDir}/{r.Name}");
            File.WriteAllText(DefFile(r.Name), JsonSerializer.Serialize(def));
            Directory.CreateDirectory(UnitDir);
            File.WriteAllText(UnitFile(r.Name), RenderUnit(def, ToolPath("rclone")!, ToolPath("fusermount3")!, conf, target, $"{CacheDir}/{r.Name}", await NasGidAsync(ct)));
            await Ok("systemctl", ["daemon-reload"], "Mounting", ct);
            started = true;
            try { await Ok("systemctl", ["enable", "--now", UnitName(r.Name)], "Mounting", ct, token); }
            catch (OperationException e)
            {
                // « Job failed » ne dit rien : les dernières lignes du journal de l'unité disent pourquoi.
                var log = await RunQuiet("journalctl", ["-u", UnitName(r.Name), "-n", "6", "--no-pager", "-o", "cat"], token, ct);
                throw log.Length > 0 ? new OperationException(Msg.F("{0} failed: {1}", "Mounting", log)) : e;
            }
            done = true;
        }
        finally
        {
            if (!done)
            {
                if (started) await RunQuiet("systemctl", ["disable", "--now", UnitName(r.Name)], token, CancellationToken.None);
                TryDelete(() => File.Delete(UnitFile(r.Name)));
                if (started) await RunQuiet("systemctl", ["daemon-reload"], token, CancellationToken.None);
                TryDelete(() => File.Delete(conf));
                TryDelete(() => File.Delete(DefFile(r.Name)));
                TryDelete(() => Directory.Delete($"{CacheDir}/{r.Name}", true));
                TryDelete(() => Directory.Delete(target));
            }
        }
    }

    /// <summary>Lance une commande dont on ne veut que la sortie (journal, nettoyage) : jamais d'exception, jamais le jeton.</summary>
    async Task<string> RunQuiet(string file, IReadOnlyList<string> args, string secret, CancellationToken ct)
    {
        try
        {
            var r = await run.RunAsync(file, args, ct: ct);
            var text = (string.IsNullOrWhiteSpace(r.StdOut) ? r.StdErr : r.StdOut).Trim();
            return secret.Length > 0 ? text.Replace(secret, "***") : text;
        }
        catch (Exception e) when (e is Win32Exception or OperationCanceledException) { return ""; }
    }

    // ---- Opérations

    /// <summary>Essaie la connexion sans rien enregistrer : montage temporaire (SMB, NFS) ou liste du compte (OneDrive, Dropbox).</summary>
    public Task TestAsync(AddRemoteRequest r, CancellationToken ct = default)
    {
        if (Rules.ValidateRemote(r) is { } e) throw new OperationException(e);
        return Mutate(async () =>
        {
            RequireTool(r.Type);
            if (Rules.IsCloud(r.Type)) await TestCloudAsync(r, ct);
            else await TestKernelAsync(r, ct);
        });
    }

    public Task AddAsync(AddRemoteRequest r, CancellationToken ct = default)
    {
        if (Rules.ValidateRemote(r) is { } e) throw new OperationException(e);
        return Mutate(async () =>
        {
            RequireTool(r.Type);
            if (Taken(r.Name)) throw new OperationException(Msg.F("“{0}” is already used as a mount folder.", r.Name));
            if (Rules.IsCloud(r.Type)) await AddCloudAsync(r, ct);
            else await AddKernelAsync(r, ct);
        });
    }

    /// <summary>Une connexion enregistrée : SMB/NFS (ligne fstab) ou en ligne (définition rclone).</summary>
    (string Name, string Type, string Target, CloudDef? Cloud) Find(string name)
    {
        if (Rules.ValidateMountName(name) is { } e) throw new OperationException(e);
        if (Entries().FirstOrDefault(x => x.Name == name) is { } k) return (k.Name, k.Type, k.Target, null);
        if (Clouds().FirstOrDefault(x => x.Name == name) is { } c) return (c.Name, c.Type, TargetOf(c.Name), c);
        throw new OperationException(Msg.F("Unknown remote storage."));
    }

    /// <summary>Monte une connexion enregistrée (la ligne fstab ou l'unité systemd donne tout le reste).</summary>
    public Task MountAsync(string name, CancellationToken ct = default) => Mutate(async () =>
    {
        var e = Find(name);
        RequireTool(e.Type);
        if (IsMounted(e.Target)) return;
        if (e.Cloud is not null) { await Ok("systemctl", ["start", UnitName(name)], "Mounting", ct); return; }
        Directory.CreateDirectory(e.Target);
        await Ok("mount", [e.Target], "Mounting", ct);
    });

    public Task UnmountAsync(string name, CancellationToken ct = default) => Mutate(async () =>
    {
        var e = Find(name);
        if (e.Cloud is not null) { await Ok("systemctl", ["stop", UnitName(name)], "Unmounting", ct); return; }
        if (!IsMounted(e.Target)) return;
        await Ok("umount", [e.Target], "Unmounting", ct);
    });

    public Task RemoveAsync(string name, CancellationToken ct = default) => Mutate(async () =>
    {
        var e = Find(name);
        if (e.Cloud is not null)
        {
            await Ok("systemctl", ["disable", "--now", UnitName(name)], "Unmounting", ct);
            TryDelete(() => File.Delete(UnitFile(name)));
            await Ok("systemctl", ["daemon-reload"], "Unmounting", ct);
            TryDelete(() => File.Delete(RcloneConf(name)));
            TryDelete(() => File.Delete(DefFile(name)));
            TryDelete(() => Directory.Delete($"{CacheDir}/{name}", true));
            TryDelete(() => Directory.Delete(e.Target)); // seulement s'il est vide
            return;
        }
        if (IsMounted(e.Target)) await Ok("umount", [e.Target], "Unmounting", ct);
        FstabFile.Edit(Fstab, lines => lines.Where(l => !IsOurs(l, e.Target)).ToList());
        TryDelete(() => File.Delete(CredFile(name)));
        TryDelete(() => Directory.Delete(e.Target)); // seulement s'il est vide
    });
}
