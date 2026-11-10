using System.ComponentModel;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Stockages distants SMB et NFS, montés en permanence sous <c>/srv/remote/&lt;nom&gt;</c>. La liste de référence est /etc/fstab : une ligne
/// marquée « # ursanas-remote » par connexion (aucune base de données). Le mot de passe SMB n'est que dans un fichier réservé à root
/// (/var/lib/ursanas/remote/&lt;nom&gt;.cred), que la ligne fstab désigne par <c>credentials=</c>.
/// Rien ne passe par un shell : <c>mount</c> reçoit des arguments séparés, tous validés par <see cref="Rules.ValidateRemote"/>.
/// </summary>
public sealed class RemoteService(ICommandRunner run, IConfiguration cfg)
{
    readonly SemaphoreSlim _gate = new(1, 1);

    string Fstab => cfg["Disks:Fstab"] ?? "/etc/fstab";
    string MountRoot => (cfg["Remote:MountRoot"] ?? "/srv/remote").TrimEnd('/');
    string CredDir => (cfg["Remote:CredDir"] ?? "/var/lib/ursanas/remote").TrimEnd('/');
    string MountInfoPath => cfg["Remote:MountInfo"] ?? "/proc/self/mountinfo";
    string[] BinDirs => cfg["Remote:BinDirs"]?.Split(':') ?? ["/usr/sbin", "/sbin", "/usr/bin", "/bin"];
    const string Marker = "# ursanas-remote";
    const string CommonOptions = "_netev,nofail,x-systemd.mount-timeout=30";

    sealed record Entry(string Name, string Type, string Host, string Path, string Options, string Target);

    // ---- Outils disponibles

    static readonly Dictionary<string, string> Helper = new() { ["smb"] = "mount.cifs", ["nfs"] = "mount.nfs" };

    bool HasTool(string tool) => BinDirs.Any(d => File.Exists(System.IO.Path.Combine(d, tool)));

    public RemoteTypes Types() => new(Rules.RemoteKinds.Where(k => HasTool(Helper[k])).ToList());

    void RequireTool(string type)
    {
        if (!HasTool(Helper[type])) throw new OperationException(Msg.F("The program {0} is not installed on the NAS.", Helper[type]));
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

    static string? Option(string options, string key) =>
        options.Split(',').FirstOrDefault(o => o.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];

    string CredFile(string name) => $"{CredDir}/{name}.cred";

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

    public IReadOnlyList<RemoteStorage> List() => Entries().OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Select(e =>
    {
        var mounted = IsMounted(e.Target);
        var (total, free) = mounted ? Space(e.Target) : (0L, 0L);
        var (user, domain) = e.Type == "smb" ? ReadCred(e.Name) : (null, null);
        return new RemoteStorage(e.Name, e.Type, e.Host, e.Path, user, domain, Option(e.Options, "vers"),
            e.Options.Split(',').Contains("ro"), e.Target, mounted, total, free);
    }).ToList();

    // ---- Commandes

    async Task<string> Ok(string file, IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        CommandResult r;
        try { r = await run.RunAsync(file, args, ct: ct); }
        catch (Win32Exception) { throw new OperationException(Msg.F("The program {0} is not installed on the NAS.", file)); }
        if (r.ExitCode == 0) return r.StdOut;
        var msg = (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim();
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
        Directory.CreateDirectory(CredDir);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(CredDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = CredFile(name);
        using (File.Create(file)) { }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); // avant d'y écrire le mot de passe
        File.WriteAllText(file, $"username={r.User}\npassword={r.Password}\n" + (string.IsNullOrEmpty(r.Domain) ? "" : $"domain={r.Domain}\n"));
        return file;
    }

    Task MountNow(AddRemoteRequest r, string target, string options, CancellationToken ct) =>
        Ok("mount", ["-t", r.Type == "smb" ? "cifs" : "nfs", Device(r.Type, r.Host, r.Path), target, "-o", options], "Mounting", ct);

    static void TryDelete(Action a)
    {
        try { a(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ---- Opérations

    /// <summary>Essaie la connexion (montage dans un dossier temporaire, puis démontage) sans rien enregistrer.</summary>
    public Task TestAsync(AddRemoteRequest r, CancellationToken ct = default)
    {
        if (Rules.ValidateRemote(r) is { } e) throw new OperationException(e);
        return Mutate(async () =>
        {
            RequireTool(r.Type);
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
        });
    }

    public Task AddAsync(AddRemoteRequest r, CancellationToken ct = default)
    {
        if (Rules.ValidateRemote(r) is { } e) throw new OperationException(e);
        return Mutate(async () =>
        {
            RequireTool(r.Type);
            var target = $"{MountRoot}/{r.Name}";
            if (Entries().Any(x => string.Equals(x.Name, r.Name, StringComparison.OrdinalIgnoreCase)) || IsMounted(target))
                throw new OperationException(Msg.F("“{0}” is already used as a mount folder.", r.Name));
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
        });
    }

    Entry Find(string name)
    {
        if (Rules.ValidateMountName(name) is { } e) throw new OperationException(e);
        return Entries().FirstOrDefault(x => x.Name == name) ?? throw new OperationException(Msg.F("Unknown remote storage."));
    }

    /// <summary>Monte une connexion enregistrée (la ligne fstab donne tout le reste).</summary>
    public Task MountAsync(string name, CancellationToken ct = default) => Mutate(async () =>
    {
        var e = Find(name);
        RequireTool(e.Type);
        if (IsMounted(e.Target)) return;
        Directory.CreateDirectory(e.Target);
        await Ok("mount", [e.Target], "Mounting", ct);
    });

    public Task UnmountAsync(string name, CancellationToken ct = default) => Mutate(async () =>
    {
        var e = Find(name);
        if (!IsMounted(e.Target)) return;
        await Ok("umount", [e.Target], "Unmounting", ct);
    });

    public Task RemoveAsync(string name, CancellationToken ct = default) => Mutate(async () =>
    {
        var e = Find(name);
        if (IsMounted(e.Target)) await Ok("umount", [e.Target], "Unmounting", ct);
        FstabFile.Edit(Fstab, lines => lines.Where(l => !IsOurs(l, e.Target)).ToList());
        TryDelete(() => File.Delete(CredFile(name)));
        TryDelete(() => Directory.Delete(e.Target)); // seulement s'il est vide
    });

    static bool IsOurs(string line, string target)
    {
        var f = line.Split((char)32, (char)9, StringSplitOptions.RemoveEmptyEntries);
        return line.Contains(Marker, StringComparison.Ordinal) && f.Length > 1 && Unescape(f[1]) == target;
    }
}
