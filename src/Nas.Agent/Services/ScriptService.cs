using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>La fin d'une sortie de programme : au plus <paramref name="capacity"/> octets, les plus anciens sont oubliés.</summary>
public sealed class OutputTail(int capacity = ScriptRules.MaxOutputBytes)
{
    readonly byte[] _buf = new byte[capacity];
    int _start, _count;
    bool _truncated;

    public void Append(ReadOnlySpan<byte> data)
    {
        lock (_buf)
        {
            if (data.Length >= _buf.Length)
            {
                _truncated |= _count > 0 || data.Length > _buf.Length;
                data[^_buf.Length..].CopyTo(_buf);
                (_start, _count) = (0, _buf.Length);
                return;
            }
            var overflow = _count + data.Length - _buf.Length;
            if (overflow > 0)
            {
                _truncated = true;
                _start = (_start + overflow) % _buf.Length;
                _count -= overflow;
            }
            var end = (_start + _count) % _buf.Length;
            var first = Math.Min(data.Length, _buf.Length - end);
            data[..first].CopyTo(_buf.AsSpan(end));
            data[first..].CopyTo(_buf);
            _count += data.Length;
        }
    }

    /// <summary>Le texte conservé ; « […] » en première ligne signale que le début a été coupé.</summary>
    public override string ToString()
    {
        lock (_buf)
        {
            var bytes = new byte[_count];
            var first = Math.Min(_count, _buf.Length - _start);
            _buf.AsSpan(_start, first).CopyTo(bytes);
            _buf.AsSpan(0, _count - first).CopyTo(bytes.AsSpan(first));
            var skip = 0;
            if (_truncated) while (skip < bytes.Length && (bytes[skip] & 0xC0) == 0x80) skip++; // un caractère UTF-8 coupé en deux
            var text = Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip).Replace("\0", "");
            return _truncated ? "[…]\n" + text : text;
        }
    }
}

/// <summary>
/// Exécute les scripts des tâches planifiées. Chaque script est écrit dans un fichier temporaire que seul son compte peut lire, lancé dans sa propre
/// session (pour pouvoir arrêter aussi ses processus enfants), puis le fichier est supprimé. Les exécutions vivent en mémoire : un redémarrage
/// de l'agent les arrête et les oublie, et l'interface les note alors « interrompues ».
/// </summary>
public sealed partial class ScriptService
{
    const int SIGKILL = 9, SIGTERM = 15;

    [DllImport("libc", SetLastError = true)] static extern int kill(int pid, int signal);
    [DllImport("libc", SetLastError = true)] static extern int chown(string path, uint owner, uint group);
    [DllImport("libc")] static extern uint geteuid();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdRegex();

    sealed record Account(string Name, uint Uid, uint Gid, string Home);

    sealed class RunState(string id)
    {
        public CancellationTokenSource Stop { get; } = new();
        public OutputTail Output { get; } = new();
        public volatile ScriptRunStatus Status = new(id, ScriptState.Running);
    }

    readonly UserService _users;
    readonly ICommandRunner _run;
    readonly IConfiguration _cfg;
    readonly ILogger<ScriptService> _log;
    readonly ConcurrentDictionary<string, RunState> _runs = new();

    /// <summary>L'unité de la limite de durée (une minute) ; les tests la raccourcissent.</summary>
    public TimeSpan TimeoutUnit { get; set; } = TimeSpan.FromMinutes(1);

    public ScriptService(UserService users, ICommandRunner run, IConfiguration cfg, ILogger<ScriptService> log, IHostApplicationLifetime life)
    {
        (_users, _run, _cfg, _log) = (users, run, cfg, log);
        life.ApplicationStopping.Register(() => { foreach (var s in _runs.Values) s.Stop.Cancel(); });
        RemoveLeftovers();
    }

    string Folder => _cfg["Scripts:Directory"] ?? "/run/ursanas-scripts";
    TimeSpan KillDelay => TimeSpan.FromSeconds(_cfg.GetValue("Scripts:KillDelaySeconds", 5));

    /// <summary>Les fichiers qu'un arrêt brutal de l'agent a pu laisser.</summary>
    void RemoveLeftovers()
    {
        try
        {
            if (Directory.Exists(Folder))
                foreach (var f in Directory.EnumerateFiles(Folder, "*.sh")) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.LogWarning("Old script files could not be removed: {Message}", e.Message); }
    }

    public IReadOnlyList<ScriptRunStatus> Statuses() => _runs.Values.Select(s => s.Status).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();

    public void Cancel(string id) { if (_runs.TryGetValue(id, out var s) && s.Status.State == ScriptState.Running) s.Stop.Cancel(); }

    /// <summary>La web a enregistré la fin du script : l'agent peut l'oublier.</summary>
    public void Acknowledge(string id)
    {
        if (_runs.TryGetValue(id, out var s) && s.Status.State != ScriptState.Running) _runs.TryRemove(id, out _);
    }

    /// <summary>Lance le script et rend la main dès que le processus est parti.</summary>
    public async Task StartAsync(ScriptRunRequest req, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsLinux() || _cfg["URSANAS_FAKE"] == "1") throw new OperationException(Msg.F("Scripts can only run on the NAS itself."));
        if (req.Id is null || !IdRegex().IsMatch(req.Id)) throw new OperationException(Msg.F("Invalid script run identifier."));
        if (ScriptRules.Validate(req.Script, req.RunAs, req.TimeoutMinutes) is { } error) throw new OperationException(error);
        var account = await ResolveAccountAsync(req.RunAs, ct);

        var state = new RunState(req.Id);
        if (!_runs.TryAdd(req.Id, state)) throw new OperationException(Msg.F("This script run already exists."));
        var path = Path.Combine(Folder, req.Id + ".sh");
        try
        {
            WriteScript(path, ScriptRules.Normalize(req.Script), account);
            var process = StartProcess(path, account);
            _ = Task.Run(() => FollowAsync(process, path, req.TimeoutMinutes, state));
        }
        catch (Exception e)
        {
            _runs.TryRemove(req.Id, out _);
            DeleteFile(path);
            if (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                throw new OperationException(Msg.F("The script could not be started: {0}", e.Message));
            throw;
        }
    }

    /// <summary>Seuls « root » et les utilisateurs du NAS sont acceptés, jamais un autre compte système.</summary>
    async Task<Account> ResolveAccountAsync(string name, CancellationToken ct)
    {
        if (name != ScriptRules.Root && !(await _users.ListAsync(ct)).Any(u => u.Name == name))
            throw new OperationException(Msg.F("User “{0}” does not exist.", name));
        var r = await _run.RunAsync("getent", ["passwd", name], ct: ct);
        var f = r.StdOut.Trim().Split(':');
        if (r.ExitCode != 0 || f.Length < 6 || f[0] != name || !uint.TryParse(f[2], out var uid) || !uint.TryParse(f[3], out var gid))
            throw new OperationException(Msg.F("User “{0}” does not exist.", name));
        // Un compte du NAS ne doit jamais avoir l'identité de root.
        if (name != ScriptRules.Root && uid == 0) throw new OperationException(Msg.F("“{0}” is an existing system account: it cannot be used.", name));
        return new Account(name, uid, gid, f[5]);
    }

    [SupportedOSPlatform("linux")]
    void WriteScript(string path, string script, Account account)
    {
        // Le dossier se traverse sans pouvoir être listé ; chaque fichier n'est lisible que par le compte qui l'exécute.
        Directory.CreateDirectory(Folder);
        File.SetUnixFileMode(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
        using (var file = new FileStream(path, options))
            file.Write(new UTF8Encoding(false).GetBytes(script));
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        if (Environment.IsPrivilegedProcess && chown(path, account.Uid, account.Gid) != 0)
            throw new IOException("chown failed (errno " + Marshal.GetLastPInvokeError() + ").");
    }

    Process StartProcess(string path, Account account)
    {
        // « setsid » : le script est le chef d'une nouvelle session, son numéro de processus est donc aussi celui du groupe à arrêter.
        // « setpriv » : quitte l'identité et les groupes de l'agent pour ceux du compte choisi.
        var psi = new ProcessStartInfo("setsid")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Directory.Exists(account.Home) ? account.Home : "/",
        };
        if (Environment.IsPrivilegedProcess)
            foreach (var a in new[] { "setpriv", $"--reuid={account.Uid}", $"--regid={account.Gid}", "--init-groups", "--" }) psi.ArgumentList.Add(a);
        else if (account.Uid != geteuid())
            throw new OperationException(Msg.F("The agent is not running as root: it cannot run a script as another account."));
        // Les erreurs rejoignent la sortie standard pour garder les lignes dans l'ordre.
        foreach (var a in new[] { "/bin/bash", "-c", "umask 022; exec /bin/bash \"$0\" 2>&1", path }) psi.ArgumentList.Add(a);

        psi.Environment.Clear(); // rien de l'environnement du service
        psi.Environment["HOME"] = account.Home;
        psi.Environment["USER"] = account.Name;
        psi.Environment["LOGNAME"] = account.Name;
        psi.Environment["SHELL"] = "/bin/bash";
        psi.Environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
        psi.Environment["LANG"] = "C.UTF-8";

        var process = Process.Start(psi) ?? throw new IOException("The shell could not be started.");
        process.StandardInput.Close(); // un script qui attend une saisie lit une fin de fichier au lieu de rester bloqué
        return process;
    }

    async Task FollowAsync(Process process, string path, int timeoutMinutes, RunState state)
    {
        var id = state.Status.Id;
        try
        {
            var group = process.Id;
            var output = Task.WhenAll(PumpAsync(process.StandardOutput.BaseStream, state.Output), PumpAsync(process.StandardError.BaseStream, state.Output));

            string? ended = null;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(state.Stop.Token))
            {
                deadline.CancelAfter(TimeoutUnit * timeoutMinutes);
                try { await process.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException)
                {
                    ended = state.Stop.IsCancellationRequested ? ScriptState.Stopped : ScriptState.TimedOut;
                    await KillAsync(process, group);
                }
            }

            // Un script terminé ne laisse rien derrière lui : un court délai, puis les processus restés dans son groupe sont arrêtés.
            await WaitUntilAsync(() => !GroupAlive(group), TimeSpan.FromSeconds(1));
            await EndGroupAsync(group);
            // Un processus volontairement détaché peut garder la sortie ouverte : on ne l'attend pas.
            await Task.WhenAny(output, Task.Delay(TimeSpan.FromSeconds(2)));

            int? code = process.HasExited ? process.ExitCode : null;
            var result = ended ?? (code == 0 ? ScriptState.Succeeded : ScriptState.Failed);
            DeleteFile(path); // avant d'annoncer la fin
            state.Status = new(id, result, ended is null ? code : null, state.Output.ToString(), DateTime.UtcNow, TimeoutMinutes: timeoutMinutes);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Script run {Id} failed unexpectedly", id);
            DeleteFile(path);
            state.Status = new(id, ScriptState.Failed, null, state.Output.ToString(), DateTime.UtcNow, e.Message);
        }
        finally
        {
            DeleteFile(path);
            process.Dispose();
        }
    }

    static async Task PumpAsync(Stream stream, OutputTail tail)
    {
        var buf = new byte[8192];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buf)) > 0) tail.Append(buf.AsSpan(0, n));
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    /// <summary>Arrêt demandé ou limite dépassée : SIGTERM au groupe, puis SIGKILL à ce qui résiste, descendants compris.</summary>
    async Task KillAsync(Process process, int group)
    {
        kill(-group, SIGTERM);
        if (!await WaitUntilAsync(() => process.HasExited, KillDelay))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
            kill(-group, SIGKILL);
            await WaitUntilAsync(() => process.HasExited, TimeSpan.FromSeconds(5));
        }
    }

    async Task EndGroupAsync(int group)
    {
        if (!GroupAlive(group)) return;
        kill(-group, SIGTERM);
        if (!await WaitUntilAsync(() => !GroupAlive(group), KillDelay)) kill(-group, SIGKILL);
    }

    // Le signal 0 n'envoie rien : il dit seulement si le groupe a encore un membre.
    static bool GroupAlive(int group) => kill(-group, 0) == 0;

    static async Task<bool> WaitUntilAsync(Func<bool> done, TimeSpan max)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed >= max) return false;
            await Task.Delay(100);
        }
        return true;
    }

    void DeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.LogWarning("The script file could not be removed: {Message}", e.Message); }
    }
}
