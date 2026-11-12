using System.Text.Json;
using System.Text.RegularExpressions;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>Quel outil écrit la configuration réseau de cette machine.</summary>
public static class NetBackend
{
    /// <summary><c>netplan</c> (Ubuntu), <c>ifupdown</c> (Debian) ou <c>none</c> (NetworkManager, systemd-networkd seul… : lecture seule).</summary>
    public static string Detect(IConfiguration cfg)
    {
        if (cfg["URSANAS_FAKE"] == "1") return "netplan";
        var root = cfg["Network:Root"] ?? "/";
        string P(string path) => Path.Combine(root, path.TrimStart('/'));
        if (File.Exists(P("/run/NetworkManager/NetworkManager.pid"))) return "none";
        var hasNetplan = File.Exists(P("/usr/sbin/netplan")) || File.Exists(P("/usr/bin/netplan"));
        if (hasNetplan && Directory.Exists(P("/etc/netplan")) && Directory.GetFiles(P("/etc/netplan"), "*.yaml").Length > 0) return "netplan";
        var hasIfup = File.Exists(P("/sbin/ifup")) || File.Exists(P("/usr/sbin/ifup"));
        if (hasIfup && File.Exists(P("/etc/network/interfaces"))) return "ifupdown";
        return "none";
    }
}

/// <summary>
/// Modifie l'adressage d'une interface avec un filet de sécurité : la nouvelle configuration s'applique tout de suite, puis l'ancienne revient
/// seule si personne ne confirme dans la minute (une erreur d'adresse couperait l'accès à l'interface web et à SSH).
/// L'état « en attente » est écrit sur disque : un redémarrage de l'agent reprend le compte à rebours, ou revient en arrière s'il est échu.
/// </summary>
public partial class NetworkChangeService(ICommandRunner runner, NetworkService net, IConfiguration cfg, ILogger<NetworkChangeService> log)
{
    public sealed record FileBackup(string Path, string? Content);
    public sealed record PendingState(string Iface, string Backend, DateTime DeadlineUtc, List<FileBackup> Files);

    readonly SemaphoreSlim lockObj = new(1, 1);
    PendingState? pending;
    CancellationTokenSource? timer;

    string Root => cfg["Network:Root"] ?? "/";
    string P(string absolute) => Path.Combine(Root, absolute.TrimStart('/'));
    string StateFile => cfg["Network:PendingFile"] ?? "/var/lib/ursanas/network-pending.json";
    bool Simulated => cfg["URSANAS_FAKE"] == "1";
    int ConfirmSeconds => cfg.GetValue("Network:ConfirmSeconds", NetRules.ConfirmSeconds);

    [GeneratedRegex(@"^\s*source(-directory)?\s+/etc/network/interfaces\.d(/\*)?\s*$", RegexOptions.Multiline)]
    private static partial Regex SourcesDir();

    public NetPending? GetPending() => pending is null ? null
        : new NetPending(pending.Iface, pending.DeadlineUtc, Math.Max(0, (int)Math.Ceiling((pending.DeadlineUtc - DateTime.UtcNow).TotalSeconds)));

    /// <summary>
    /// Applique le nouvel adressage, puis le vérifie : l'adresse est bien portée par l'interface, la route par défaut existe et la passerelle répond.
    /// Si tout est bon, le changement est conservé ; sinon l'ancienne configuration revient aussitôt et l'erreur dit pourquoi.
    /// Ce que l'agent ne peut pas savoir : si le poste de l'administrateur atteint le NAS à sa nouvelle adresse.
    /// </summary>
    public async Task EditAsync(NetEditRequest req, CancellationToken ct = default)
    {
        if (NetRules.Validate(req) is { } invalid) throw new OperationException(invalid);
        var backend = NetBackend.Detect(cfg);
        if (backend == "none") throw new OperationException(Msg.F("The network configuration of this machine cannot be edited from here (netplan or ifupdown required)."));
        if (backend == "ifupdown" && ((req.Ipv4.Mode == "auto" && req.Ipv4.DnsMode == "manual") || (req.Ipv6.Mode == "auto" && req.Ipv6.DnsMode == "manual")))
            throw new OperationException(Msg.F("Manual DNS servers need a manual address on this system."));

        var snap = await net.SnapshotAsync(ct);
        var iface = snap.Interfaces.FirstOrDefault(i => i.Name == req.Iface) ?? throw new OperationException(Msg.F("Network interface {0} not found.", req.Iface));
        if (iface.Kind is not ("ethernet" or "bridge") || iface.Master is not null) throw new OperationException(Msg.F("This interface cannot be edited here."));

        await lockObj.WaitAsync(ct);
        try
        {
            if (pending is not null) throw new OperationException(Msg.F("A network change is already in progress."));
            if (Simulated) return;
            var changes = backend == "netplan" ? PlanNetplan(req, iface.Kind) : PlanIfupdown(req);
            var backups = changes.Keys.Select(path => new FileBackup(path, File.Exists(path) ? File.ReadAllText(path) : null)).ToList();
            var state = new PendingState(req.Iface, backend, DateTime.UtcNow.AddSeconds(ConfirmSeconds), backups);

            // L'état d'abord : si l'agent s'arrête en plein milieu, le prochain démarrage sait quoi restaurer. L'échéance sert aussi de garde-fou.
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
            pending = state;
            StartTimer(state.DeadlineUtc);
            try
            {
                foreach (var (path, content) in changes) Write(path, content);
                await ApplyAsync(backend, req.Iface, ct);
                if (await VerifyAsync(req, ct) is { } problem) throw new OperationException(problem);
            }
            catch
            {
                await RollbackLockedAsync(CancellationToken.None);
                throw;
            }
            timer?.Cancel();
            pending = null;
            try { File.Delete(StateFile); } catch (IOException) { }
            log.LogInformation("Network change on {Iface} applied and verified", req.Iface);
        }
        finally { lockObj.Release(); }
    }

    int VerifySeconds => cfg.GetValue("Network:VerifySeconds", 30);
    int VerifyPollMs => cfg.GetValue("Network:VerifyPollMs", 2000);

    /// <summary>Attend (jusqu'à <c>VerifySeconds</c>, un bail DHCP peut tarder) que l'état réel corresponde à la demande. Renvoie le problème, ou null si tout est bon.</summary>
    async Task<string?> VerifyAsync(NetEditRequest req, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(VerifySeconds);
        string problem;
        while (true)
        {
            problem = await CheckOnceAsync(req, ct);
            if (problem.Length == 0) return null;
            if (DateTime.UtcNow >= deadline) return problem;
            await Task.Delay(VerifyPollMs, ct);
        }
    }

    async Task<string> CheckOnceAsync(NetEditRequest req, CancellationToken ct)
    {
        var iface = (await net.SnapshotAsync(ct)).Interfaces.FirstOrDefault(i => i.Name == req.Iface);
        if (iface is null) return Msg.F("The new settings could not be verified: the interface disappeared. The previous settings were restored.");
        var v4 = req.Ipv4;
        string? gateway4;
        if (v4.Mode == "manual")
        {
            var prefix = NetRules.PrefixOf4(v4.Netmask);
            if (!iface.Ipv4.Any(a => a.Address == v4.Address && a.PrefixLength == prefix)) return Msg.F("The new settings could not be verified: the IPv4 address {0} is not set on {1}. The previous settings were restored.", v4.Address!, req.Iface);
            gateway4 = string.IsNullOrWhiteSpace(v4.Gateway) ? null : v4.Gateway;
            if (gateway4 is not null && iface.Gateway4 != gateway4) return Msg.F("The new settings could not be verified: the default route through {0} is missing. The previous settings were restored.", gateway4);
        }
        else
        {
            if (!iface.Ipv4.Any(a => a.Scope == "global")) return Msg.F("The new settings could not be verified: no IPv4 address was obtained (DHCP). The previous settings were restored.");
            gateway4 = iface.Gateway4;
        }
        var v6 = req.Ipv6;
        if (v6.Mode == "manual" && !iface.Ipv6.Any(a => string.Equals(a.Address, v6.Address, StringComparison.OrdinalIgnoreCase)))
            return Msg.F("The new settings could not be verified: the IPv6 address {0} is not set on {1}. The previous settings were restored.", v6.Address!, req.Iface);
        if (gateway4 is not null && !await GatewayAnswersAsync(req.Iface, gateway4, ct)) return Msg.F("The new settings could not be verified: the gateway {0} does not answer. The previous settings were restored.", gateway4);
        return "";
    }

    /// <summary>La passerelle est joignable sur le réseau local : un ping (facultatif, certains routeurs ne répondent pas), puis une entrée de voisinage résolue.</summary>
    async Task<bool> GatewayAnswersAsync(string iface, string gateway, CancellationToken ct)
    {
        try { await runner.RunAsync("ping", ["-c", "1", "-W", "2", "-I", iface, gateway], ct: ct); }
        catch (System.ComponentModel.Win32Exception) { } // ping absent : la table des voisins suffit
        var r = await runner.RunAsync("ip", ["-j", "neigh", "show", gateway, "dev", iface], ct: ct);
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.StdOut) ? "[]" : r.StdOut);
            foreach (var n in doc.RootElement.EnumerateArray())
            {
                if (!n.TryGetProperty("lladdr", out _)) continue;
                var states = n.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.Array ? s.EnumerateArray().Select(x => x.GetString()).ToList() : [];
                if (!states.Contains("FAILED") && !states.Contains("INCOMPLETE")) return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    /// <summary>Au démarrage de l'agent : reprend un changement resté en attente (délai échu : retour arrière immédiat).</summary>
    public async Task RestoreOnStartupAsync()
    {
        if (Simulated || !File.Exists(StateFile)) return;
        try
        {
            var state = JsonSerializer.Deserialize<PendingState>(File.ReadAllText(StateFile));
            if (state is null) return;
            await lockObj.WaitAsync();
            try
            {
                pending = state;
                if (state.DeadlineUtc <= DateTime.UtcNow) await RollbackLockedAsync(CancellationToken.None);
                else StartTimer(state.DeadlineUtc);
            }
            finally { lockObj.Release(); }
        }
        catch (Exception e) when (e is JsonException or IOException) { log.LogError(e, "Pending network change could not be read"); }
    }

    void StartTimer(DateTime deadline)
    {
        timer?.Cancel();
        var cts = timer = new CancellationTokenSource();
        var current = pending;
        _ = Task.Run(async () =>
        {
            try
            {
                var wait = deadline - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cts.Token);
                await lockObj.WaitAsync(CancellationToken.None);
                try { if (ReferenceEquals(pending, current)) { log.LogWarning("Network change not confirmed in time: reverting"); await RollbackLockedAsync(CancellationToken.None); } }
                finally { lockObj.Release(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { log.LogError(e, "Network rollback failed"); }
        });
    }

    /// <summary>Restaure les fichiers d'origine puis réapplique. À appeler avec le verrou pris.</summary>
    async Task RollbackLockedAsync(CancellationToken ct)
    {
        var state = pending;
        if (state is null) return;
        timer?.Cancel();
        if (!Simulated)
        {
            foreach (var f in state.Files)
            {
                try { if (f.Content is null) File.Delete(f.Path); else File.WriteAllText(f.Path, f.Content); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogError(e, "Could not restore {Path}", f.Path); }
            }
            try { await ApplyAsync(state.Backend, state.Iface, ct); }
            catch (OperationException e) { log.LogError("Reapplying the previous network configuration failed: {Message}", e.Message); }
            try { File.Delete(StateFile); } catch (IOException) { }
        }
        pending = null;
    }

    static string Lf(string s) => s.Replace("\r", "");

    static void Write(string path, string? content)
    {
        if (content is null) { File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var secret = path.Contains("netplan", StringComparison.Ordinal);
        File.WriteAllText(path, content);
        if (secret && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // netplan se plaint des fichiers lisibles par tous
    }

    async Task ApplyAsync(string backend, string iface, CancellationToken ct)
    {
        if (backend == "netplan")
        {
            await Run("netplan", ["generate"], ct);
            await Run("netplan", ["apply"], ct);
            return;
        }
        // ifupdown : on coupe l'interface, on vide ses adresses (l'ancienne ne doit pas rester), puis on la remonte avec la nouvelle configuration.
        await runner.RunAsync("ifdown", ["--force", iface], ct: ct);
        await runner.RunAsync("ip", ["-4", "addr", "flush", "dev", iface], ct: ct);
        await runner.RunAsync("ip", ["-6", "addr", "flush", "dev", iface, "scope", "global"], ct: ct);
        await Run("ifup", [iface], ct);
    }

    async Task Run(string file, string[] args, CancellationToken ct)
    {
        var r = await runner.RunAsync(file, args, ct: ct);
        if (r.ExitCode != 0)
        {
            var detail = (r.StdErr.Length > 0 ? r.StdErr : r.StdOut).Trim().Split('\n')[0];
            throw new OperationException(Msg.F("Applying the network configuration failed: {0}", detail.Length > 0 ? detail : file));
        }
    }

    // ---------------------------------------------------------------- plans : chemin → nouveau contenu (null = supprimer)

    Dictionary<string, string?> PlanNetplan(NetEditRequest req, string kind)
    {
        var dir = P("/etc/netplan");
        var ours = Path.Combine(dir, $"90-ursanas-{req.Iface}.yaml");
        var changes = new Dictionary<string, string?>();
        string? section = null;
        foreach (var file in Directory.GetFiles(dir, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (file == ours) continue;
            var text = File.ReadAllText(file);
            if (NetplanConfig.SectionOf(text, req.Iface) is not { } s) continue;
            if (s is not ("ethernets" or "bridges")) throw new OperationException(Msg.F("This interface cannot be edited here."));
            section ??= s;
            var stripped = NetplanConfig.StripIface(text, req.Iface);
            if (stripped != Lf(text)) changes[file] = stripped;
        }
        section ??= kind == "bridge" ? throw new OperationException(Msg.F("This interface cannot be edited here.")) : "ethernets";
        changes[ours] = NetplanConfig.Generate(req, section);
        // cloud-init réécrirait sa propre configuration réseau au prochain démarrage : on la désactive si on a dû toucher son fichier.
        var cloudDir = P("/etc/cloud/cloud.cfg.d");
        if (changes.Keys.Any(f => Path.GetFileName(f).Contains("cloud-init", StringComparison.Ordinal)) && Directory.Exists(P("/etc/cloud")))
        {
            var marker = Path.Combine(cloudDir, "99-disable-network-config.cfg");
            if (!File.Exists(marker)) changes[marker] = "# Written by UrsaNAS: the network is configured from the UrsaNAS Network page.\nnetwork: {config: disabled}\n";
        }
        return changes;
    }

    Dictionary<string, string?> PlanIfupdown(NetEditRequest req)
    {
        var main = P("/etc/network/interfaces");
        var dir = P("/etc/network/interfaces.d");
        var ours = Path.Combine(dir, $"ursanas-{req.Iface}");
        var files = new List<string> { main };
        if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir).Where(f => f != ours && Regex.IsMatch(Path.GetFileName(f), "^[A-Za-z0-9_-]+$")).OrderBy(f => f, StringComparer.Ordinal));

        var changes = new Dictionary<string, string?>();
        var hasAuto = false;
        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            var text = File.ReadAllText(file);
            hasAuto |= IfupdownConfig.HasAuto(text, req.Iface);
            var stripped = IfupdownConfig.Strip(text, req.Iface);
            if (stripped != Lf(text)) changes[file] = stripped;
        }
        var block = IfupdownConfig.Generate(req, addAuto: !hasAuto);
        var mainText = changes.TryGetValue(main, out var m) && m is not null ? m : File.ReadAllText(main);
        if (SourcesDir().IsMatch(mainText)) changes[ours] = block;
        else
        {
            // pas de « source » : le bloc va dans le fichier principal, à la suite
            changes.Remove(ours);
            changes[main] = mainText.TrimEnd('\n') + "\n\n" + block;
        }
        if (req.Ipv4.DnsMode == "manual" || req.Ipv6.DnsMode == "manual")
        {
            var resolv = P("/etc/resolv.conf");
            if (File.Exists(resolv) && new FileInfo(resolv).LinkTarget is null) changes[resolv] = IfupdownConfig.ResolvConf(req);
        }
        return changes;
    }
}
