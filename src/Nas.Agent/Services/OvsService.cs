using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Open vSwitch reste facultatif : rien n'est installé par défaut. Activer installe le paquet <c>openvswitch-switch</c> au besoin
/// et démarre son service; désactiver arrête le service (le paquet reste) et est refusé tant qu'un pont OVS existe.
/// </summary>
public class OvsService(ICommandRunner runner, IConfiguration cfg, ILogger<OvsService> log)
{
    const string Unit = "openvswitch-switch";
    bool Fake => cfg["URSANAS_FAKE"] == "1";
    bool simulated;   // état du mode simulé

    public async Task<OvsStatus> StatusAsync(CancellationToken ct = default)
    {
        if (Fake) return new OvsStatus(simulated, simulated, true, simulated ? "3.3.0" : null, []);
        var installed = await InstalledAsync(ct);
        if (!installed) return new OvsStatus(false, false, await CanInstallAsync(ct), null, []);
        var active = (await Try("systemctl", ["is-active", Unit], ct)).StdOut.Trim() == "active";
        string? version = null;
        IReadOnlyList<string> bridges = [];
        if (active)
        {
            var v = await Try("ovs-vsctl", ["--version"], ct);
            version = ParseVersion(v.StdOut);
            var b = await Try("ovs-vsctl", ["list-br"], ct);
            if (b.ExitCode == 0) bridges = ParseLines(b.StdOut);
        }
        return new OvsStatus(true, active, true, version, bridges);
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (Fake) { simulated = enabled; return; }
        var s = await StatusAsync(ct);
        if (enabled)
        {
            if (!s.Installed)
            {
                if (!s.CanInstall) throw new OperationException(Msg.F("Open vSwitch cannot be installed here: apt-get was not found."));
                log.LogInformation("Installing {Package}.", Unit);
                var i = await runner.RunAsync("env", ["DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", "--no-install-recommends", Unit], ct: ct);
                if (i.ExitCode != 0) throw new OperationException(Msg.F("Open vSwitch could not be installed: {0}", Last(i)));
            }
            var e = await runner.RunAsync("systemctl", ["enable", "--now", Unit], ct: ct);
            if (e.ExitCode != 0) throw new OperationException(Msg.F("Open vSwitch could not be started: {0}", Last(e)));
            if (!(await StatusAsync(ct)).Active) throw new OperationException(Msg.F("Open vSwitch could not be started: {0}", Last(e)));
        }
        else
        {
            if (!s.Installed) return;
            if (s.Bridges.Count > 0) throw new OperationException(Msg.F("Open vSwitch is still used by the bridges {0}: delete them first.", string.Join(", ", s.Bridges)));
            var d = await runner.RunAsync("systemctl", ["disable", "--now", Unit], ct: ct);
            if (d.ExitCode != 0) throw new OperationException(Msg.F("Open vSwitch could not be stopped: {0}", Last(d)));
        }
    }

    async Task<bool> InstalledAsync(CancellationToken ct)
        => (await Try("dpkg-query", ["-W", "-f=${Status}", Unit], ct)).StdOut.Contains("install ok installed");

    async Task<bool> CanInstallAsync(CancellationToken ct) => (await Try("apt-get", ["--version"], ct)).ExitCode == 0;

    // Un programme absent (Win32Exception) équivaut à un échec, pas à une erreur de l'agent.
    async Task<CommandResult> Try(string file, string[] args, CancellationToken ct)
    {
        try { return await runner.RunAsync(file, args, ct: ct); }
        catch (System.ComponentModel.Win32Exception) { return new CommandResult(127, "", ""); }
    }

    static string Last(CommandResult r)
        => (r.StdErr + "\n" + r.StdOut).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? $"exit {r.ExitCode}";

    public static IReadOnlyList<string> ParseLines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>« ovs-vsctl (Open vSwitch) 3.3.0 » → « 3.3.0 ».</summary>
    public static string? ParseVersion(string s)
        => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } l && l.Contains("(Open vSwitch)") ? l[(l.IndexOf(')') + 1)..].Trim() : null;
}
