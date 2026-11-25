using System.ComponentModel;
using Microsoft.Extensions.Configuration;
using Nas.Agent.Services;
using Nas.Contracts;
using Nas.Web;

namespace Nas.Agent.Tests;

public class DockerParserTests
{
    [Theory]
    [InlineData("10.5MiB", 11010048)]
    [InlineData("1.2GB", 1200000000)]
    [InlineData("512kB", 512000)]
    [InlineData("0B", 0)]
    [InlineData("1.94GiB", 2083059138)]
    [InlineData("", 0)]
    [InlineData("n/a", 0)]
    public void Convertit_les_tailles(string text, long expected) => Assert.InRange(DockerParser.ParseSize(text), expected - 1000, expected + 1000);

    [Fact]
    public void Lit_les_conteneurs_et_leur_stack()
    {
        var text = "{\"ID\":\"abc123\",\"Names\":\"web-1\",\"Image\":\"nginx\",\"State\":\"running\",\"Status\":\"Up 2 hours\",\"Ports\":\"0.0.0.0:80->80/tcp\"," +
                   "\"Labels\":\"a=b,com.docker.compose.project=blog,c=d\"}\n" +
                   "{\"ID\":\"def456\",\"Names\":\"solo\",\"Image\":\"alpine\",\"State\":\"exited\",\"Status\":\"Exited (0)\",\"Ports\":\"\",\"Labels\":\"\"}\n" +
                   "pas du JSON\n";
        var list = DockerParser.ParseContainers(text);
        Assert.Equal(2, list.Count);
        Assert.Equal(("web-1", "blog", "running"), (list[0].Name, list[0].Project, list[0].State));
        Assert.Null(list[1].Project);
    }

    [Fact]
    public void Lit_les_statistiques_les_images_les_volumes_et_les_reseaux()
    {
        var s = DockerParser.ParseStats("{\"ID\":\"abc\",\"CPUPerc\":\"1.50%\",\"MemUsage\":\"10MiB / 2GiB\"}\n").Single();
        Assert.Equal(1.5, s.CpuPercent);
        Assert.Equal(10L * 1024 * 1024, s.MemUsedBytes);
        Assert.Equal(2L * 1024 * 1024 * 1024, s.MemLimitBytes);

        var i = DockerParser.ParseImages("{\"ID\":\"a1\",\"Repository\":\"nginx\",\"Tag\":\"latest\",\"Size\":\"187MB\",\"CreatedSince\":\"3 weeks ago\"}\n").Single();
        Assert.Equal(("nginx", "latest", 187_000_000L), (i.Repository, i.Tag, i.SizeBytes));

        Assert.Equal("data", DockerParser.ParseVolumes("{\"Name\":\"data\",\"Driver\":\"local\",\"Mountpoint\":\"/var/lib/docker/volumes/data/_data\"}\n").Single().Name);
        Assert.Equal("bridge", DockerParser.ParseNetworks("{\"ID\":\"n1\",\"Name\":\"bridge\",\"Driver\":\"bridge\",\"Scope\":\"local\"}\n").Single().Name);
    }
}

public class DockerInspectTests
{
    internal const string ImageJson = """{"Config":{"Env":["PATH=/usr/bin","LANG=C"],"Cmd":["nginx"],"Entrypoint":null,"User":"","WorkingDir":""}}""";

    internal static string ContainerJson(string extraHostConfig = "", string cmd = "[\"nginx\"]", string labels = "{}", bool running = true, string networks = "{\"bridge\":{}}") => $$"""
        {"Name":"/web","Image":"sha256:abc","State":{"Running":{{(running ? "true" : "false")}}},
         "Config":{"Image":"nginx:1.27","Env":["PATH=/usr/bin","LANG=C","TZ=UTC"],"Cmd":{{cmd}},"Entrypoint":null,"User":"","WorkingDir":"","Labels":{{labels}} },
         "HostConfig":{"RestartPolicy":{"Name":"unless-stopped"},{{extraHostConfig}}
           "PortBindings":{"80/tcp":[{"HostIp":"","HostPort":"8080"}],"53/udp":[{"HostIp":"192.168.1.5","HostPort":"5353"}],"443/tcp":[{"HostIp":"::","HostPort":""}] } },
         "Mounts":[{"Type":"bind","Source":"/srv/zfs/tank/www","Destination":"/usr/share/nginx/html","RW":false},
                   {"Type":"volume","Name":"webdata","Destination":"/data","RW":true}],
         "NetworkSettings":{"Networks":{{networks}} } }
        """;

    static (System.Text.Json.JsonElement C, System.Text.Json.JsonElement I) Parse(string container) =>
        (System.Text.Json.JsonDocument.Parse(container).RootElement, System.Text.Json.JsonDocument.Parse(ImageJson).RootElement);

    [Fact]
    public void Relit_les_reglages_sans_les_variables_de_l_image()
    {
        var (c, i) = Parse(ContainerJson(networks: "{\"media\":{}}"));
        var s = DockerInspect.Settings(c, i);
        Assert.Equal(("nginx:1.27", "web", "unless-stopped", "media"), (s.Image, s.Name, s.Restart, s.Network));
        Assert.Equal(["TZ=UTC"], s.Env);
        Assert.Equal(["8080:80", "192.168.1.5:5353:53/udp"], s.Ports); // le lien sans port d'hôte est ignoré
        Assert.Equal(["/srv/zfs/tank/www:/usr/share/nginx/html:ro", "webdata:/data"], s.Volumes);
        Assert.Empty(DockerInspect.Unsupported(c, i));
        Assert.True(DockerInspect.Running(c));
        Assert.Null(DockerInspect.Stack(c));
    }

    [Fact]
    public void Le_reseau_par_defaut_n_est_pas_repris()
    {
        var (c, i) = Parse(ContainerJson());
        Assert.Equal("", DockerInspect.Settings(c, i).Network);
    }

    [Fact]
    public void Repere_les_reglages_que_le_formulaire_ne_sait_pas_garder()
    {
        var (c, i) = Parse(ContainerJson(extraHostConfig: "\"Privileged\":true,\"Memory\":536870912,\"CapAdd\":[\"NET_ADMIN\"],", cmd: "[\"sleep\",\"300\"]"));
        var u = DockerInspect.Unsupported(c, i);
        Assert.Contains("memory or CPU limits", u);
        Assert.Contains("custom command", u);
        Assert.DoesNotContain("privileged mode", u); // le formulaire sait maintenant garder le mode privilégié et les capacités
        Assert.DoesNotContain("added or removed capabilities", u);

        var (c2, i2) = Parse(ContainerJson(networks: "{\"a\":{},\"b\":{}}"));
        Assert.Contains("several networks", DockerInspect.Unsupported(c2, i2));
    }

    [Fact]
    public void Reconnait_la_stack_compose()
    {
        var (c, _) = Parse(ContainerJson(labels: "{\"com.docker.compose.project\":\"blog\"}"));
        Assert.Equal("blog", DockerInspect.Stack(c));
    }
}

public class DockerRulesTests
{
    [Theory]
    [InlineData("nginx", true)]
    [InlineData("nginx:1.27-alpine", true)]
    [InlineData("ghcr.io/user/app:v1", true)]
    [InlineData("registry.local:5000/team/app@sha256:abcdef", true)]
    [InlineData("-v", false)]
    [InlineData("--privileged", false)]
    [InlineData("a b", false)]
    [InlineData("Nginx", false)] // les noms d'image sont en minuscules
    [InlineData("", false)]
    public void Valide_les_images(string image, bool ok) => Assert.Equal(ok, Rules.ValidateImageRef(image) is null);

    [Theory]
    [InlineData("web-1", true)]
    [InlineData("a1b2c3d4e5f6", true)]
    [InlineData("my_volume.v2", true)]
    [InlineData("-rf", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void Valide_les_noms(string name, bool ok) => Assert.Equal(ok, Rules.ValidateDockerName(name) is null);

    [Theory]
    [InlineData("blog", true)]
    [InlineData("my-stack_2", true)]
    [InlineData("Blog", false)]
    [InlineData("-x", false)]
    [InlineData("a b", false)]
    public void Valide_les_noms_de_stack(string name, bool ok) => Assert.Equal(ok, Rules.ValidateStackName(name) is null);

    [Theory]
    [InlineData("8080:80", true)]
    [InlineData("8080:80/udp", true)]
    [InlineData("127.0.0.1:8080:80", true)]
    [InlineData("8000-8010:8000-8010", true)]
    [InlineData("80", false)]
    [InlineData("8080:80;rm", false)]
    [InlineData("--net=host", false)]
    public void Valide_les_ports(string p, bool ok) => Assert.Equal(ok, Rules.ValidatePort(p) is null);

    [Theory]
    [InlineData("TZ=America/Toronto", true)]
    [InlineData("EMPTY=", true)]
    [InlineData("A_B.c=x y z", true)]
    [InlineData("1BAD=x", false)]
    [InlineData("NOEQUAL", false)]
    [InlineData("A=x\ny", false)]
    public void Valide_les_variables(string e, bool ok) => Assert.Equal(ok, Rules.ValidateEnv(e) is null);

    [Theory]
    [InlineData("/srv/zfs/tank/media:/media", true)]
    [InlineData("/srv/zfs/tank/media:/media:ro", true)]
    [InlineData("data:/var/lib/data", true)]
    [InlineData("/srv/../etc:/x", false)]
    [InlineData("/srv/a:relative", false)]
    [InlineData("/srv/a", false)]
    [InlineData("/srv/a:/b:zz", false)]
    [InlineData("/srv/a,source=/etc:/b", false)] // la virgule ouvrirait des options de montage
    [InlineData("--privileged:/b", false)]
    public void Valide_les_volumes(string v, bool ok) => Assert.Equal(ok, Rules.ValidateVolumeMount(v) is null);

    [Fact]
    public void Valide_la_demande_de_lancement()
    {
        RunContainerRequest R(string image = "nginx", string restart = "no", string[]? ports = null) => new(image, null, restart, ports ?? [], [], []);
        Assert.Null(Rules.ValidateRunContainer(R()));
        Assert.NotNull(Rules.ValidateRunContainer(R(image: "-x")));
        Assert.NotNull(Rules.ValidateRunContainer(R(restart: "sometimes")));
        Assert.NotNull(Rules.ValidateRunContainer(R(ports: ["nope"])));
    }

    [Fact]
    public void Valide_le_contenu_d_une_stack()
    {
        Assert.Null(Rules.ValidateStackContent("services: {}"));
        Assert.NotNull(Rules.ValidateStackContent("  "));
        Assert.NotNull(Rules.ValidateStackContent(new string('a', Rules.MaxStackBytes + 1)));
        Assert.NotNull(Rules.ValidateStackContent("a\0b"));
    }
}

public sealed class DockerServiceTests : IDisposable
{
    sealed class Runner(Func<IReadOnlyList<string>, CommandResult> reply) : ICommandRunner
    {
        public List<string[]> Calls { get; } = [];
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default)
        {
            Calls.Add(args.ToArray());
            return Task.FromResult(reply(args));
        }
    }

    sealed class Missing : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default) =>
            throw new Win32Exception(2);
    }

    static readonly CommandResult Good = new(0, "", "");
    readonly string _dir = Path.Combine(Path.GetTempPath(), "ursanas-docker-" + Guid.NewGuid().ToString("N"));

    DockerService Svc(ICommandRunner r) => new(r, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Docker:StacksDir"] = _dir }).Build());

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public async Task L_etat_distingue_absent_arrete_et_pret()
    {
        Assert.Equal(DockerState.NotInstalled, (await Svc(new Missing()).StatusAsync()).State);
        Assert.Equal(DockerState.Stopped, (await Svc(new Runner(_ => new(1, "", "Cannot connect to the Docker daemon"))).StatusAsync()).State);

        var ready = await Svc(new Runner(a => a[0] == "version" ? new(0, "27.3.1\n", "") : a[0] == "compose" ? new(0, "2.29.7\n", "") : Good)).StatusAsync();
        Assert.Equal((DockerState.Ready, "27.3.1", "2.29.7"), (ready.State, ready.Version, ready.ComposeVersion));

        var noCompose = await Svc(new Runner(a => a[0] == "version" ? new(0, "27.3.1\n", "") : new(1, "", "unknown command"))).StatusAsync();
        Assert.Equal(DockerState.Ready, noCompose.State);
        Assert.Null(noCompose.ComposeVersion);
    }

    [Fact]
    public async Task Une_action_invalide_ne_lance_aucune_commande()
    {
        var r = new Runner(_ => Good);
        var s = Svc(r);
        await Assert.ThrowsAsync<OperationException>(() => s.ContainerActionAsync("--all", "stop"));
        await Assert.ThrowsAsync<OperationException>(() => s.ContainerActionAsync("web", "kill"));
        await Assert.ThrowsAsync<OperationException>(() => s.PullImageAsync("--platform=x"));
        await Assert.ThrowsAsync<OperationException>(() => s.RemoveVolumeAsync("-f"));
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task Supprimer_un_conteneur_n_utilise_pas_l_option_force()
    {
        var r = new Runner(_ => Good);
        await Svc(r).ContainerActionAsync("web", "remove");
        Assert.Equal(["rm", "web"], r.Calls.Single());
    }

    [Fact]
    public async Task Lance_un_conteneur_avec_ses_options_et_l_image_en_dernier()
    {
        var r = new Runner(_ => Good);
        await Svc(r).RunContainerAsync(new RunContainerRequest("nginx:latest", "web", "unless-stopped", ["8080:80"], ["TZ=UTC"], ["/srv/a:/data:ro"]));
        Assert.Equal(["run", "-d", "--restart", "unless-stopped", "--name", "web", "-p", "8080:80", "-e", "TZ=UTC", "-v", "/srv/a:/data:ro", "nginx:latest"], r.Calls.Single());
    }

    [Fact]
    public async Task Lance_un_conteneur_avec_ses_capacites_et_le_mode_prive()
    {
        var r = new Runner(_ => Good);
        await Svc(r).RunContainerAsync(new RunContainerRequest("nginx", null, "no", [], [], [], null, true, ["NET_ADMIN", "NET_ADMIN"], ["MKNOD"]));
        var run = r.Calls.Single();
        Assert.Contains("--privileged", run);
        Assert.Equal(1, run.Count(a => a == "--cap-add=NET_ADMIN")); // pas de doublon
        Assert.Contains("--cap-drop=MKNOD", run);
        Assert.Equal("nginx", run[^1]); // l'image reste en dernier

        r.Calls.Clear();
        await Svc(r).RunContainerAsync(new RunContainerRequest("nginx", null, "no", [], [], []));
        Assert.DoesNotContain(r.Calls.Single(), a => a.StartsWith("--cap") || a == "--privileged");

        await Assert.ThrowsAsync<OperationException>(() => Svc(r).RunContainerAsync(new RunContainerRequest("nginx", null, "no", [], [], [], null, false, ["--rm"])));
        await Assert.ThrowsAsync<OperationException>(() => Svc(r).RunContainerAsync(new RunContainerRequest("nginx", null, "no", [], [], [], null, false, null, ["cap_chown"])));
    }

    [Fact]
    public void Les_droits_effectifs_tiennent_compte_du_mode_prive_des_ajouts_et_des_retraits()
    {
        Assert.True(Rules.EffectiveCapabilities(false, null, null).SetEquals(Rules.DefaultCapabilities));
        Assert.Equal(Rules.AllCapabilities.Count, Rules.EffectiveCapabilities(true, null, null).Count);
        Assert.Equal(Rules.AllCapabilities.Count, Rules.EffectiveCapabilities(false, ["ALL"], null).Count);
        Assert.Empty(Rules.EffectiveCapabilities(false, null, ["ALL"]));
        var only = Rules.EffectiveCapabilities(false, ["NET_BIND_SERVICE"], ["ALL"]);
        Assert.Equal(["NET_BIND_SERVICE"], only);
        var some = Rules.EffectiveCapabilities(false, ["NET_ADMIN"], ["CHOWN"]);
        Assert.Contains("NET_ADMIN", some);
        Assert.DoesNotContain("CHOWN", some);
        Assert.All(Rules.DefaultCapabilities, c => Assert.Contains(c, Rules.AllCapabilities));
    }

    [Fact]
    public void Relit_le_mode_prive_et_les_capacites_sans_le_prefixe_cap()
    {
        var (c, i) = Parse2(DockerInspectTests.ContainerJson(extraHostConfig: "\"Privileged\":true,\"CapAdd\":[\"CAP_NET_ADMIN\",\"sys_time\"],\"CapDrop\":[\"ALL\"],"));
        var s = DockerInspect.Settings(c, i);
        Assert.True(s.Privileged);
        Assert.Equal(["NET_ADMIN", "SYS_TIME"], s.CapAdd);
        Assert.Equal(["ALL"], s.CapDrop);
    }

    static (System.Text.Json.JsonElement C, System.Text.Json.JsonElement I) Parse2(string container) =>
        (System.Text.Json.JsonDocument.Parse(container).RootElement, System.Text.Json.JsonDocument.Parse(DockerInspectTests.ImageJson).RootElement);

    [Fact]
    public async Task Changer_les_capacites_recree_le_conteneur_mais_les_droits_equivalents_ne_changent_rien()
    {
        var r = Inspecting(DockerInspectTests.ContainerJson(extraHostConfig: "\"CapAdd\":[\"CHOWN\"],")); // CHOWN est déjà accordé d'office
        await Svc(r).UpdateContainerAsync("web", Current());
        Assert.DoesNotContain(r.Calls, c => c[0] is "update" or "stop" or "rename" or "run" or "rm"); // mêmes droits réels : rien à faire

        await Svc(r).UpdateContainerAsync("web", Current() with { CapAdd = ["NET_ADMIN"] });
        var run = r.Calls.Single(c => c[0] == "run");
        Assert.Contains("--cap-add=NET_ADMIN", run);

        r.Calls.Clear();
        await Svc(r).UpdateContainerAsync("web", Current() with { Privileged = true });
        Assert.Contains("--privileged", r.Calls.Single(c => c[0] == "run"));
    }

    [Fact]
    public async Task Le_reseau_choisi_est_cree_seulement_s_il_n_existe_pas()
    {
        var existing = "{\"ID\":\"n1\",\"Name\":\"media\",\"Driver\":\"bridge\",\"Scope\":\"local\"}\n";
        var r = new Runner(a => a[0] == "network" && a[1] == "ls" ? new(0, existing, "") : Good);
        await Svc(r).RunContainerAsync(new RunContainerRequest("nginx", null, "no", [], [], [], "media"));
        Assert.DoesNotContain(r.Calls, c => c.Take(2).SequenceEqual(["network", "create"]));
        Assert.Contains("--network", r.Calls.Last());

        r.Calls.Clear();
        await Svc(r).RunContainerAsync(new RunContainerRequest("nginx", null, "no", [], [], [], "neuf"));
        Assert.Contains(r.Calls, c => c.SequenceEqual(["network", "create", "neuf"]));
        Assert.True(r.Calls.FindIndex(c => c[0] == "network" && c[1] == "create") < r.Calls.FindIndex(c => c[0] == "run")); // créé avant le lancement

        await Assert.ThrowsAsync<OperationException>(() => Svc(r).RunContainerAsync(new RunContainerRequest("nginx", null, "no", [], [], [], "--rm")));
    }

    static Runner Inspecting(string containerJson, Func<IReadOnlyList<string>, CommandResult?>? custom = null) => new(a =>
    {
        if (custom?.Invoke(a) is { } r) return r;
        if (a[0] == "inspect") return new(0, containerJson, "");
        if (a[0] == "image" && a[1] == "inspect") return new(0, DockerInspectTests.ImageJson, "");
        return Good;
    });

    static RunContainerRequest Current(string image = "nginx:1.27", string restart = "unless-stopped", string[]? ports = null, string network = "") =>
        new(image, "web", restart, ports ?? ["8080:80", "192.168.1.5:5353:53/udp"], ["TZ=UTC"], ["/srv/zfs/tank/www:/usr/share/nginx/html:ro", "webdata:/data"], network);

    [Fact]
    public async Task Changer_seulement_le_redemarrage_ne_recree_pas_le_conteneur()
    {
        var r = Inspecting(DockerInspectTests.ContainerJson());
        await Svc(r).UpdateContainerAsync("web", Current(restart: "always"));
        Assert.Contains(r.Calls, c => c.SequenceEqual(["update", "--restart", "always", "web"]));
        Assert.DoesNotContain(r.Calls, c => c[0] is "stop" or "rename" or "run" or "rm");

        r.Calls.Clear();
        await Svc(r).UpdateContainerAsync("web", Current()); // rien ne change : aucune commande de modification
        Assert.DoesNotContain(r.Calls, c => c[0] is "update" or "stop" or "rename" or "run" or "rm");
    }

    [Fact]
    public async Task Un_changement_recree_le_conteneur_dans_le_bon_ordre()
    {
        var r = Inspecting(DockerInspectTests.ContainerJson());
        await Svc(r).UpdateContainerAsync("web", Current(image: "nginx:1.28"));
        var verbs = r.Calls.Select(c => c[0]).Where(v => v is "stop" or "rename" or "run" or "rm").ToList();
        Assert.Equal(["stop", "rename", "run", "rm"], verbs);
        var run = r.Calls.Single(c => c[0] == "run");
        Assert.Equal("nginx:1.28", run[^1]);
        Assert.Contains("web", run);                       // le nouveau conteneur reprend le nom
        Assert.Equal("rm", r.Calls.Last()[0]);             // l'ancien (renommé) n'est supprimé qu'à la fin
    }

    [Fact]
    public async Task Un_conteneur_arrete_est_recree_arrete()
    {
        var r = Inspecting(DockerInspectTests.ContainerJson(running: false));
        await Svc(r).UpdateContainerAsync("web", Current(image: "nginx:1.28"));
        Assert.DoesNotContain(r.Calls, c => c[0] is "stop" or "run");
        Assert.Contains(r.Calls, c => c[0] == "create");
    }

    [Fact]
    public async Task Si_le_nouveau_conteneur_echoue_l_ancien_est_remis_en_place()
    {
        var r = Inspecting(DockerInspectTests.ContainerJson(), a => a[0] == "run" ? new(1, "", "port is already allocated") : null);
        var ex = await Assert.ThrowsAsync<OperationException>(() => Svc(r).UpdateContainerAsync("web", Current(image: "nginx:1.28")));
        Assert.Contains("previous one was restored", ex.Message);
        Assert.Contains("port is already allocated", ex.Message);
        var verbs = r.Calls.Select(c => c[0]).Where(v => v is "stop" or "rename" or "run" or "rm" or "start").ToList();
        Assert.Equal(["stop", "rename", "run", "rm", "rename", "start"], verbs);
        var back = r.Calls.Last(c => c[0] == "rename");
        Assert.StartsWith("web-old-", back[1]); // l'ancien, qui avait été mis de côté...
        Assert.Equal("web", back[2]);           // ... reprend son nom
        Assert.Equal([["rm", "-f", "web"]], r.Calls.Where(c => c[0] == "rm").Select(c => c.ToArray()).ToArray()); // seul le nouveau est supprimé
    }

    [Fact]
    public async Task Refuse_de_recreer_un_conteneur_de_stack_ou_aux_reglages_inconnus()
    {
        var stacked = Inspecting(DockerInspectTests.ContainerJson(labels: "{\"com.docker.compose.project\":\"blog\"}"));
        var e1 = await Assert.ThrowsAsync<OperationException>(() => Svc(stacked).UpdateContainerAsync("web", Current(image: "nginx:1.28")));
        Assert.Contains("stack", e1.Message);
        Assert.DoesNotContain(stacked.Calls, c => c[0] is "stop" or "rename");

        var special = Inspecting(DockerInspectTests.ContainerJson(extraHostConfig: "\"Memory\":536870912,"));
        var e2 = await Assert.ThrowsAsync<OperationException>(() => Svc(special).UpdateContainerAsync("web", Current(image: "nginx:1.28")));
        Assert.Contains("cannot be kept", e2.Message);
        Assert.DoesNotContain(special.Calls, c => c[0] is "stop" or "rename");

        // ... mais la politique de redémarrage reste modifiable à chaud
        await Svc(special).UpdateContainerAsync("web", Current(restart: "always"));
        Assert.Contains(special.Calls, c => c[0] == "update");
    }

    [Fact]
    public async Task Une_modification_invalide_ne_lance_aucune_commande()
    {
        var r = Inspecting(DockerInspectTests.ContainerJson());
        await Assert.ThrowsAsync<OperationException>(() => Svc(r).UpdateContainerAsync("--all", Current()));
        await Assert.ThrowsAsync<OperationException>(() => Svc(r).UpdateContainerAsync("web", Current(image: "-x")));
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task Un_echec_de_docker_devient_un_message_lisible()
    {
        var ex = await Assert.ThrowsAsync<OperationException>(() => Svc(new Runner(_ => new(1, "", "No such container: x"))).ContainerActionAsync("x", "start"));
        Assert.Contains("No such container", ex.Message);
        var gone = await Assert.ThrowsAsync<OperationException>(() => Svc(new Missing()).ContainersAsync());
        Assert.Contains("not installed", gone.Message);
    }

    [Fact]
    public async Task Les_journaux_gardent_les_deux_flux_et_limitent_le_nombre_de_lignes()
    {
        var r = new Runner(_ => new(0, "out\n", "err\n"));
        Assert.Equal("out\nerr\n", await Svc(r).LogsAsync("web", 999999));
        Assert.Contains("2000", r.Calls.Single());
    }

    static Runner ComposeRunner(bool configOk = true) => new(a => a[0] switch
    {
        "version" => new(0, "27\n", ""),
        "compose" when a.Contains("version") => new(0, "2.29\n", ""),
        "compose" when a.Contains("config") => configOk ? Good : new(1, "", "services.web.image must be a string"),
        _ => Good,
    });

    [Fact]
    public async Task Reconstruire_une_stack_recree_tout_sans_interroger_les_registres()
    {
        var r = ComposeRunner();
        var s = Svc(r);
        await s.SaveStackAsync("blog", "services:\n  web:\n    image: nginx\n");
        r.Calls.Clear();

        await s.StackActionAsync("blog", "rebuild");

        Assert.Contains(r.Calls, c => c.Contains("up") && c.Contains("--force-recreate"));
        Assert.DoesNotContain(r.Calls, c => c.Contains("pull"));
    }

    [Fact]
    public async Task Une_stack_invalide_n_ecrase_pas_l_ancienne()
    {
        var s = Svc(ComposeRunner());
        await s.SaveStackAsync("blog", "services:\n  web:\n    image: nginx\n");
        Assert.Contains("nginx", s.ReadStack("blog").Content);

        var bad = Svc(ComposeRunner(configOk: false));
        var ex = await Assert.ThrowsAsync<OperationException>(() => bad.SaveStackAsync("blog", "services: nope"));
        Assert.Contains("must be a string", ex.Message);
        Assert.Contains("nginx", s.ReadStack("blog").Content);          // l'ancien fichier est intact
        Assert.False(File.Exists(Path.Combine(_dir, "blog", "compose.yml.new"))); // pas de reste
    }

    [Fact]
    public async Task Une_premiere_stack_refusee_ne_laisse_pas_de_dossier_vide()
    {
        var ex = await Assert.ThrowsAsync<OperationException>(() => Svc(ComposeRunner(configOk: false)).SaveStackAsync("nouvelle", "services: nope"));
        Assert.NotNull(ex);
        Assert.False(Directory.Exists(Path.Combine(_dir, "nouvelle")));
    }

    [Fact]
    public async Task Les_actions_de_stack_appellent_compose_avec_le_bon_fichier()
    {
        var r = ComposeRunner();
        var s = Svc(r);
        await s.SaveStackAsync("blog", "services: {}\n");
        r.Calls.Clear();

        await s.StackActionAsync("blog", "update");
        var file = Path.Combine(_dir, "blog", "compose.yml");
        Assert.Contains(r.Calls, c => c.SequenceEqual(["compose", "-p", "blog", "-f", file, "pull"]));
        Assert.Contains(r.Calls, c => c.SequenceEqual(["compose", "-p", "blog", "-f", file, "up", "-d", "--remove-orphans"]));

        await s.StackActionAsync("blog", "remove");
        Assert.False(Directory.Exists(Path.Combine(_dir, "blog")));
        await Assert.ThrowsAsync<OperationException>(() => s.StackActionAsync("blog", "up")); // n'existe plus
        await Assert.ThrowsAsync<OperationException>(() => s.StackActionAsync("../x", "up"));
    }

    [Fact]
    public async Task Les_stacks_comptent_leurs_conteneurs()
    {
        var r = new Runner(a => a[0] switch
        {
            "version" => new(0, "27\n", ""),
            "compose" => new(0, "2.29\n", ""),
            "ps" => new(0, "{\"ID\":\"a\",\"Names\":\"blog-web-1\",\"State\":\"running\",\"Labels\":\"com.docker.compose.project=blog\"}\n" +
                           "{\"ID\":\"b\",\"Names\":\"blog-db-1\",\"State\":\"exited\",\"Labels\":\"com.docker.compose.project=blog\"}\n", ""),
            _ => Good,
        });
        var s = Svc(r);
        await s.SaveStackAsync("blog", "services: {}\n");
        var stack = (await s.StacksAsync()).Single();
        Assert.Equal(("blog", 1, 2), (stack.Name, stack.Running, stack.Total));
    }

    [Fact]
    public async Task Le_fichier_env_est_enregistre_a_cote_du_compose_et_relu()
    {
        var r = ComposeRunner();
        var s = Svc(r);
        await s.SaveStackAsync("blog", "services: {}\n", "DB_PASSWORD=secret\n");
        Assert.Equal("DB_PASSWORD=secret\n", File.ReadAllText(Path.Combine(_dir, "blog", ".env")));
        Assert.Equal("DB_PASSWORD=secret\n", s.ReadStack("blog").Env);
        Assert.Contains(r.Calls, c => c.Contains("--env-file") && c.Contains("config")); // vérifié avec le .env à enregistrer
        Assert.False(File.Exists(Path.Combine(_dir, "blog", ".env.new")));
    }

    [Fact]
    public async Task Sans_env_le_fichier_existant_reste_et_un_env_vide_le_supprime()
    {
        var s = Svc(ComposeRunner());
        await s.SaveStackAsync("blog", "services: {}\n", "A=1\n");
        await s.SaveStackAsync("blog", "services: {}\n"); // null : on n'y touche pas
        Assert.Equal("A=1\n", s.ReadStack("blog").Env);
        await s.SaveStackAsync("blog", "services: {}\n", "");
        Assert.Equal("", s.ReadStack("blog").Env);
        Assert.False(File.Exists(Path.Combine(_dir, "blog", ".env")));
    }

    [Fact]
    public async Task Un_env_refuse_ne_change_rien()
    {
        var s = Svc(ComposeRunner());
        await s.SaveStackAsync("blog", "services: {}\n", "A=1\n");
        await Assert.ThrowsAsync<OperationException>(() => s.SaveStackAsync("blog", "services: {}\n", "A=\0"));
        Assert.Equal("A=1\n", s.ReadStack("blog").Env);
    }

    [Fact]
    public async Task Sans_compose_on_n_enregistre_pas_de_stack()
    {
        var r = new Runner(a => a[0] == "version" ? new(0, "27\n", "") : new(1, "", "unknown"));
        var ex = await Assert.ThrowsAsync<OperationException>(() => Svc(r).SaveStackAsync("blog", "services: {}\n"));
        Assert.Contains("Compose", ex.Message);
    }
}

public class DockerAvailabilityTests
{
    [Fact]
    public void Le_menu_n_est_pret_que_si_docker_repond_et_previent_des_changements()
    {
        var a = new DockerAvailability();
        var changes = 0;
        a.Changed += () => changes++;
        Assert.False(a.Ready);

        a.Set(new DockerStatus(DockerState.Stopped, null, null));
        Assert.False(a.Ready);
        a.Set(new DockerStatus(DockerState.Ready, "27", "2.29"));
        Assert.True(a.Ready);
        a.Set(new DockerStatus(DockerState.Ready, "27", "2.29")); // identique : pas de nouvel événement
        a.Set(new DockerStatus(DockerState.NotInstalled, null, null));
        Assert.False(a.Ready);
        Assert.Equal(3, changes);
    }
}
