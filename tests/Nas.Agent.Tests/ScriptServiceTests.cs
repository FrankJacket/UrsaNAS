using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent.Tests;

public class ScriptRulesTests
{
    [Theory]
    [InlineData("echo ok", "root", 60, true)]
    [InlineData("echo ok", "alice", 10080, true)]
    [InlineData("  \n ", "root", 60, false)]
    [InlineData("echo ok", "", 60, false)] // aucun compte par défaut
    [InlineData("echo ok", "a b", 60, false)]
    [InlineData("echo ok", "root", 0, false)]
    [InlineData("echo ok", "root", 10081, false)]
    [InlineData("echo \0", "root", 60, false)]
    public void Valide_le_script_le_compte_et_la_duree(string script, string runAs, int minutes, bool ok) =>
        Assert.Equal(ok, ScriptRules.Validate(script, runAs, minutes) is null);

    [Fact]
    public void Refuse_un_script_trop_long_et_convertit_les_fins_de_ligne()
    {
        Assert.NotNull(ScriptRules.Validate(new string('a', ScriptRules.MaxScriptLength + 1), "root", 60));
        Assert.Equal("a\nb\nc\n", ScriptRules.Normalize("a\r\nb\rc\n"));
    }
}

public class OutputTailTests
{
    static string Tail(int capacity, params string[] chunks)
    {
        var tail = new OutputTail(capacity);
        foreach (var c in chunks) tail.Append(Encoding.UTF8.GetBytes(c));
        return tail.ToString();
    }

    [Theory]
    [InlineData("abcdefgh", "abc", "defgh")]
    [InlineData("[…]\ncdefghij", "abc", "defghij")]
    [InlineData("[…]\n23456789", "0123456789")]
    [InlineData("[…]\n3456789a", "01234", "56789", "a")]
    [InlineData("12345678", "12345678")]
    public void Garde_la_fin(string expected, params string[] chunks) => Assert.Equal(expected, Tail(8, chunks));

    [Fact]
    public void Ne_coupe_pas_un_caractere() => Assert.Equal("[…]\néé", Tail(5, "ééé")); // le 1er « é » est à moitié sorti
}

/// <summary>De vrais scripts Bash, sous le compte qui lance les tests (ignorés hors de Linux).</summary>
public class ScriptServiceTests : IDisposable
{
    [DllImport("libc")] static extern uint geteuid();
    [DllImport("libc")] static extern uint getegid();

    /// <summary>« getent » : le compte des tests est le seul utilisateur du NAS.</summary>
    sealed class Accounts(string me, string home) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default)
        {
            var uid = OperatingSystem.IsLinux() ? geteuid() : 0;
            var gid = OperatingSystem.IsLinux() ? getegid() : 0;
            var line = $"{me}:x:{uid}:{gid}::{home}:/bin/bash\n";
            return Task.FromResult(string.Join(' ', args) switch
            {
                "group nasusers" => new CommandResult(0, $"nasusers:x:60000:{me}\n", ""),
                "passwd" => new CommandResult(0, line, ""),
                var a when a == "passwd " + me => new CommandResult(0, line, ""),
                _ => new CommandResult(2, "", ""),
            });
        }
    }

    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-scripts-" + Guid.NewGuid().ToString("N"));
    readonly string _me = Environment.UserName;

    public ScriptServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }

    string Folder => Path.Combine(_dir, "run");

    ScriptService Service()
    {
        var run = new Accounts(_me, Path.Combine(_dir, "nulle-part"));
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Scripts:Directory"] = Folder, ["Scripts:KillDelaySeconds"] = "1" }).Build();
        return new ScriptService(new UserService(run), run, cfg, NullLogger<ScriptService>.Instance, new StubLifetime());
    }

    static string NewId() => Guid.NewGuid().ToString("N");

    async Task<string> StartAsync(ScriptService s, string script, int minutes = 1)
    {
        var id = NewId();
        await s.StartAsync(new ScriptRunRequest(id, script, _me, minutes));
        return id;
    }

    static async Task<ScriptRunStatus> EndOfAsync(ScriptService s, string id)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            var status = s.Statuses().Single(x => x.Id == id);
            if (status.State != ScriptState.Running) return status;
            await Task.Delay(50);
        }
        throw new TimeoutException("Le script ne se termine pas.");
    }

    static bool Alive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[stat.LastIndexOf(')') + 2] != 'Z';
        }
        catch (IOException) { return false; }
    }

    [Fact]
    public async Task La_sortie_et_les_erreurs_sont_recueillies_et_le_fichier_est_supprime()
    {
        if (!OperatingSystem.IsLinux()) return;
        var s = Service();
        Environment.SetEnvironmentVariable("URSANAS_TEST_SECRET", "visible");
        var id = await StartAsync(s, "echo un\r\necho deux >&2\r\necho \"$USER $PWD [$URSANAS_TEST_SECRET]\"\r\nstat -c %a \"$0\"\r\nread x || echo sans-saisie\r\n");
        var end = await EndOfAsync(s, id);

        Assert.Equal((ScriptState.Succeeded, 0), (end.State, end.ExitCode));
        // Dossier personnel absent : le script démarre dans « / ». L'environnement du service n'est pas transmis.
        Assert.Equal($"un\ndeux\n{_me} / []\n400\nsans-saisie\n", end.Output);
        Assert.Empty(Directory.GetFiles(Folder));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute,
            File.GetUnixFileMode(Folder));

        s.Acknowledge(id);
        Assert.Empty(s.Statuses());
    }

    [Fact]
    public async Task Un_code_de_sortie_different_de_zero_est_un_echec()
    {
        if (!OperatingSystem.IsLinux()) return;
        var s = Service();
        var end = await EndOfAsync(s, await StartAsync(s, "echo avant\nexit 3\n"));
        Assert.Equal((ScriptState.Failed, 3, "avant\n"), (end.State, end.ExitCode, end.Output));
    }

    [Fact]
    public async Task La_limite_de_duree_arrete_aussi_les_processus_enfants()
    {
        if (!OperatingSystem.IsLinux()) return;
        var s = Service();
        s.TimeoutUnit = TimeSpan.FromSeconds(1);
        var end = await EndOfAsync(s, await StartAsync(s, "sleep 30 &\necho $!\nwait\n"));

        Assert.Equal((ScriptState.TimedOut, 1), (end.State, end.TimeoutMinutes));
        Assert.False(Alive(int.Parse(end.Output.Trim())));
    }

    [Fact]
    public async Task Arreter_force_un_script_qui_ignore_la_demande()
    {
        if (!OperatingSystem.IsLinux()) return;
        var s = Service();
        var ready = Path.Combine(_dir, "pret");
        var id = await StartAsync(s, $"trap '' TERM\nsleep 30 &\necho $!\ntouch '{ready}'\nwait\n");
        for (var i = 0; i < 100 && !File.Exists(ready); i++) await Task.Delay(50);
        s.Acknowledge(id); // sans effet : il tourne encore
        s.Cancel(id);
        var end = await EndOfAsync(s, id);

        Assert.Equal(ScriptState.Stopped, end.State);
        Assert.False(Alive(int.Parse(end.Output.Trim())));
    }

    [Fact]
    public async Task Un_script_termine_ne_laisse_pas_de_processus_derriere_lui()
    {
        if (!OperatingSystem.IsLinux()) return;
        var s = Service();
        var end = await EndOfAsync(s, await StartAsync(s, "sleep 30 &\necho $!\n"));

        Assert.Equal(ScriptState.Succeeded, end.State);
        Assert.False(Alive(int.Parse(end.Output.Trim())));
    }

    [Fact]
    public async Task Refuse_un_compte_inconnu_et_un_identifiant_invalide()
    {
        if (!OperatingSystem.IsLinux()) return;
        var s = Service();
        await Assert.ThrowsAsync<OperationException>(() => s.StartAsync(new ScriptRunRequest(NewId(), "echo", "daemon", 1))); // pas un utilisateur du NAS
        await Assert.ThrowsAsync<OperationException>(() => s.StartAsync(new ScriptRunRequest("../x", "echo", _me, 1)));
        Assert.Empty(s.Statuses());
    }

    [Fact]
    public void Les_fichiers_laisses_par_un_arret_brutal_sont_supprimes_au_demarrage()
    {
        Directory.CreateDirectory(Folder);
        var old = Path.Combine(Folder, NewId() + ".sh");
        File.WriteAllText(old, "echo");
        Service();
        Assert.False(File.Exists(old));
    }
}
