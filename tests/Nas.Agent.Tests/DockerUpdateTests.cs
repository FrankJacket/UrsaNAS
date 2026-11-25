using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Nas.Agent.Services;
using Nas.Contracts;
using Nas.Web;
using Nas.Web.Data;

namespace Nas.Agent.Tests;

public class ImageReferenceTests
{
    [Theory]
    [InlineData("nginx", "registry-1.docker.io", "library/nginx", "latest")]
    [InlineData("nginx:1.27-alpine", "registry-1.docker.io", "library/nginx", "1.27-alpine")]
    [InlineData("jellyfin/jellyfin", "registry-1.docker.io", "jellyfin/jellyfin", "latest")]
    [InlineData("docker.io/library/redis:7", "registry-1.docker.io", "library/redis", "7")]
    [InlineData("ghcr.io/user/app:v1", "ghcr.io", "user/app", "v1")]
    [InlineData("registry.local:5000/app", "registry.local:5000", "app", "latest")]
    [InlineData("localhost/app:2", "localhost", "app", "2")]
    public void Decompose_comme_docker(string image, string registry, string repository, string tag) =>
        Assert.Equal(new ImageReference(registry, repository, tag), ImageReference.Parse(image));

    [Theory]
    [InlineData("nginx@sha256:abc")]
    [InlineData("-x")]
    [InlineData("ghcr.io/../x")]
    [InlineData("nginx:")]
    public void Refuse_ce_qui_ne_peut_pas_etre_verifie(string image) => Assert.Null(ImageReference.Parse(image));
}

public class RegistryClientTests
{
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<(string Method, string Url, string? Auth)> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
            return Task.FromResult(reply(request));
        }
    }

    static readonly ImageReference Nginx = new("registry-1.docker.io", "library/nginx", "latest");

    static HttpResponseMessage WithDigest()
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK);
        r.Headers.Add("Docker-Content-Digest", "sha256:abc");
        return r;
    }

    static HttpResponseMessage Challenge(string realm)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        r.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer", $"realm=\"{realm}\",service=\"registry.docker.io\",scope=\"repository:library/nginx:pull\""));
        return r;
    }

    [Fact]
    public async Task Demande_un_jeton_anonyme_puis_lit_l_empreinte_sans_rien_telecharger()
    {
        var h = new Handler(q =>
            q.RequestUri!.Host == "auth.docker.io" ? new(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"t0k\"}") }
            : q.Headers.Authorization is null ? Challenge("https://auth.docker.io/token")
            : WithDigest());

        Assert.Equal(new RegistryDigest("sha256:abc", null), await new RegistryClient(h).DigestAsync(Nginx));

        Assert.Equal(["HEAD", "GET", "HEAD"], h.Calls.Select(c => c.Method).ToArray());
        Assert.Equal("https://registry-1.docker.io/v2/library/nginx/manifests/latest", h.Calls[0].Url);
        Assert.Contains("service=registry.docker.io", h.Calls[1].Url);
        Assert.Equal("Bearer t0k", h.Calls[2].Auth);
    }

    [Fact]
    public async Task Ne_suit_pas_un_serveur_de_jetons_sans_https()
    {
        var h = new Handler(_ => Challenge("http://evil/token"));
        var result = await new RegistryClient(h).DigestAsync(Nginx);
        Assert.Null(result.Digest);
        Assert.Single(h.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "does not give access")]
    [InlineData(HttpStatusCode.TooManyRequests, "more requests")]
    [InlineData(HttpStatusCode.InternalServerError, "answered 500")]
    [InlineData(HttpStatusCode.OK, "fingerprint")] // sans l'en-tête de l'empreinte
    public async Task Une_reponse_inutilisable_donne_une_raison(HttpStatusCode status, string reason)
    {
        var result = await new RegistryClient(new Handler(_ => new(status))).DigestAsync(Nginx);
        Assert.Null(result.Digest);
        Assert.Contains(reason, result.Error);
    }

    [Fact]
    public async Task Un_registre_injoignable_donne_une_raison()
    {
        var result = await new RegistryClient(new Handler(_ => throw new HttpRequestException("no route"))).DigestAsync(Nginx);
        Assert.Contains("could not be reached", result.Error);
    }
}

public sealed class ImageUpdateOperationTests : IDisposable
{
    sealed class Admin : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], "test"))));
    }

    sealed class Agent : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Calls.Add(path);
            return Task.FromResult(path switch
            {
                "docker/containers/1/update-image" => new HttpResponseMessage(HttpStatusCode.BadRequest),
                "docker/containers/2/update-image" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ContainerUpdateResult(true)) },
                "docker/images/remove-replaced" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new RemovedImagesResult(1)) },
                _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            });
        }
    }

    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-image-ops-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task Met_a_jour_les_conteneurs_seuls_puis_chaque_stack_une_fois_meme_apres_un_echec()
    {
        var db = new Db(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = _dir }).Build());
        db.Init();
        var repo = new Repo(db);
        var agent = new Agent();
        var ops = new DockerOperations(new AgentClient(new HttpClient(agent) { BaseAddress = new Uri("http://agent/") }), repo, new Admin());

        var results = await ops.UpdateImageUsersAsync(
        [
            new("9", "blog-web", "nginx", "blog", ImageUpdateState.UpdateAvailable),
            new("1", "casse", "nginx", null, ImageUpdateState.UpdateAvailable),
            new("2", "ng", "nginx", null, ImageUpdateState.UpdateAvailable, null, "sha256:old"),
            new("8", "blog-proxy", "nginx", "blog", ImageUpdateState.Recreate),
        ]);

        // le ménage de l'ancienne image vient après toutes les mises à jour
        Assert.Equal(["docker/containers/1/update-image", "docker/containers/2/update-image", "docker/stacks/blog/update", "docker/images/remove-replaced"],
            agent.Calls.ToArray());
        Assert.Equal(["casse", "ng", "blog"], results.Select(r => r.Name).ToArray());
        Assert.NotNull(results[0].Error);
        Assert.True(results[1].Updated && results[2].Updated);
        Assert.Equal(3, (await repo.GetAuditAsync()).Count()); // deux mises à jour et le ménage ; rien pour l'échec
    }
}

public sealed class DockerUpdateTests : IDisposable
{
    const string Old = "sha256:old", New = "sha256:new";

    sealed record Local(string Id, params string[] Digests);

    // Un Docker minimal : des conteneurs (nom, image demandée, image utilisée) et les images présentes sur le NAS.
    sealed class Docker : ICommandRunner
    {
        public List<(string Id, string Name, string Image, string Used)> Containers { get; } = [];
        public Dictionary<string, Local> Images { get; } = new();
        public Local? AfterPull; // ce que « docker pull » apporte
        public string Labels = "{}";
        public bool RunFails;
        public List<string[]> Calls { get; } = [];

        static CommandResult Out(string text) => new(0, text, "");

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> a, string? stdin = null, CancellationToken ct = default)
        {
            Calls.Add(a.ToArray());
            return Task.FromResult(Reply(a));
        }

        CommandResult Reply(IReadOnlyList<string> a)
        {
            if (a[0] == "ps")
                return Out(string.Concat(Containers.Select(c => JsonSerializer.Serialize(new { ID = c.Id, Names = c.Name, c.Image, State = "running", Labels = "" }) + "\n")));
            if (a[0] == "inspect" && !a.Contains("--format"))
                return Out(JsonSerializer.Serialize(Containers.Select(c => new { Id = c.Id, Image = c.Used, Config = new { c.Image } })));
            if (a[0] == "inspect")
            {
                var c = Containers.Single(x => x.Id == a[^1]);
                return Out(JsonSerializer.Serialize(new
                {
                    Name = "/" + c.Name, Image = c.Used, State = new { Running = true },
                    Config = new { c.Image, Env = new[] { "PATH=/usr/bin", "TZ=UTC" }, Labels = JsonSerializer.Deserialize<JsonElement>(Labels) },
                    HostConfig = new { RestartPolicy = new { Name = "always" }, PortBindings = new Dictionary<string, object>() },
                    Mounts = new[] { new { Type = "volume", Name = "data", Destination = "/data", RW = true } },
                    NetworkSettings = new { Networks = new Dictionary<string, object> { ["bridge"] = new { } } },
                }));
            }
            if (a[0] == "image" && a[1] == "inspect")
            {
                if (a[^1].StartsWith("sha256:", StringComparison.Ordinal)) return Out("""{"Config":{"Env":["PATH=/usr/bin"]}}""");
                return Images.TryGetValue(a[^1], out var i)
                    ? Out(JsonSerializer.Serialize(new { i.Id, RepoDigests = i.Digests }))
                    : new(1, "", "No such image");
            }
            if (a[0] == "pull" && AfterPull is not null) Images[a[1]] = AfterPull;
            if (a[0] == "run" && RunFails) return new(1, "", "port is already allocated");
            return Out("");
        }
    }

    sealed class Registry(Func<ImageReference, RegistryDigest> reply) : IRegistryClient
    {
        public int Calls;
        public Task<RegistryDigest> DigestAsync(ImageReference image, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(reply(image));
        }
    }

    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-docker-upd-" + Guid.NewGuid().ToString("N"));

    IConfiguration Cfg() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Docker:StacksDir"] = Path.Combine(_dir, "stacks"), ["Docker:UpdatesFile"] = Path.Combine(_dir, "updates.json"),
    }).Build();

    DockerService Svc(ICommandRunner r) => new(r, Cfg());
    DockerUpdateService Updates(ICommandRunner r, IRegistryClient registry) => new(Svc(r), registry, Cfg());

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public async Task Classe_chaque_conteneur_selon_son_image()
    {
        var d = new Docker();
        void Add(string id, string image, string used, Local? local)
        {
            d.Containers.Add((id, "c" + id, image, used));
            if (local is not null) d.Images[image] = local;
        }
        Add("1", "nginx:latest", Old, new(Old, "nginx@sha256:aaa"));      // le registre a autre chose
        Add("2", "redis:7", Old, new(Old, "redis@sha256:same"));          // identique
        Add("3", "mariadb:11", Old, new(New, "mariadb@sha256:same"));     // image téléchargée, conteneur ancien
        Add("4", "caddy@sha256:abc", Old, null);                          // épinglée
        Add("5", "maison:latest", Old, new(Old));                         // construite sur place
        Add("6", "prive/app:1", Old, new(Old, "prive/app@sha256:aaa"));   // refusée par le registre
        Add("7", "parti:1", Old, null);                                   // plus sur le NAS
        var registry = new Registry(i => i.Repository == "prive/app" ? new(null, "refused") : new("sha256:same", null));

        var report = await Updates(d, registry).CheckAsync();

        Assert.Equal(
            [ImageUpdateState.UpdateAvailable, ImageUpdateState.UpToDate, ImageUpdateState.Recreate, ImageUpdateState.Pinned,
                ImageUpdateState.Unverifiable, ImageUpdateState.Unverifiable, ImageUpdateState.Unverifiable],
            report.Containers.OrderBy(c => c.ContainerId).Select(c => c.State).ToArray());
        Assert.Equal("refused", report.Containers.Single(c => c.ContainerId == "6").Reason);
        Assert.Equal(4, registry.Calls); // ni l'image épinglée, ni la locale, ni l'absente
        Assert.NotNull(report.CheckedAtUtc);
    }

    [Fact]
    public async Task Le_rapport_ne_reinterroge_pas_les_registres_et_suit_l_etat_de_docker()
    {
        var d = new Docker();
        d.Containers.Add(("1", "web", "nginx:latest", Old));
        d.Images["nginx:latest"] = new(Old, "nginx@sha256:aaa");
        var registry = new Registry(_ => new("sha256:bbb", null));

        var never = await Updates(d, registry).ReportAsync();
        Assert.Null(never.CheckedAtUtc);
        Assert.Empty(never.Containers);

        await Updates(d, registry).CheckAsync();
        d.Images["nginx:latest"] = new(New, "nginx@sha256:bbb"); // mise à jour faite : conteneur recréé sur la nouvelle image
        d.Containers[0] = ("1", "web", "nginx:latest", New);

        var after = await Updates(d, registry).ReportAsync(); // un nouveau service : le résultat vient du fichier
        Assert.Equal(ImageUpdateState.UpToDate, Assert.Single(after.Containers).State);
        Assert.Equal(1, registry.Calls);
    }

    static Docker OneContainer(string image = "nginx:latest")
    {
        var d = new Docker();
        d.Containers.Add(("abc", "web", image, Old));
        d.Images[image] = new(Old, "nginx@sha256:aaa");
        return d;
    }

    sealed class Cleaner : ICommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> a, string? stdin = null, CancellationToken ct = default)
        {
            Calls.Add(a.ToArray());
            if (a[0] == "image") return Task.FromResult(new CommandResult(0, a[^1] == "sha256:named" ? "[\"nginx:1.27\"]\n" : "[]\n", ""));
            return Task.FromResult(a[^1] == "sha256:used" ? new CommandResult(1, "", "image is being used") : new CommandResult(0, "", ""));
        }
    }

    [Fact]
    public async Task Le_menage_ne_supprime_que_les_anciennes_images_sans_nom_et_sans_forcer()
    {
        var r = new Cleaner();
        var removed = await Svc(r).RemoveReplacedImagesAsync(["sha256:old", "sha256:named", "sha256:used", "sha256:old"]);

        Assert.Equal(1, removed);
        Assert.Equal(["sha256:old", "sha256:used"], r.Calls.Where(c => c[0] == "rmi").Select(c => c[^1]).ToArray()); // l'image encore nommée est gardée
        Assert.DoesNotContain(r.Calls, c => c.Contains("-f"));
        await Assert.ThrowsAsync<OperationException>(() => Svc(r).RemoveReplacedImagesAsync(["nginx:latest"]));
    }

    [Fact]
    public async Task Mettre_a_jour_telecharge_puis_recree_avec_les_memes_reglages()
    {
        var d = OneContainer();
        d.AfterPull = new(New, "nginx@sha256:bbb");

        Assert.True(await Svc(d).UpdateContainerImageAsync("abc"));

        Assert.Equal(["pull", "stop", "rename", "run", "rm"], d.Calls.Select(c => c[0]).Where(v => v is "pull" or "stop" or "rename" or "run" or "rm").ToArray());
        var run = d.Calls.Single(c => c[0] == "run");
        Assert.Contains("TZ=UTC", run);
        Assert.DoesNotContain("PATH=/usr/bin", run); // vient de l'image : la nouvelle apporte la sienne
        Assert.Contains("data:/data", run);
        Assert.Equal("nginx:latest", run[^1]);
    }

    [Fact]
    public async Task Rien_n_est_recree_si_l_image_n_a_pas_change()
    {
        var d = OneContainer();
        Assert.False(await Svc(d).UpdateContainerImageAsync("abc"));
        Assert.DoesNotContain(d.Calls, c => c[0] is "stop" or "rename" or "run" or "rm");
    }

    [Fact]
    public async Task Une_image_construite_sur_place_est_recreee_sans_telechargement()
    {
        var d = OneContainer("maison:latest");
        d.Images["maison:latest"] = new(New);
        Assert.True(await Svc(d).UpdateContainerImageAsync("abc"));
        Assert.DoesNotContain(d.Calls, c => c[0] == "pull");
    }

    [Fact]
    public async Task L_ancien_conteneur_est_remis_en_place_si_le_nouveau_echoue()
    {
        var d = OneContainer();
        d.AfterPull = new(New, "nginx@sha256:bbb");
        d.RunFails = true;
        var e = await Assert.ThrowsAsync<OperationException>(() => Svc(d).UpdateContainerImageAsync("abc"));
        Assert.Contains("previous one was restored", e.Message);
        Assert.Contains(d.Calls, c => c.SequenceEqual(["start", "web"]));
    }

    [Fact]
    public async Task Un_conteneur_de_stack_n_est_pas_mis_a_jour_seul()
    {
        var d = OneContainer();
        d.Labels = """{"com.docker.compose.project":"blog"}""";
        var e = await Assert.ThrowsAsync<OperationException>(() => Svc(d).UpdateContainerImageAsync("abc"));
        Assert.Contains("update the stack", e.Message);
        Assert.DoesNotContain(d.Calls, c => c[0] is "pull" or "stop");
    }

    [Fact]
    public async Task En_mode_simule_un_conteneur_nginx_se_met_a_jour_de_bout_en_bout()
    {
        var fake = new FakeCommandRunner();
        var docker = Svc(fake);
        var updates = new DockerUpdateService(docker, new FakeRegistryClient(), Cfg());
        await docker.RunContainerAsync(new RunContainerRequest("nginx", "web", "always", [], [], []));

        var before = (await updates.CheckAsync()).Containers.Single(c => c.ContainerName == "web");
        Assert.Equal(ImageUpdateState.UpdateAvailable, before.State);

        Assert.True(await docker.UpdateContainerImageAsync(before.ContainerId));

        var after = (await updates.ReportAsync()).Containers.Single(c => c.ContainerName == "web");
        Assert.Equal(ImageUpdateState.UpToDate, after.State);
        Assert.False(await docker.UpdateContainerImageAsync(after.ContainerId));
    }
}
