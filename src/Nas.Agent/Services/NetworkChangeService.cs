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
    public sealed record PendingState(string Iface, string Backend, DateTime DeadlineUtc, List<FileBackup> Files, string? DropBridge = null, string? RestoreBridge = null, string? DropLink = null);

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
            var state = new PendingState(req.Iface, backend, DateTime.UtcNow.AddSeconds(ConfirmSeconds), Backups(changes));
            await CommitLockedAsync(state, changes, null, () => ApplyAsync(backend, req.Iface, null, ct), () => VerifyAsync(t => CheckOnceAsync(req, t), ct));
            log.LogInformation("Network change on {Iface} applied and verified", req.Iface);
        }
        finally { lockObj.Release(); }
    }

    int VerifySeconds => cfg.GetValue("Network:VerifySeconds", 30);
    int VerifyPollMs => cfg.GetValue("Network:VerifyPollMs", 2000);

    /// <summary>Attend (jusqu'à <c>VerifySeconds</c>, un bail DHCP peut tarder) que l'état réel corresponde à la demande. Renvoie le problème, ou null si tout est bon.</summary>
    async Task<string?> VerifyAsync(Func<CancellationToken, Task<string>> check, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(VerifySeconds);
        string problem;
        while (true)
        {
            problem = await check(ct);
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
            if (state.DropBridge is not null) await DelBridgeAsync(state.DropBridge, ct);
            if (state.DropLink is not null) await DelLinkAsync(state.DropLink, ct);
            try { await ApplyAsync(state.Backend, state.Iface, state.RestoreBridge, ct); }
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

    async Task ApplyAsync(string backend, string iface, string? bridge, CancellationToken ct)
    {
        if (backend == "netplan")
        {
            await Run("netplan", ["generate"], ct);
            await Run("netplan", ["apply"], ct);
            return;
        }
        // ifupdown : on coupe l'interface, on vide ses adresses (l'ancienne ne doit pas rester), puis on la remonte avec la nouvelle configuration.
        // « iface » peut nommer plusieurs cartes séparées par des virgules (les membres d'une agrégation).
        var names = iface.Split(',');
        foreach (var n in names)
        {
            await runner.RunAsync("ifdown", ["--force", n], ct: ct);
            await runner.RunAsync("ip", ["-4", "addr", "flush", "dev", n], ct: ct);
            await runner.RunAsync("ip", ["-6", "addr", "flush", "dev", n, "scope", "global"], ct: ct);
        }
        if (bridge is not null) await Run("ifup", [bridge], ct);   // le pont Open vSwitch reprend sa carte comme port; l'agrégation reprend ses membres
        if (bridge is not null && bridge.StartsWith("bond", StringComparison.Ordinal)) return;   // ifenslave a déjà enrôlé les membres
        foreach (var n in names) await Run("ifup", [n], ct);
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

    static List<FileBackup> Backups(Dictionary<string, string?> changes)
        => changes.Keys.Select(path => new FileBackup(path, File.Exists(path) ? File.ReadAllText(path) : null)).ToList();

    /// <summary>
    /// Écrit l'état d'abord (un arrêt de l'agent en plein milieu se rattrape au démarrage), écrit les fichiers, applique, vérifie ; revient en arrière au moindre échec.
    /// <paramref name="beforeWrite"/> s'exécute avec l'ancienne configuration encore en place (par exemple couper l'interface proprement). À appeler avec le verrou pris.
    /// </summary>
    async Task CommitLockedAsync(PendingState state, Dictionary<string, string?> changes, Func<Task>? beforeWrite, Func<Task> apply, Func<Task<string?>> verify)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(state));
        pending = state;
        StartTimer(state.DeadlineUtc);
        try
        {
            if (beforeWrite is not null) await beforeWrite();
            foreach (var (path, content) in changes) Write(path, content);
            await apply();
            if (await verify() is { } problem) throw new OperationException(problem);
        }
        catch
        {
            await RollbackLockedAsync(CancellationToken.None);
            throw;
        }
        timer?.Cancel();
        pending = null;
        try { File.Delete(StateFile); } catch (IOException) { }
    }

    // ---------------------------------------------------------------- Open vSwitch : une carte comme sortie d'un pont

    async Task DelBridgeAsync(string bridge, CancellationToken ct)
    {
        try { await runner.RunAsync("ovs-vsctl", ["--if-exists", "del-br", bridge], ct: ct); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Ponts Open vSwitch existants, ou null si Open vSwitch n'est pas installé ou ne répond pas.</summary>
    async Task<IReadOnlyList<string>?> OvsBridgesAsync(CancellationToken ct)
    {
        try
        {
            var r = await runner.RunAsync("ovs-vsctl", ["--timeout=3", "list-br"], ct: ct);
            return r.ExitCode != 0 ? null : r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    async Task<IReadOnlyList<string>> PortsAsync(string bridge, CancellationToken ct)
    {
        var r = await runner.RunAsync("ovs-vsctl", ["--timeout=3", "list-ports", bridge], ct: ct);
        return r.ExitCode != 0 ? [] : r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>L'adressage actuel d'une interface, repris tel quel pour <paramref name="newName"/> (la carte qui le passe au pont, ou l'inverse).</summary>
    public static NetEditRequest Addressing(NetIface i, string newName)
    {
        var a4 = i.Ipv4.FirstOrDefault(a => a.Scope == "global");
        var dns4 = i.Dns.Where(d => !d.Contains(':')).ToList();
        var v4 = i.Dhcp4 || a4 is null ? new NetIpConfig("auto", null, null, null, "auto", [])
            : new NetIpConfig("manual", a4.Address, a4.Mask, i.Gateway4, dns4.Count > 0 ? "manual" : "auto", dns4);
        var a6 = i.Ipv6.FirstOrDefault(a => a.Scope == "global");
        var dns6 = i.Dns.Where(d => d.Contains(':')).ToList();
        var v6 = a6 is null || a6.Dynamic ? new NetIpConfig("auto", null, null, null, "auto", [])
            : new NetIpConfig("manual", a6.Address, a6.PrefixLength.ToString(), i.Gateway6, dns6.Count > 0 ? "manual" : "auto", dns6);
        return new NetEditRequest(newName, v4, v6);
    }

    /// <summary>
    /// Met la carte en sortie d'un pont Open vSwitch créé pour elle (<c>ovsbrN</c>, qui reprend adresse IP, passerelle et adresse MAC), ou défait ce montage.
    /// Même filet que l'édition d'adresse : appliqué, vérifié (adresse, route, passerelle, carte bien dans le pont), sinon l'ancienne configuration revient.
    /// </summary>
    public async Task UplinkAsync(OvsUplinkRequest r, CancellationToken ct = default)
    {
        if (!NetRules.IsIfaceName(r.Name)) throw new OperationException(Msg.F("Unknown setting."));
        var backend = NetBackend.Detect(cfg);
        if (backend == "none") throw new OperationException(Msg.F("The network configuration of this machine cannot be edited from here (netplan or ifupdown required)."));
        await lockObj.WaitAsync(ct);
        try
        {
            if (pending is not null) throw new OperationException(Msg.F("A network change is already in progress."));
            if (Simulated) return;
            var bridges = await OvsBridgesAsync(ct) ?? throw new OperationException(Msg.F("Open vSwitch is not enabled: turn it on in the network Settings first."));
            var snap = await net.SnapshotAsync(ct);
            if (r.Enable) await EnableUplinkAsync(r.Name, backend, snap, bridges, ct);
            else await DisableUplinkAsync(r.Name, backend, snap, ct);
        }
        finally { lockObj.Release(); }
    }

    async Task EnableUplinkAsync(string port, string backend, NetSnapshot snap, IReadOnlyList<string> bridges, CancellationToken ct)
    {
        var iface = snap.Interfaces.FirstOrDefault(i => i.Name == port) ?? throw new OperationException(Msg.F("Network interface {0} not found.", port));
        if (iface.Kind is not ("ethernet" or "bond") || iface.Master is not null) throw new OperationException(Msg.F("This interface cannot be edited here."));   // une carte, ou une agrégation de liens
        var bridge = Enumerable.Range(0, 100).Select(n => "ovsbr" + n).First(b => !bridges.Contains(b) && snap.Interfaces.All(i => i.Name != b));
        var req = Addressing(iface, bridge);
        if (NetRules.Validate(req) is { } invalid) throw new OperationException(invalid);
        if (backend == "ifupdown" && ((req.Ipv4.Mode == "auto" && req.Ipv4.DnsMode == "manual") || (req.Ipv6.Mode == "auto" && req.Ipv6.DnsMode == "manual")))
            throw new OperationException(Msg.F("Manual DNS servers need a manual address on this system."));

        var changes = backend == "netplan" ? PlanOvsNetplan(req, port, iface.Mac) : PlanOvsIfupdown(req, port, iface.Mac);
        var state = new PendingState(port, backend, DateTime.UtcNow.AddSeconds(ConfirmSeconds), Backups(changes), DropBridge: bridge);
        await CommitLockedAsync(state, changes,
            backend == "ifupdown" ? () => runner.RunAsync("ifdown", ["--force", port], ct: ct) : null,   // avec l'ancien bloc encore en place : le client DHCP s'arrête proprement
            () => ApplyAsync(backend, port, bridge, ct),
            () => VerifyAsync(async t =>
            {
                var problem = await CheckOnceAsync(req, t);
                if (problem.Length > 0) return problem;
                return (await PortsAsync(bridge, t)).Contains(port) ? ""
                    : Msg.F("The new settings could not be verified: {0} is not a port of the bridge {1}. The previous settings were restored.", port, bridge);
            }, ct));
        log.LogInformation("{Port} is now the uplink of the Open vSwitch bridge {Bridge}", port, bridge);
    }

    async Task DisableUplinkAsync(string bridge, string backend, NetSnapshot snap, CancellationToken ct)
    {
        var iface = snap.Interfaces.FirstOrDefault(i => i.Name == bridge) ?? throw new OperationException(Msg.F("Network interface {0} not found.", bridge));
        if (iface.Kind != "ovs") throw new OperationException(Msg.F("This bridge was not created by UrsaNAS."));
        var pre = new Dictionary<string, string?>();
        string? port = null;
        var portOptions = "";
        if (backend == "netplan")
        {
            var ours = P("/etc/netplan/" + NetplanConfig.OvsFileName(bridge));
            if (File.Exists(ours))
            {
                pre[ours] = null;
                port = Regex.Match(File.ReadAllText(ours), @"interfaces: \[([^\],]+)\]") is { Success: true } m ? m.Groups[1].Value.Trim() : null;
            }
        }
        else
        {
            var main = P("/etc/network/interfaces");
            var dir = P("/etc/network/interfaces.d");
            var files = new List<string> { main };
            if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir).Where(f => Regex.IsMatch(Path.GetFileName(f), "^[A-Za-z0-9_-]+$")));
            foreach (var f in files.Where(File.Exists))
            {
                var text = File.ReadAllText(f);
                var found = IfupdownConfig.FindOvs(text).Where(o => o.Bridge == bridge).Select(o => o.Port).FirstOrDefault();
                if (found is null) continue;
                port = found;
                portOptions = IfupdownConfig.BondOptions(text, found);   // si le port est une agrégation, ses options reviennent avec elle
                var rest = IfupdownConfig.RemoveOvs(text, bridge);
                pre[f] = f != main && rest.Trim().Length == 0 ? null : rest;
            }
        }
        if (port is null || !NetRules.IsIfaceName(port)) throw new OperationException(Msg.F("This bridge was not created by UrsaNAS."));

        var req = Addressing(iface, port);
        if (NetRules.Validate(req) is { } invalid) throw new OperationException(invalid);
        if (backend == "ifupdown" && ((req.Ipv4.Mode == "auto" && req.Ipv4.DnsMode == "manual") || (req.Ipv6.Mode == "auto" && req.Ipv6.DnsMode == "manual")))
            throw new OperationException(Msg.F("Manual DNS servers need a manual address on this system."));
        var changes = backend == "netplan" ? PlanNetplan(req, "ethernet", pre) : PlanIfupdown(req, pre, portOptions);
        var state = new PendingState(port, backend, DateTime.UtcNow.AddSeconds(ConfirmSeconds), Backups(changes), RestoreBridge: bridge);
        await CommitLockedAsync(state, changes,
            backend == "ifupdown" ? () => runner.RunAsync("ifdown", ["--force", bridge], ct: ct) : null,
            async () => { await DelBridgeAsync(bridge, ct); await ApplyAsync(backend, port, null, ct); },
            () => VerifyAsync(t => CheckOnceAsync(req, t), ct));
        log.LogInformation("The Open vSwitch bridge {Bridge} was removed; {Port} carries the addressing again", bridge, port);
    }

    Dictionary<string, string?> PlanOvsNetplan(NetEditRequest bridgeReq, string port, string? mac)
    {
        var dir = P("/etc/netplan");
        var ours = Path.Combine(dir, NetplanConfig.OvsFileName(bridgeReq.Iface));
        var changes = new Dictionary<string, string?>();
        var portSection = "ethernets";
        foreach (var file in Directory.GetFiles(dir, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (file == ours) continue;
            var text = File.ReadAllText(file);
            if (NetplanConfig.SectionOf(text, port) is not { } s) continue;
            if (s is not ("ethernets" or "bonds")) throw new OperationException(Msg.F("This interface cannot be edited here."));
            portSection = s;
            var stripped = NetplanConfig.StripIface(text, port);
            if (stripped != Lf(text)) changes[file] = stripped;
        }
        changes[ours] = NetplanConfig.GenerateOvsBridge(bridgeReq, port, mac, portSection);
        GuardCloudInit(changes);
        return changes;
    }

    Dictionary<string, string?> PlanOvsIfupdown(NetEditRequest bridgeReq, string port, string? mac)
    {
        var main = P("/etc/network/interfaces");
        var dir = P("/etc/network/interfaces.d");
        var ours = Path.Combine(dir, $"ursanas-ovs-{bridgeReq.Iface}");
        var files = new List<string> { main };
        if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir).Where(f => f != ours && Regex.IsMatch(Path.GetFileName(f), "^[A-Za-z0-9_-]+$")).OrderBy(f => f, StringComparer.Ordinal));
        var changes = new Dictionary<string, string?>();
        var portOptions = "";   // pour une agrégation : ses options bond-* suivent le port
        foreach (var file in files.Where(File.Exists))
        {
            var text = File.ReadAllText(file);
            if (portOptions.Length == 0) portOptions = IfupdownConfig.BondOptions(text, port);
            var stripped = IfupdownConfig.Strip(text, port);
            if (stripped != Lf(text)) changes[file] = stripped;
        }
        var block = IfupdownConfig.GenerateOvsBridge(bridgeReq, port, mac, portOptions);
        var mainText = changes.TryGetValue(main, out var m) && m is not null ? m : File.ReadAllText(main);
        if (SourcesDir().IsMatch(mainText)) changes[ours] = block;
        else changes[main] = mainText.TrimEnd('\n') + "\n\n" + block;
        return changes;
    }

    /// <summary>cloud-init réécrirait sa propre configuration réseau au prochain démarrage : on la désactive si on a dû toucher son fichier.</summary>
    void GuardCloudInit(Dictionary<string, string?> changes)
    {
        if (!changes.Keys.Any(f => Path.GetFileName(f).Contains("cloud-init", StringComparison.Ordinal)) || !Directory.Exists(P("/etc/cloud"))) return;
        var marker = Path.Combine(P("/etc/cloud/cloud.cfg.d"), "99-disable-network-config.cfg");
        if (!File.Exists(marker)) changes[marker] = "# Written by UrsaNAS: the network is configured from the UrsaNAS Network page.\nnetwork: {config: disabled}\n";
    }

    // ---------------------------------------------------------------- agrégation de liens (bond)

    async Task DelLinkAsync(string link, CancellationToken ct)
    {
        try { await runner.RunAsync("ip", ["link", "delete", link], ct: ct); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>
    /// Réunit des cartes Ethernet libres en une agrégation <c>bondN</c> qui reprend l'adressage (et l'adresse MAC) d'une d'elles, ou défait cette agrégation.
    /// Même filet que les autres changements : appliqué, vérifié (adresse, route, passerelle, membres enrôlés), sinon l'ancienne configuration revient.
    /// </summary>
    public async Task BondAsync(BondRequest r, CancellationToken ct = default)
    {
        if (BondRequest.Validate(r) is { } invalid) throw new OperationException(invalid);
        var backend = NetBackend.Detect(cfg);
        if (backend == "none") throw new OperationException(Msg.F("The network configuration of this machine cannot be edited from here (netplan or ifupdown required)."));
        await lockObj.WaitAsync(ct);
        try
        {
            if (pending is not null) throw new OperationException(Msg.F("A network change is already in progress."));
            if (Simulated) return;
            var snap = await net.SnapshotAsync(ct);
            if (r.Enable) await CreateBondAsync(r, backend, snap, ct);
            else await RemoveBondAsync(r.Name, backend, snap, ct);
        }
        finally { lockObj.Release(); }
    }

    async Task CreateBondAsync(BondRequest r, string backend, NetSnapshot snap, CancellationToken ct)
    {
        var cards = r.Members.Select(m => snap.Interfaces.FirstOrDefault(i => i.Name == m) ?? throw new OperationException(Msg.F("Network interface {0} not found.", m))).ToList();
        if (cards.Any(c => c.Kind != "ethernet" || c.Master is not null)) throw new OperationException(Msg.F("This interface cannot be edited here."));
        var bond = Enumerable.Range(0, 100).Select(n => "bond" + n).First(b => snap.Interfaces.All(i => i.Name != b));
        // l'agrégation reprend l'adressage d'une carte : celle qui porte la passerelle, sinon une qui a une adresse, sinon la première
        var source = cards.FirstOrDefault(c => c.Gateway4 is not null) ?? cards.FirstOrDefault(c => c.Ipv4.Any(a => a.Scope == "global")) ?? cards[0];
        var req = Addressing(source, bond);
        if (NetRules.Validate(req) is { } bad) throw new OperationException(bad);
        if (backend == "ifupdown" && ((req.Ipv4.Mode == "auto" && req.Ipv4.DnsMode == "manual") || (req.Ipv6.Mode == "auto" && req.Ipv6.DnsMode == "manual")))
            throw new OperationException(Msg.F("Manual DNS servers need a manual address on this system."));
        if (backend == "ifupdown") await EnsureIfenslaveAsync(ct);
        try { await runner.RunAsync("modprobe", ["bonding"], ct: ct); } catch (System.ComponentModel.Win32Exception) { }

        var members = r.Members.ToList();
        var changes = backend == "netplan" ? PlanBondNetplan(req, members, r.Mode, source.Mac) : PlanBondIfupdown(req, members, r.Mode, source.Mac);
        var joined = string.Join(',', members);
        var state = new PendingState(joined, backend, DateTime.UtcNow.AddSeconds(ConfirmSeconds), Backups(changes), DropLink: bond);
        await CommitLockedAsync(state, changes,
            backend == "ifupdown" ? async () => { foreach (var m in members) await runner.RunAsync("ifdown", ["--force", m], ct: ct); } : null,
            () => ApplyAsync(backend, joined, bond, ct),
            () => VerifyAsync(async t =>
            {
                var problem = await CheckOnceAsync(req, t);
                if (problem.Length > 0) return problem;
                var now = (await net.SnapshotAsync(t)).Interfaces;
                var missing = members.FirstOrDefault(m => now.FirstOrDefault(i => i.Name == m)?.Master != bond);
                return missing is null ? "" : Msg.F("The new settings could not be verified: {0} is not a member of {1}. The previous settings were restored.", missing, bond);
            }, ct));
        log.LogInformation("Link aggregation {Bond} created with {Members}", bond, joined);
    }

    async Task EnsureIfenslaveAsync(CancellationToken ct)
    {
        if (File.Exists(P("/sbin/ifenslave")) || File.Exists(P("/usr/sbin/ifenslave")) || File.Exists(P("/etc/network/if-pre-up.d/ifenslave"))) return;
        var i = await runner.RunAsync("env", ["DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", "--no-install-recommends", "ifenslave"], ct: ct);
        if (i.ExitCode != 0)
            throw new OperationException(Msg.F("Link aggregation needs the ifenslave package, which could not be installed: {0}",
                (i.StdErr + "\n" + i.StdOut).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "apt-get"));
    }

    async Task RemoveBondAsync(string bond, string backend, NetSnapshot snap, CancellationToken ct)
    {
        var iface = snap.Interfaces.FirstOrDefault(i => i.Name == bond) ?? throw new OperationException(Msg.F("Network interface {0} not found.", bond));
        if (iface.Kind != "bond") throw new OperationException(Msg.F("This aggregation was not created by UrsaNAS."));
        if (iface.Master is not null) throw new OperationException(Msg.F("This aggregation is a port of an Open vSwitch bridge: remove that bridge first."));
        var pre = new Dictionary<string, string?>();
        List<string> members = [];
        if (backend == "netplan")
        {
            var ours = P("/etc/netplan/" + NetplanConfig.BondFileName(bond));
            if (File.Exists(ours))
            {
                pre[ours] = null;
                if (Regex.Match(File.ReadAllText(ours), @"interfaces: \[([^\]]+)\]") is { Success: true } m)
                    members = m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }
        }
        else
        {
            var main = P("/etc/network/interfaces");
            var dir = P("/etc/network/interfaces.d");
            var files = new List<string> { main };
            if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir).Where(f => Regex.IsMatch(Path.GetFileName(f), "^[A-Za-z0-9_-]+$")));
            foreach (var f in files.Where(File.Exists))
            {
                var text = File.ReadAllText(f);
                var found = IfupdownConfig.FindBlocks(text, "bond").Where(b => b.Name == bond).Select(b => b.Extra).FirstOrDefault();
                if (found is null) continue;
                members = found.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                var rest = IfupdownConfig.RemoveBlock(text, "bond", bond);
                pre[f] = f != main && rest.Trim().Length == 0 ? null : rest;
            }
        }
        if (members.Count == 0 || members.Any(m => !NetRules.IsIfaceName(m))) throw new OperationException(Msg.F("This aggregation was not created by UrsaNAS."));

        var port = members[0];   // la première carte retrouve l'adressage; les autres redeviennent libres, sans configuration
        var req = Addressing(iface, port);
        if (NetRules.Validate(req) is { } invalid) throw new OperationException(invalid);
        if (backend == "ifupdown" && ((req.Ipv4.Mode == "auto" && req.Ipv4.DnsMode == "manual") || (req.Ipv6.Mode == "auto" && req.Ipv6.DnsMode == "manual")))
            throw new OperationException(Msg.F("Manual DNS servers need a manual address on this system."));
        var changes = backend == "netplan" ? PlanNetplan(req, "ethernet", pre) : PlanIfupdown(req, pre);
        var state = new PendingState(port, backend, DateTime.UtcNow.AddSeconds(ConfirmSeconds), Backups(changes), RestoreBridge: bond);
        await CommitLockedAsync(state, changes,
            backend == "ifupdown" ? () => runner.RunAsync("ifdown", ["--force", bond], ct: ct) : null,
            async () => { await DelLinkAsync(bond, ct); await ApplyAsync(backend, port, null, ct); },
            () => VerifyAsync(t => CheckOnceAsync(req, t), ct));
        log.LogInformation("Link aggregation {Bond} removed; {Port} carries the addressing again", bond, port);
    }

    Dictionary<string, string?> PlanBondNetplan(NetEditRequest bondReq, List<string> members, string mode, string? mac)
    {
        var dir = P("/etc/netplan");
        var ours = Path.Combine(dir, NetplanConfig.BondFileName(bondReq.Iface));
        var changes = new Dictionary<string, string?>();
        foreach (var file in Directory.GetFiles(dir, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (file == ours) continue;
            var text = File.ReadAllText(file);
            var stripped = text;
            foreach (var m in members)
            {
                if (NetplanConfig.SectionOf(stripped, m) is not { } s) continue;
                if (s != "ethernets") throw new OperationException(Msg.F("This interface cannot be edited here."));
                stripped = NetplanConfig.StripIface(stripped, m);
            }
            if (stripped != Lf(text)) changes[file] = stripped;
        }
        changes[ours] = NetplanConfig.GenerateBond(bondReq, members, mode, mac);
        GuardCloudInit(changes);
        return changes;
    }

    Dictionary<string, string?> PlanBondIfupdown(NetEditRequest bondReq, List<string> members, string mode, string? mac)
    {
        var main = P("/etc/network/interfaces");
        var dir = P("/etc/network/interfaces.d");
        var ours = Path.Combine(dir, $"ursanas-bond-{bondReq.Iface}");
        var files = new List<string> { main };
        if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir).Where(f => f != ours && Regex.IsMatch(Path.GetFileName(f), "^[A-Za-z0-9_-]+$")).OrderBy(f => f, StringComparer.Ordinal));
        var changes = new Dictionary<string, string?>();
        foreach (var file in files.Where(File.Exists))
        {
            var text = File.ReadAllText(file);
            var stripped = text;
            foreach (var m in members) stripped = IfupdownConfig.Strip(stripped, m);
            if (stripped != Lf(text)) changes[file] = stripped;
        }
        var block = IfupdownConfig.GenerateBond(bondReq, members, mode, mac);
        var mainText = changes.TryGetValue(main, out var mt) && mt is not null ? mt : File.ReadAllText(main);
        if (SourcesDir().IsMatch(mainText)) changes[ours] = block;
        else changes[main] = mainText.TrimEnd('\n') + "\n\n" + block;
        return changes;
    }

    // ---------------------------------------------------------------- plans : chemin → nouveau contenu (null = supprimer)

    Dictionary<string, string?> PlanNetplan(NetEditRequest req, string kind, Dictionary<string, string?>? pre = null)
    {
        var dir = P("/etc/netplan");
        var ours = Path.Combine(dir, $"90-ursanas-{req.Iface}.yaml");
        var changes = pre is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(pre);
        string? section = null;
        foreach (var file in Directory.GetFiles(dir, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (file == ours) continue;
            string text;
            if (changes.TryGetValue(file, out var preText)) { if (preText is null) continue; text = preText; }   // déjà modifié (ou retiré) par l'opération en cours
            else text = File.ReadAllText(file);
            if (NetplanConfig.SectionOf(text, req.Iface) is not { } s) continue;
            if (s is not ("ethernets" or "bridges" or "bonds")) throw new OperationException(Msg.F("This interface cannot be edited here."));
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

    Dictionary<string, string?> PlanIfupdown(NetEditRequest req, Dictionary<string, string?>? pre = null, string v4Options = "")
    {
        var main = P("/etc/network/interfaces");
        var dir = P("/etc/network/interfaces.d");
        var ours = Path.Combine(dir, $"ursanas-{req.Iface}");
        var files = new List<string> { main };
        if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir).Where(f => f != ours && Regex.IsMatch(Path.GetFileName(f), "^[A-Za-z0-9_-]+$")).OrderBy(f => f, StringComparer.Ordinal));

        var changes = pre is null ? new Dictionary<string, string?>() : new Dictionary<string, string?>(pre);
        var hasAuto = false;
        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;
            string text;
            if (changes.TryGetValue(file, out var preText)) { if (preText is null) continue; text = preText; }
            else text = File.ReadAllText(file);
            hasAuto |= IfupdownConfig.HasAuto(text, req.Iface);
            var stripped = IfupdownConfig.Strip(text, req.Iface);
            if (stripped != Lf(text)) changes[file] = stripped;
        }
        var block = IfupdownConfig.Generate(req, addAuto: !hasAuto, v4Options: v4Options);
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
