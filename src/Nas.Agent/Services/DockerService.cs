using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>Lecture de la sortie de la commande docker (formats « {{json .}} » : une ligne JSON par objet).</summary>
public static class DockerParser
{
    static IEnumerable<JsonElement> Lines(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { }
            if (doc is not null) yield return doc.RootElement.Clone();
        }
    }

    static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>« 10.5MiB », « 1.2GB », « 512kB » → octets (Docker mélange les unités décimales et binaires).</summary>
    public static long ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var t = text.Trim();
        var i = 0;
        while (i < t.Length && (char.IsDigit(t[i]) || t[i] == '.')) i++;
        if (i == 0 || !double.TryParse(t[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return 0;
        double mult = t[i..].Trim().ToLowerInvariant() switch
        {
            "b" or "" => 1,
            "kb" => 1e3, "mb" => 1e6, "gb" => 1e9, "tb" => 1e12,
            "kib" => 1024, "mib" => 1024.0 * 1024, "gib" => 1024.0 * 1024 * 1024, "tib" => 1024.0 * 1024 * 1024 * 1024,
            _ => 1,
        };
        return (long)(n * mult);
    }

    public static IReadOnlyList<ContainerInfo> ParseContainers(string text) =>
        Lines(text).Select(e => new ContainerInfo(S(e, "ID"), S(e, "Names").Split(',')[0], S(e, "Image"), S(e, "State"), S(e, "Status"),
            S(e, "Ports"), ProjectOf(S(e, "Labels")))).ToList();

    static string? ProjectOf(string labels)
    {
        const string key = "com.docker.compose.project=";
        foreach (var l in labels.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (l.StartsWith(key, StringComparison.Ordinal)) return l[key.Length..];
        return null;
    }

    public static IReadOnlyList<ContainerStats> ParseStats(string text) =>
        Lines(text).Select(e =>
        {
            var cpu = S(e, "CPUPerc").TrimEnd('%');
            var mem = S(e, "MemUsage").Split('/');
            return new ContainerStats(S(e, "ID"),
                double.TryParse(cpu, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : 0,
                ParseSize(mem[0]), mem.Length > 1 ? ParseSize(mem[1]) : 0);
        }).ToList();

    public static IReadOnlyList<ImageInfo> ParseImages(string text) =>
        Lines(text).Select(e => new ImageInfo(S(e, "ID"), S(e, "Repository"), S(e, "Tag"), ParseSize(S(e, "Size")), S(e, "CreatedSince"))).ToList();

    public static IReadOnlyList<DockerVolume> ParseVolumes(string text) =>
        Lines(text).Select(e => new DockerVolume(S(e, "Name"), S(e, "Driver"), S(e, "Mountpoint"))).ToList();

    public static IReadOnlyList<DockerNetwork> ParseNetworks(string text) =>
        Lines(text).Select(e => new DockerNetwork(S(e, "ID"), S(e, "Name"), S(e, "Driver"), S(e, "Scope"))).ToList();
}

/// <summary>
/// Gestion de Docker par la commande <c>docker</c> (sans shell). Les stacks Compose vivent dans <c>Docker:StacksDir/&lt;nom&gt;/compose.yml</c>.
/// Aucune option fournie par l'utilisateur n'atteint la commande : noms, images, ports, variables et volumes sont validés par
/// <see cref="Rules"/>, et aucun ne peut commencer par « - ».
/// </summary>
public sealed class DockerService(ICommandRunner run, IConfiguration cfg)
{
    // Une opération qui modifie Docker à la fois ; les lectures n'attendent pas (un téléchargement peut durer des minutes).
    readonly SemaphoreSlim _gate = new(1, 1);

    string Bin => cfg["Docker:Binary"] ?? "docker";
    string StacksDir => (cfg["Docker:StacksDir"] ?? "/var/lib/ursanas/stacks").TrimEnd('/', '\\');
    string StackFile(string name) => Path.Combine(StacksDir, name, "compose.yml");

    // ---- État

    public async Task<DockerStatus> StatusAsync(CancellationToken ct = default)
    {
        CommandResult v;
        try { v = await run.RunAsync(Bin, ["version", "--format", "{{.Server.Version}}"], ct: ct); }
        catch (Win32Exception) { return new(DockerState.NotInstalled, null, null); }
        if (v.ExitCode != 0) return new(DockerState.Stopped, null, null);
        string? compose = null;
        try
        {
            var c = await run.RunAsync(Bin, ["compose", "version", "--short"], ct: ct);
            if (c.ExitCode == 0) compose = c.StdOut.Trim();
        }
        catch (Win32Exception) { }
        return new(DockerState.Ready, v.StdOut.Trim(), compose);
    }

    async Task<string> Ok(IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        CommandResult r;
        try { r = await run.RunAsync(Bin, args, ct: ct); }
        catch (Win32Exception) { throw new OperationException(Msg.F("Docker is not installed.")); }
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

    // ---- Conteneurs

    public async Task<IReadOnlyList<ContainerInfo>> ContainersAsync(CancellationToken ct = default) =>
        DockerParser.ParseContainers(await Ok(["ps", "-a", "--format", "{{json .}}"], "Listing containers", ct));

    public async Task<IReadOnlyList<ContainerStats>> StatsAsync(CancellationToken ct = default) =>
        DockerParser.ParseStats(await Ok(["stats", "--no-stream", "--format", "{{json .}}"], "Reading the statistics", ct));

    public async Task<string> LogsAsync(string id, int tail, CancellationToken ct = default)
    {
        if (Rules.ValidateDockerName(id) is { } e) throw new OperationException(e);
        tail = Math.Clamp(tail, 1, 2000);
        CommandResult r;
        try { r = await run.RunAsync(Bin, ["logs", "--tail", tail.ToString(CultureInfo.InvariantCulture), "--timestamps", id], ct: ct); }
        catch (Win32Exception) { throw new OperationException(Msg.F("Docker is not installed.")); }
        if (r.ExitCode != 0) throw new OperationException(Msg.F("{0} failed: {1}", "Reading the logs", r.StdErr.Trim()));
        var text = r.StdOut + r.StdErr; // docker écrit les deux flux du conteneur séparément
        return text.Length > 200_000 ? text[^200_000..] : text;
    }

    public Task ContainerActionAsync(string id, string action, CancellationToken ct = default)
    {
        if (Rules.ValidateDockerName(id) is { } e) throw new OperationException(e);
        var (verb, what) = action switch
        {
            "start" => ("start", "Starting the container"),
            "stop" => ("stop", "Stopping the container"),
            "restart" => ("restart", "Restarting the container"),
            "remove" => ("rm", "Deleting the container"), // sans -f : un conteneur en marche doit d'abord être arrêté
            _ => throw new OperationException(Msg.F("Unknown action.")),
        };
        return Mutate(async () => await Ok([verb, id], what, ct));
    }

    /// <summary>« docker run -d » (ou « docker create » pour un conteneur qui doit rester arrêté) ; l'image est toujours le dernier argument.</summary>
    static List<string> RunArgs(RunContainerRequest r, string name, bool start)
    {
        var args = start ? new List<string> { "run", "-d" } : ["create"];
        args.AddRange(["--restart", r.Restart]);
        if (!string.IsNullOrEmpty(name)) { args.Add("--name"); args.Add(name); }
        if (!string.IsNullOrEmpty(r.Network)) { args.Add("--network"); args.Add(r.Network); }
        foreach (var p in r.Ports ?? []) { args.Add("-p"); args.Add(p); }
        foreach (var x in r.Env ?? []) { args.Add("-e"); args.Add(x); }
        foreach (var v in r.Volumes ?? []) { args.Add("-v"); args.Add(v); }
        if (r.Privileged) args.Add("--privileged");
        foreach (var cap in (r.CapAdd ?? []).Distinct()) args.Add("--cap-add=" + cap);
        foreach (var cap in (r.CapDrop ?? []).Distinct()) args.Add("--cap-drop=" + cap);
        args.Add(r.Image);
        return args;
    }

    // Un réseau qui n'existe pas encore est créé (les réseaux prédéfinis bridge, host et none existent toujours).
    async Task EnsureNetworkAsync(string? network, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(network)) return;
        var known = DockerParser.ParseNetworks(await Ok(["network", "ls", "--format", "{{json .}}"], "Listing networks", ct));
        if (!known.Any(n => n.Name == network)) await Ok(["network", "create", network], "Creating the network", ct);
    }

    public Task RunContainerAsync(RunContainerRequest r, CancellationToken ct = default)
    {
        if (Rules.ValidateRunContainer(r) is { } e) throw new OperationException(e);
        return Mutate(async () =>
        {
            await EnsureNetworkAsync(r.Network, ct);
            await Ok(RunArgs(r, r.Name ?? "", start: true), "Starting the container", ct);
        });
    }

    // ---- Modification d'un conteneur

    async Task<(JsonElement Container, JsonElement Image)> InspectAsync(string id, CancellationToken ct)
    {
        var c = JsonDocument.Parse(await Ok(["inspect", "--type", "container", "--format", "{{json .}}", id], "Reading the container", ct)).RootElement.Clone();
        var imageId = c.TryGetProperty("Image", out var i) ? i.GetString() ?? "" : "";
        var image = JsonDocument.Parse(await Ok(["image", "inspect", "--format", "{{json .}}", imageId], "Reading the image", ct)).RootElement.Clone();
        return (c, image);
    }

    public async Task<ContainerSettings> SettingsAsync(string id, CancellationToken ct = default)
    {
        if (Rules.ValidateDockerName(id) is { } e) throw new OperationException(e);
        var (c, image) = await InspectAsync(id, ct);
        return new ContainerSettings(DockerInspect.Settings(c, image), DockerInspect.Running(c), DockerInspect.Stack(c), DockerInspect.Unsupported(c, image));
    }

    static bool SameList(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a ?? []).Order(StringComparer.Ordinal).SequenceEqual((b ?? []).Order(StringComparer.Ordinal));

    /// <summary>
    /// Applique de nouveaux réglages. Seule la politique de redémarrage change : « docker update », à chaud. Tout autre changement
    /// recrée le conteneur : l'ancien est arrêté et renommé, le nouveau est créé ; s'il échoue, l'ancien est remis en place tel quel.
    /// Les données des volumes et des dossiers du NAS restent ; ce qui était écrit dans le conteneur lui-même est perdu.
    /// </summary>
    public Task UpdateContainerAsync(string id, RunContainerRequest r, CancellationToken ct = default)
    {
        if (Rules.ValidateDockerName(id) is { } e0) throw new OperationException(e0);
        if (Rules.ValidateRunContainer(r) is { } e1) throw new OperationException(e1);
        return Mutate(async () =>
        {
            var (c, image) = await InspectAsync(id, ct);
            var current = DockerInspect.Settings(c, image);
            var name = string.IsNullOrEmpty(r.Name) ? current.Name! : r.Name;
            var sameExceptRestart = r.Image == current.Image && name == current.Name && (r.Network ?? "") == (current.Network ?? "")
                && SameList(r.Ports, current.Ports) && SameList(r.Env, current.Env) && SameList(r.Volumes, current.Volumes)
                && r.Privileged == current.Privileged
                && Rules.EffectiveCapabilities(r.Privileged, r.CapAdd, r.CapDrop).SetEquals(Rules.EffectiveCapabilities(current.Privileged, current.CapAdd, current.CapDrop)); // seuls les droits réels comptent

            if (sameExceptRestart)
            {
                if (r.Restart != current.Restart) await Ok(["update", "--restart", r.Restart, id], "Updating the container", ct);
                return;
            }
            if (DockerInspect.Stack(c) is { } stack)
                throw new OperationException(Msg.F("This container belongs to the stack “{0}”: edit the stack instead.", stack));
            if (DockerInspect.Unsupported(c, image).Count > 0)
                throw new OperationException(Msg.F("This container uses settings that cannot be kept when it is recreated: only its restart policy can be changed here."));

            var wasRunning = DockerInspect.Running(c);
            var oldName = current.Name!;
            var suffix = "-old-" + Guid.NewGuid().ToString("N")[..6];
            var parked = oldName.Length + suffix.Length <= 64 ? oldName + suffix : "old-" + Guid.NewGuid().ToString("N")[..12];

            await EnsureNetworkAsync(r.Network, ct);
            if (wasRunning) await Ok(["stop", id], "Stopping the container", ct);
            await Ok(["rename", id, parked], "Renaming the container", ct);
            try
            {
                await Ok(RunArgs(r, name, start: wasRunning), "Creating the container", ct);
            }
            catch (OperationException failure)
            {
                // Retour en arrière : on retire ce qui a pu être créé, puis l'ancien conteneur reprend son nom et son état.
                try
                {
                    await run.RunAsync(Bin, ["rm", "-f", name], ct: ct);
                    await Ok(["rename", parked, oldName], "Renaming the container", ct);
                    if (wasRunning) await Ok(["start", oldName], "Starting the container", ct);
                }
                catch (OperationException restoreFailure)
                {
                    throw new OperationException(Msg.F("The container could not be recreated and the previous one could not be restored ({0}): {1}", restoreFailure.Message, failure.Message));
                }
                throw new OperationException(Msg.F("The container could not be recreated; the previous one was restored. {0}", failure.Message));
            }
            await Ok(["rm", parked], "Deleting the container", ct);
        });
    }

    // ---- Images

    public async Task<IReadOnlyList<ImageInfo>> ImagesAsync(CancellationToken ct = default) =>
        DockerParser.ParseImages(await Ok(["image", "ls", "--format", "{{json .}}"], "Listing images", ct));

    public Task PullImageAsync(string image, CancellationToken ct = default)
    {
        if (Rules.ValidateImageRef(image) is { } e) throw new OperationException(e);
        return Mutate(async () => await Ok(["pull", image], "Downloading the image", ct));
    }

    public Task RemoveImageAsync(string id, CancellationToken ct = default)
    {
        if (Rules.ValidateImageRef(id) is { } e) throw new OperationException(e);
        return Mutate(async () => await Ok(["rmi", id], "Deleting the image", ct));
    }

    /// <summary>Supprime les images sans étiquette (« dangling ») ; renvoie le résumé de docker.</summary>
    public async Task<string> PruneImagesAsync(CancellationToken ct = default)
    {
        var text = "";
        await Mutate(async () => text = await Ok(["image", "prune", "-f"], "Cleaning the images", ct));
        return text.Trim();
    }

    // ---- Volumes et réseaux

    public async Task<IReadOnlyList<DockerVolume>> VolumesAsync(CancellationToken ct = default) =>
        DockerParser.ParseVolumes(await Ok(["volume", "ls", "--format", "{{json .}}"], "Listing volumes", ct));

    public Task CreateVolumeAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidateDockerName(name) is { } e) throw new OperationException(e);
        return Mutate(async () => await Ok(["volume", "create", name], "Creating the volume", ct));
    }

    public Task RemoveVolumeAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidateDockerName(name) is { } e) throw new OperationException(e);
        return Mutate(async () => await Ok(["volume", "rm", name], "Deleting the volume", ct));
    }

    public async Task<IReadOnlyList<DockerNetwork>> NetworksAsync(CancellationToken ct = default) =>
        DockerParser.ParseNetworks(await Ok(["network", "ls", "--format", "{{json .}}"], "Listing networks", ct));

    public Task CreateNetworkAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidateDockerName(name) is { } e) throw new OperationException(e);
        return Mutate(async () => await Ok(["network", "create", name], "Creating the network", ct));
    }

    public Task RemoveNetworkAsync(string name, CancellationToken ct = default)
    {
        if (Rules.ValidateDockerName(name) is { } e) throw new OperationException(e);
        return Mutate(async () => await Ok(["network", "rm", name], "Deleting the network", ct));
    }

    // ---- Stacks Compose

    async Task RequireComposeAsync(CancellationToken ct)
    {
        var s = await StatusAsync(ct);
        if (s.State == DockerState.NotInstalled) throw new OperationException(Msg.F("Docker is not installed."));
        if (s.State == DockerState.Stopped) throw new OperationException(Msg.F("The Docker service is not running."));
        if (s.ComposeVersion is null) throw new OperationException(Msg.F("Docker Compose is not installed (package docker-compose)."));
    }

    public async Task<IReadOnlyList<StackInfo>> StacksAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(StacksDir)) return [];
        var containers = (await StatusAsync(ct)).State == DockerState.Ready ? await ContainersAsync(ct) : [];
        return Directory.EnumerateDirectories(StacksDir)
            .Select(Path.GetFileName).OfType<string>()
            .Where(n => Rules.ValidateStackName(n) is null && File.Exists(StackFile(n)))
            .Order(StringComparer.Ordinal)
            .Select(n =>
            {
                var mine = containers.Where(c => c.Project == n).ToList();
                return new StackInfo(n, mine.Count(c => c.State == "running"), mine.Count);
            }).ToList();
    }

    public StackContent ReadStack(string name)
    {
        if (Rules.ValidateStackName(name) is { } e) throw new OperationException(e);
        var file = StackFile(name);
        if (!File.Exists(file)) throw new OperationException(Msg.F("Stack “{0}” does not exist.", name));
        return new StackContent(name, File.ReadAllText(file));
    }

    /// <summary>Enregistre le fichier après l'avoir fait valider par « docker compose config » : un fichier invalide n'écrase jamais l'ancien.</summary>
    public Task SaveStackAsync(string name, string content, CancellationToken ct = default)
    {
        if (Rules.ValidateStackName(name) is { } e1) throw new OperationException(e1);
        if (Rules.ValidateStackContent(content) is { } e2) throw new OperationException(e2);
        return Mutate(async () =>
        {
            await RequireComposeAsync(ct);
            var dir = Path.Combine(StacksDir, name);
            var created = !Directory.Exists(dir);
            Directory.CreateDirectory(dir);
            var tmp = Path.Combine(dir, "compose.yml.new");
            await File.WriteAllTextAsync(tmp, content, ct);
            try
            {
                var r = await run.RunAsync(Bin, ["compose", "-p", name, "-f", tmp, "config", "-q"], ct: ct);
                if (r.ExitCode != 0)
                    throw new OperationException(Msg.F("The compose file is invalid: {0}", (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim()));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead); // peut contenir des mots de passe
                File.Move(tmp, StackFile(name), overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                if (created && !File.Exists(StackFile(name))) Directory.Delete(dir, recursive: true); // un fichier refusé ne laisse pas de dossier vide
            }
        });
    }

    public Task StackActionAsync(string name, string action, CancellationToken ct = default)
    {
        if (Rules.ValidateStackName(name) is { } e) throw new OperationException(e);
        return Mutate(async () =>
        {
            var file = StackFile(name);
            if (!File.Exists(file)) throw new OperationException(Msg.F("Stack “{0}” does not exist.", name));
            await RequireComposeAsync(ct);
            string[] baseArgs = ["compose", "-p", name, "-f", file];
            switch (action)
            {
                case "up": await Ok([.. baseArgs, "up", "-d", "--remove-orphans"], "Starting the stack", ct); break;
                case "down": await Ok([.. baseArgs, "down"], "Stopping the stack", ct); break;
                case "update":
                    await Ok([.. baseArgs, "pull"], "Updating the stack", ct);
                    await Ok([.. baseArgs, "up", "-d", "--remove-orphans"], "Updating the stack", ct);
                    break;
                case "remove":
                    await Ok([.. baseArgs, "down"], "Stopping the stack", ct); // les volumes nommés restent
                    Directory.Delete(Path.Combine(StacksDir, name), recursive: true);
                    break;
                default: throw new OperationException(Msg.F("Unknown action."));
            }
        });
    }
}
