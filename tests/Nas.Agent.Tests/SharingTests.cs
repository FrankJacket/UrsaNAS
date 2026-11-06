using Microsoft.Extensions.Configuration;
using Nas.Agent.Services;
using Nas.Contracts;
using Xunit;

namespace Nas.Agent.Tests;

sealed class FakeRunner : ICommandRunner
{
    public record Call(string File, string[] Args, string? Stdin);
    public List<Call> Calls { get; } = [];
    public Func<Call, CommandResult>? Handler { get; set; }

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default)
    {
        var call = new Call(fileName, args.ToArray(), stdin);
        Calls.Add(call);
        return Task.FromResult(Handler?.Invoke(call) ?? new CommandResult(0, "", ""));
    }
}

public class RulesTests
{
    [Theory]
    [InlineData("photos", true)]
    [InlineData("Docs_2024-b", true)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    [InlineData("../etc", false)]
    [InlineData("global", false)]
    [InlineData("HOMES", false)]
    [InlineData("-x", false)]
    public void Nom_de_partage(string name, bool ok) => Assert.Equal(ok, Rules.ValidateShareName(name) is null);

    [Theory]
    [InlineData("alice", true)]
    [InlineData("a_b-1", true)]
    [InlineData("Alice", false)]
    [InlineData("1abc", false)]
    [InlineData("root", false)]
    [InlineData("a;rm", false)]
    public void Nom_utilisateur(string name, bool ok) => Assert.Equal(ok, Rules.ValidateUserName(name) is null);

    [Theory]
    [InlineData("12345678", true)]
    [InlineData("court", false)]
    [InlineData("bon mot de passe", true)]
    [InlineData("avec\nretour", false)]
    public void Mot_de_passe(string pw, bool ok) => Assert.Equal(ok, Rules.ValidatePassword(pw) is null);

    [Fact]
    public void Partages_refuse_doublons_et_utilisateurs_inconnus()
    {
        var known = new HashSet<string> { "alice" };
        var a = new ShareDef("docs", "", false, ["alice"]);
        Assert.Null(Rules.ValidateShares([a], known));
        Assert.Contains("duplicated", Rules.ValidateShares([a, a with { Name = "DOCS" }], known));
        Assert.Contains("unknown", Rules.ValidateShares([a with { ValidUsers = ["bob"] }], known));
        Assert.Contains("at least one", Rules.ValidateShares([a with { ValidUsers = [] }], known));
        Assert.Contains("control characters", Rules.ValidateShares([a with { Comment = "x\ny" }], known));
    }
}

public class ShareConfTests
{
    [Fact]
    public void Rend_un_partage()
    {
        var text = ShareConf.Render([new ShareDef("docs", "Documents 100%", true, ["alice", "bob"])], "/srv/nas");
        Assert.Contains("[docs]", text);
        Assert.Contains("path = /srv/nas/docs", text);
        Assert.Contains("comment = Documents 100%%", text);
        Assert.Contains("read only = yes", text);
        Assert.Contains("valid users = alice bob", text);
        Assert.Contains("guest ok = no", text);
    }

    [Fact]
    public void Include_insere_apres_global_une_seule_fois()
    {
        var conf = "[global]\n   workgroup = X\n\n[homes]\n   browseable = no\n";
        var once = ShareConf.EnsureInclude(conf, "/etc/samba/ursanas.conf");

        // À la fin : sinon les sections du fichier inclus couperaient [global] en deux.
        Assert.StartsWith("[global]\n   workgroup = X\n\n[homes]\n   browseable = no\n", once);
        Assert.EndsWith("include = /etc/samba/ursanas.conf\n", once);
        Assert.Equal(once, ShareConf.EnsureInclude(once, "/etc/samba/ursanas.conf"));
    }

    [Fact]
    public void Include_mal_place_par_une_version_precedente_est_deplace_a_la_fin()
    {
        var bad = "[global]\n   include = /etc/samba/ursanas.conf\n   workgroup = X\n\n[homes]\n   browseable = no\n";
        var fixedConf = ShareConf.EnsureInclude(bad, "/etc/samba/ursanas.conf");

        Assert.StartsWith("[global]\n   workgroup = X\n", fixedConf);
        Assert.EndsWith("include = /etc/samba/ursanas.conf\n", fixedConf);
        Assert.Single(fixedConf.Split('\n'), l => l.Contains("ursanas.conf"));
        Assert.Equal(fixedConf, ShareConf.EnsureInclude(fixedConf, "/etc/samba/ursanas.conf"));
    }

    [Fact]
    public void Include_fonctionne_aussi_sur_un_fichier_sans_global() =>
        Assert.EndsWith("include = /x\n", ShareConf.EnsureInclude("[homes]\n", "/x"));
}

public class UserServiceTests
{
    static CommandResult Ok(string stdout = "") => new(0, stdout, "");
    static CommandResult Fail(string stderr = "") => new(1, "", stderr);

    [Fact]
    public async Task Liste_les_membres_et_les_comptes_dont_le_groupe_principal_est_nasusers()
    {
        var run = new FakeRunner
        {
            Handler = c => (c.File, c.Args.Length > 0 ? c.Args[0] : "") switch
            {
                ("getent", "group") => Ok("nasusers:x:990:bob\n"),
                ("getent", "passwd") => Ok("root:x:0:0::/root:/bin/bash\nalice:x:1001:990::/:/usr/sbin/nologin\n"),
                _ => Ok(),
            },
        };
        var users = await new UserService(run).ListAsync();
        Assert.Equal(["alice", "bob"], users.Select(u => u.Name));
    }

    [Fact]
    public async Task Creation_met_le_mot_de_passe_sur_stdin_et_jamais_dans_les_arguments()
    {
        var run = new FakeRunner
        {
            Handler = c => (c.File, c.Args.Length > 0 ? c.Args[0] : "") switch
            {
                ("getent", "group") => Ok("nasusers:x:990:\n"),
                ("id", _) => Fail(),
                _ => Ok(),
            },
        };
        await new UserService(run).CreateAsync(new CreateUserRequest("alice", "secret-pw-1"));

        var useradd = Assert.Single(run.Calls, c => c.File == "useradd");
        Assert.Contains("--no-create-home", useradd.Args);
        Assert.Contains("/usr/sbin/nologin", useradd.Args);
        var smb = Assert.Single(run.Calls, c => c.File == "smbpasswd");
        Assert.Equal("secret-pw-1\nsecret-pw-1\n", smb.Stdin);
        Assert.DoesNotContain(run.Calls, c => c.Args.Any(a => a.Contains("secret-pw-1")));
    }

    [Fact]
    public async Task Creation_refuse_un_compte_systeme_existant()
    {
        var run = new FakeRunner
        {
            Handler = c => (c.File, c.Args.Length > 0 ? c.Args[0] : "") switch
            {
                ("getent", "group") => Ok("nasusers:x:990:\n"),
                ("id", _) => Ok("1000"),
                _ => Ok(),
            },
        };
        var ex = await Assert.ThrowsAsync<OperationException>(() =>
            new UserService(run).CreateAsync(new CreateUserRequest("daemon2", "12345678")));
        Assert.Contains("system account", ex.Message);
        Assert.DoesNotContain(run.Calls, c => c.File is "useradd" or "smbpasswd");
    }

    [Fact]
    public async Task Echec_de_smbpasswd_supprime_le_compte_cree()
    {
        var run = new FakeRunner
        {
            Handler = c => (c.File, c.Args.Length > 0 ? c.Args[0] : "") switch
            {
                ("getent", "group") => Ok("nasusers:x:990:\n"),
                ("id", _) => Fail(),
                ("smbpasswd", _) => Fail("boom"),
                _ => Ok(),
            },
        };
        await Assert.ThrowsAsync<OperationException>(() =>
            new UserService(run).CreateAsync(new CreateUserRequest("alice", "12345678")));
        Assert.Contains(run.Calls, c => c.File == "userdel" && c.Args[0] == "alice");
    }

    [Fact]
    public async Task Suppression_refuse_un_compte_non_gere()
    {
        var run = new FakeRunner { Handler = c => c.File == "getent" && c.Args[0] == "group" ? Ok("nasusers:x:990:\n") : Ok() };
        await Assert.ThrowsAsync<OperationException>(() => new UserService(run).DeleteAsync("postgres"));
        Assert.DoesNotContain(run.Calls, c => c.File == "userdel");
    }
}

public class ShareServiceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-share-" + Guid.NewGuid().ToString("N"));
    readonly FakeRunner _run = new();
    readonly ShareService _svc;

    public ShareServiceTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "smb.conf"), "[global]\n   workgroup = X\n");
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Samba:ConfigDir"] = _dir,
            ["Samba:ShareRoot"] = Path.Combine(_dir, "shares"),
        }).Build();
        _run.Handler = c => (c.File, c.Args.Length > 0 ? c.Args[0] : "") switch
        {
            ("getent", "group") => new(0, "nasusers:x:990:alice\n", ""),
            _ => new(0, "", ""),
        };
        _svc = new ShareService(_run, new UserService(_run), cfg);
    }

    string Conf => File.ReadAllText(Path.Combine(_dir, "ursanas.conf"));

    [Fact]
    public async Task Applique_les_partages_cree_les_dossiers_et_recharge_samba()
    {
        await _svc.ApplyAsync([new ShareDef("docs", "", false, ["alice"])]);

        Assert.Contains("[docs]", Conf);
        Assert.Contains("include =", File.ReadAllText(Path.Combine(_dir, "smb.conf")));
        Assert.True(File.Exists(Path.Combine(_dir, "smb.conf.ursanas-bak")));
        Assert.True(Directory.Exists(Path.Combine(_dir, "shares", "docs")));
        Assert.Contains(_run.Calls, c => c.File == "chmod" && c.Args[0] == "2770");
        // Le dossier racine doit rester traversable (sinon aucun utilisateur n'atteint son partage).
        Assert.Contains(_run.Calls, c => c.File == "chmod" && c.Args[0] == "0755" && c.Args[1].EndsWith("shares"));

        // L'include est la dernière ligne de smb.conf, jamais dans [global].
        var main = File.ReadAllText(Path.Combine(_dir, "smb.conf"));
        Assert.StartsWith("[global]\n   workgroup = X", main);
        Assert.EndsWith("ursanas.conf\n", main);
        Assert.Contains(_run.Calls, c => c.File == "testparm");
        Assert.Contains(_run.Calls, c => c.File == "smbcontrol" && c.Args[0] == "all");
    }

    [Fact]
    public async Task Retour_arriere_si_testparm_refuse()
    {
        await _svc.ApplyAsync([new ShareDef("docs", "", false, ["alice"])]);
        var good = Conf;

        var old = _run.Handler;
        _run.Handler = c => c.File == "testparm" ? new(1, "", "erreur de syntaxe") : old!(c);
        var ex = await Assert.ThrowsAsync<OperationException>(() =>
            _svc.ApplyAsync([new ShareDef("docs", "", false, ["alice"]), new ShareDef("autre", "", false, ["alice"])]));

        Assert.Contains("erreur de syntaxe", ex.Message);
        Assert.Equal(good, Conf);
    }

    [Fact]
    public async Task Refuse_un_utilisateur_inconnu_sans_rien_ecrire()
    {
        await Assert.ThrowsAsync<OperationException>(() => _svc.ApplyAsync([new ShareDef("docs", "", false, ["mallory"])]));
        Assert.False(File.Exists(Path.Combine(_dir, "ursanas.conf")));
        Assert.DoesNotContain(_run.Calls, c => c.File == "chmod");
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
