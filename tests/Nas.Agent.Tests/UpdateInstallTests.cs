using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Nas.Agent.Services;
using Nas.Contracts;
using Xunit;

namespace Nas.Agent.Tests;

public sealed class UpdateInstallTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-update-install-" + Guid.NewGuid().ToString("N"));

    public UpdateInstallTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    /// <summary>Construit un .tar.gz minimal contenant un seul fichier, sous un dossier « ursanas-{version}-linux-x64/ » comme la release réelle.</summary>
    static byte[] BuildTarGz(string version, string fileName, string content)
    {
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Gnu, leaveOpen: true))
        {
            var entry = new GnuTarEntry(TarEntryType.RegularFile, $"ursanas-{version}-linux-x64/{fileName}")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            };
            writer.WriteEntry(entry);
        }
        tar.Position = 0;
        using var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true)) tar.CopyTo(z);
        return gz.ToArray();
    }

    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> dispatch) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];

        public Handler(Func<HttpRequestMessage, HttpResponseMessage> sync) : this(req => Task.FromResult(sync(req))) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return await dispatch(request);
        }
    }

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    static HttpResponseMessage Bytes(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };

    (UpdateService Svc, FakeRunner Runner, Handler Http) Make(Func<HttpRequestMessage, HttpResponseMessage> dispatch, string version = "1.0.0")
    {
        File.WriteAllText(Path.Combine(_dir, "VERSION"), version);
        var script = Path.Combine(_dir, "update-apply.sh");
        File.WriteAllText(script, "#!/bin/bash\n");
        var runner = new FakeRunner { Handler = _ => new CommandResult(0, "", "") };
        var http = new Handler(dispatch);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Update:SettingsFile"] = Path.Combine(_dir, "settings.json"),
            ["Update:TokenFile"] = Path.Combine(_dir, "token"),
            ["Update:VersionFile"] = Path.Combine(_dir, "VERSION"),
            ["Update:RebootRequiredFile"] = Path.Combine(_dir, "reboot-required"),
            ["Update:WorkDir"] = Path.Combine(_dir, "work"),
            ["Update:ApplyScript"] = script,
        }).Build();
        return (new UpdateService(runner, cfg, NullLogger<UpdateService>.Instance, http), runner, http);
    }

    const string OneRelease = """
        [{"tag_name":"v1.2.3","draft":false,"prerelease":false,"html_url":"https://x/1.2.3","body":"n","published_at":"2026-05-01T10:00:00Z"}]
        """;

    static string ReleaseWithAssets(string tag) => $$"""
        {"tag_name":"{{tag}}","assets":[
          {"name":"ursanas-1.2.3-linux-x64.tar.gz","url":"https://api.github.com/assets/1"},
          {"name":"ursanas-1.2.3-linux-x64.tar.gz.sha256","url":"https://api.github.com/assets/2"}
        ]}
        """;

    HttpResponseMessage DefaultDispatch(HttpRequestMessage req, byte[] tar, string shaOverride = "")
    {
        var url = req.RequestUri!.ToString();
        if (url.Contains("/releases/tags/")) return Json(ReleaseWithAssets("v1.2.3"));
        if (url.Contains("/assets/1")) return Bytes(tar);
        if (url.Contains("/assets/2"))
        {
            var sha = shaOverride.Length > 0 ? shaOverride : Convert.ToHexStringLower(SHA256.HashData(tar));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{sha}  ursanas-1.2.3-linux-x64.tar.gz\n") };
        }
        return Json(OneRelease);
    }

    [Fact]
    public async Task Installer_sans_avoir_verifie_d_abord_est_refuse()
    {
        var (svc, _, _) = Make(_ => Json(OneRelease));
        var ex = await Assert.ThrowsAsync<OperationException>(() => svc.InstallAppAsync(default));
        Assert.Contains("Check for updates", ex.Message);
    }

    [Fact]
    public async Task Installer_alors_qu_aucune_mise_a_jour_n_est_disponible_est_refuse()
    {
        var (svc, _, _) = Make(_ => Json(OneRelease), version: "1.2.3"); // déjà à jour
        await svc.CheckAppAsync(default);
        var ex = await Assert.ThrowsAsync<OperationException>(() => svc.InstallAppAsync(default));
        Assert.Contains("Check for updates", ex.Message);
    }

    [Fact]
    public async Task Une_somme_de_controle_erronee_bloque_l_installation()
    {
        var tar = BuildTarGz("1.2.3", "install.sh", "echo hi");
        var (svc, runner, _) = Make(req => DefaultDispatch(req, tar, shaOverride: "0000000000000000000000000000000000000000000000000000000000000000"));
        await svc.CheckAppAsync(default);
        var ex = await Assert.ThrowsAsync<OperationException>(() => svc.InstallAppAsync(default));
        Assert.Contains("checksum", ex.Message);
        Assert.Empty(runner.Calls); // jamais arrivé jusqu'à systemd-run
        Assert.Equal("failed", svc.GetInstallStatus().Stage);
    }

    [Fact]
    public async Task Une_mise_a_jour_valide_est_telechargee_extraite_puis_lancee_detachee()
    {
        var tar = BuildTarGz("1.2.3", "install.sh", "echo hi");
        var (svc, runner, http) = Make(req => DefaultDispatch(req, tar));
        svc.SetSettings(new SetUpdateSettingsRequest("stable", "ghp_ABCDEFGHIJKLMNOPQRSTUVWX"));
        await svc.CheckAppAsync(default);
        await svc.InstallAppAsync(default);

        var call = Assert.Single(runner.Calls, c => c.File == "systemd-run");
        Assert.Contains("--collect", call.Args);
        Assert.Contains("/bin/bash", call.Args);
        var workDirArg = call.Args[^2];
        Assert.True(File.Exists(Path.Combine(workDirArg, "install.sh")));
        Assert.Equal("echo hi", File.ReadAllText(Path.Combine(workDirArg, "install.sh")));

        // le jeton part bien en en-tête, pas seulement lors de la vérification de version
        Assert.Contains(http.Requests, r => r.Headers.Authorization != null && r.Headers.Authorization.Parameter == "ghp_ABCDEFGHIJKLMNOPQRSTUVWX");
    }

    [Fact]
    public async Task Une_installation_deja_en_cours_est_refusee()
    {
        var tar = BuildTarGz("1.2.3", "install.sh", "echo hi");
        var gate = new TaskCompletionSource<bool>();
        File.WriteAllText(Path.Combine(_dir, "VERSION"), "1.0.0");
        File.WriteAllText(Path.Combine(_dir, "update-apply.sh"), "#!/bin/bash\n");
        var runner = new FakeRunner { Handler = _ => new CommandResult(0, "", "") };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Update:VersionFile"] = Path.Combine(_dir, "VERSION"),
            ["Update:WorkDir"] = Path.Combine(_dir, "work"),
            ["Update:ApplyScript"] = Path.Combine(_dir, "update-apply.sh"),
        }).Build();
        var http = new Handler(async req =>
        {
            if (req.RequestUri!.ToString().Contains("/assets/1")) await gate.Task; // attend vraiment, sans bloquer le thread appelant
            return DefaultDispatch(req, tar);
        });
        var svc = new UpdateService(runner, cfg, NullLogger<UpdateService>.Instance, http);
        await svc.CheckAppAsync(default);
        var first = svc.InstallAppAsync(default);
        await Task.Delay(100);
        await Assert.ThrowsAsync<OperationException>(() => svc.InstallAppAsync(default));
        gate.SetResult(true);
        await first;
    }

    [Fact]
    public async Task Le_script_d_installation_manquant_est_signale()
    {
        var tar = BuildTarGz("1.2.3", "install.sh", "echo hi");
        var (svc, runner, _) = Make(req => DefaultDispatch(req, tar));
        File.Delete(Path.Combine(_dir, "update-apply.sh"));
        await svc.CheckAppAsync(default);
        var ex = await Assert.ThrowsAsync<OperationException>(() => svc.InstallAppAsync(default));
        Assert.Contains("missing", ex.Message);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void L_etat_par_defaut_est_inactif()
    {
        var (svc, _, _) = Make(_ => Json(OneRelease));
        Assert.Equal("idle", svc.GetInstallStatus().Stage);
    }

    [Fact]
    public void L_etat_ecrit_par_le_script_bash_est_relu()
    {
        var (svc, _, _) = Make(_ => Json(OneRelease));
        var statusFile = Path.Combine(_dir, "work", "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statusFile)!);
        File.WriteAllText(statusFile, """{"stage":"rolled_back","version":"1.2.3","message":"The services did not respond: the previous version was restored","updatedAt":"2026-01-01T00:00:00Z"}""");
        var s = svc.GetInstallStatus();
        Assert.Equal(("rolled_back", "1.2.3"), (s.Stage, s.Version));
    }

    [Theory]
    [InlineData("samba", true)]
    [InlineData("libc6-dev", true)]
    [InlineData("docker.io", true)]
    [InlineData("", false)]
    [InlineData("Samba", false)]
    [InlineData("-x", false)]
    [InlineData("samba; rm -rf /", false)]
    [InlineData("samba ", false)]
    public void Validation_des_noms_de_paquets(string name, bool ok) => Assert.Equal(ok, Rules.IsPackageName(name));

    [Fact]
    public async Task Installer_des_paquets_sans_selection_est_refuse()
    {
        var (svc, _, _) = Make(_ => Json(OneRelease));
        var ex = await Assert.ThrowsAsync<OperationException>(() => svc.InstallPackagesAsync(new InstallPackagesRequest([]), default));
        Assert.Contains("selected", ex.Message);
    }

    [Fact]
    public async Task Un_paquet_absent_de_la_derniere_verification_est_refuse()
    {
        var runner = new FakeRunner { Handler = c => c.File == "apt" ? new CommandResult(0, "Listing...\nsamba/stable 2:4.22.4+dfsg-1 amd64 [upgradable from: 2:4.22.3+dfsg-1]\n", "") : new CommandResult(0, "", "") };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Update:VersionFile"] = Path.Combine(_dir, "VERSION") }).Build();
        var svc = new UpdateService(runner, cfg, NullLogger<UpdateService>.Instance, new Handler(_ => Json(OneRelease)));
        await svc.CheckPackagesAsync(default);
        var ex = await Assert.ThrowsAsync<OperationException>(() => svc.InstallPackagesAsync(new InstallPackagesRequest(["inconnu"]), default));
        Assert.Contains("last check", ex.Message);
    }

    [Fact]
    public async Task Un_paquet_connu_est_installe_puis_la_liste_est_rafraichie()
    {
        var calls = new List<string>();
        var listCalls = 0;
        var runner = new FakeRunner
        {
            Handler = c =>
            {
                calls.Add(c.File + " " + string.Join(' ', c.Args));
                if (c.File == "apt" && c.Args.SequenceEqual(["list", "--upgradable"]))
                    return ++listCalls == 1 ? new CommandResult(0, "Listing...\nsamba/stable 2:4.22.4+dfsg-1 amd64 [upgradable from: 2:4.22.3+dfsg-1]\n", "") : new CommandResult(0, "Listing...\n", "");
                return new CommandResult(0, "", "");
            },
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Update:VersionFile"] = Path.Combine(_dir, "VERSION") }).Build();
        var svc = new UpdateService(runner, cfg, NullLogger<UpdateService>.Instance, new Handler(_ => Json(OneRelease)));
        var first = await svc.CheckPackagesAsync(default);
        Assert.Single(first.Items);

        await svc.InstallPackagesAsync(new InstallPackagesRequest(["samba"]), default);
        Assert.Empty(svc.Status().Packages!.Items);
        Assert.Contains(calls, c => c == "apt-get install -y -qq --only-upgrade samba");
    }
}
