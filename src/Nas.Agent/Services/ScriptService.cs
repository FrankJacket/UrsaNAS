using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>The end of a program's output: at most <paramref name="capacity"/> bytes, the oldest ones are dropped.</summary>
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

    /// <summary>The kept text; “[…]” on the first line means the beginning was cut off.</summary>
    public override string ToString()
    {
        lock (_buf)
        {
            var bytes = new byte[_count];
            var first = Math.Min(_count, _buf.Length - _start);
            _buf.AsSpan(_start, first).CopyTo(bytes);
            _buf.AsSpan(0, _count - first).CopyTo(bytes.AsSpan(first));
            var skip = 0;
            if (_truncated) while (skip < bytes.Length && (bytes[skip] & 0xC0) == 0x80) skip++; // a UTF-8 character cut in half
            var text = Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip).Replace("\0", "");
            return _truncated ? "[…]\n" + text : text;
        }
    }
}

/// <summary>
/// Runs the scripts of scheduled tasks. Each script is written to a temporary file that only its account can read, started in its own
/// session (so that its child processes can be stopped too), then the file is deleted. Runs live in memory: restarting the agent
/// stops and forgets them, and the web interface then records them as interrupted.
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

    /// <summary>The unit of the time limit (one minute); tests shorten it.</summary>
    public TimeSpan TimeoutUnit { get; set; } = TimeSpan.FromMinutes(1);

    public ScriptService(UserService users, ICommandRunner run, IConfiguration cfg, ILogger<ScriptService> log, IHostApplicationLifetime life)
    {
        (_users, _run, _cfg, _log) = (users, run, cfg, log);
        life.ApplicationStopping.Register(() => { foreach (var s in _runs.Values) s.Stop.Cancel(); });
        RemoveLeftovers();
    }

    string Folder => _cfg["Scripts:Directory"] ?? "/run/ursanas-scripts";
    TimeSpan KillDelay => TimeSpan.FromSeconds(_cfg.GetValue("Scripts:KillDelaySeconds", 5));

    /// <summary>Files that an abrupt stop of the agent may have left behind.</summary>
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

    /// <summary>The web interface has recorded the end of the script: the agent can forget it.</summary>
    public void Acknowledge(string id)
    {
        if (_runs.TryGetValue(id, out var s) && s.Status.State != ScriptState.Running) _runs.TryRemove(id, out _);
    }

    /// <summary>Starts the script and returns as soon as the process is running.</summary>
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

    /// <summary>Only “root” and NAS users are accepted, never any other system account.</summary>
    async Task<Account> ResolveAccountAsync(string name, CancellationToken ct)
    {
        if (name != ScriptRules.Root && !(await _users.ListAsync(ct)).Any(u => u.Name == name))
            throw new OperationException(Msg.F("User “{0}” does not exist.", name));
        var r = await _run.RunAsync("getent", ["passwd", name], ct: ct);
        var f = r.StdOut.Trim().Split(':');
        if (r.ExitCode != 0 || f.Length < 6 || f[0] != name || !uint.TryParse(f[2], out var uid) || !uint.TryParse(f[3], out var gid))
            throw new OperationException(Msg.F("User “{0}” does not exist.", name));
        // A NAS account must never have the identity of root.
        if (name != ScriptRules.Root && uid == 0) throw new OperationException(Msg.F("“{0}” is an existing system account: it cannot be used.", name));
        return new Account(name, uid, gid, f[5]);
    }

    [SupportedOSPlatform("linux")]
    void WriteScript(string path, string script, Account account)
    {
        // The folder can be traversed but not listed; each file is readable only by the account that runs it.
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
        // “setsid”: the script leads a new session, so its process id is also the id of the group to stop.
        // “setpriv”: leaves the agent's identity and groups for those of the chosen account.
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
        // Errors join standard output so that lines stay in order.
        foreach (var a in new[] { "/bin/bash", "-c", "umask 022; exec /bin/bash \"$0\" 2>&1", path }) psi.ArgumentList.Add(a);

        psi.Environment.Clear(); // nothing from the service's environment
        psi.Environment["HOME"] = account.Home;
        psi.Environment["USER"] = account.Name;
        psi.Environment["LOGNAME"] = account.Name;
        psi.Environment["SHELL"] = "/bin/bash";
        psi.Environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
        psi.Environment["LANG"] = "C.UTF-8";

        var process = Process.Start(psi) ?? throw new IOException("The shell could not be started.");
        process.StandardInput.Close(); // a script waiting for input gets an end of file instead of hanging
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

            // A finished script leaves nothing behind: after a short delay, the processes remaining in its group are stopped.
            await WaitUntilAsync(() => !GroupAlive(group), TimeSpan.FromSeconds(1));
            await EndGroupAsync(group);
            // A deliberately detached process can keep the output open: we do not wait for it.
            await Task.WhenAny(output, Task.Delay(TimeSpan.FromSeconds(2)));

            int? code = process.HasExited ? process.ExitCode : null;
            var result = ended ?? (code == 0 ? ScriptState.Succeeded : ScriptState.Failed);
            DeleteFile(path); // before announcing the end
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

    /// <summary>Stop requested or time limit exceeded: SIGTERM to the group, then SIGKILL to whatever resists, descendants included.</summary>
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

    // Signal 0 sends nothing: it only tells whether the group still has a member.
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
