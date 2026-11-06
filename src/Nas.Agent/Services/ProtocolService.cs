using System.Text;
using System.Text.RegularExpressions;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>Génération (sans effet de bord, donc testable) des fichiers de configuration NFS et FTP.</summary>
public static partial class ProtocolConf
{
    /// <summary>Compte « anonyme » des clients NFS : tous les clients sont ramenés à lui, avec le groupe nasusers.</summary>
    public const int AnonUid = 65534;

    /// <summary>Fichier /etc/exports : une ligne par partage exporté, tous les clients ramenés au groupe nasusers (all_squash).</summary>
    public static string RenderExports(IReadOnlyList<ShareDef> shares, IReadOnlyDictionary<string, string> sharePaths, int anonGid)
    {
        var sb = new StringBuilder("# Généré par UrsaNAS. Ne pas modifier à la main : ce fichier est réécrit à chaque changement.\n");
        foreach (var s in shares)
        {
            if (s.Nfs is not { } nfs) continue;
            var opts = $"{(nfs.ReadOnly ? "ro" : "rw")},sync,no_subtree_check,all_squash,anonuid={AnonUid},anongid={anonGid}";
            sb.Append(EscapePath(sharePaths[s.Name])).Append(' ')
              .Append(string.Join(' ', nfs.Clients.Select(c => $"{ExportClient(c)}({opts})"))).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Les espaces et antislash d'un chemin s'écrivent en octal dans /etc/exports.</summary>
    public static string EscapePath(string path) => path.Replace("\\", "\\134").Replace(" ", "\\040").Replace("\t", "\\011");

    /// <summary>Une adresse IPv6 s'écrit entre crochets dans /etc/exports (avec son préfixe éventuel à l'extérieur).</summary>
    public static string ExportClient(string client)
    {
        if (!client.Contains(':')) return client;
        var slash = client.IndexOf('/');
        return slash < 0 ? $"[{client}]" : $"[{client[..slash]}]{client[slash..]}";
    }

    public static string RenderFtpConf(FtpSettings s, string certFile, string userListFile)
    {
        var sb = new StringBuilder();
        sb.Append("# Généré par UrsaNAS. Ne pas modifier à la main : ce fichier est réécrit à chaque changement.\n");
        sb.Append("listen=YES\nlisten_ipv6=NO\nanonymous_enable=NO\nlocal_enable=YES\nwrite_enable=YES\n");
        sb.Append("local_umask=007\nuse_localtime=YES\nxferlog_enable=YES\nconnect_from_port_20=YES\n");
        sb.Append("chroot_local_user=YES\nsecure_chroot_dir=/var/run/vsftpd/empty\npam_service_name=ursanas-ftp\n");
        // Seuls les utilisateurs de la liste peuvent se connecter ; chacun voit uniquement ses propres partages (son dossier personnel).
        sb.Append("userlist_enable=YES\nuserlist_deny=NO\nuserlist_file=").Append(userListFile).Append('\n');
        sb.Append("pasv_enable=YES\npasv_min_port=").Append(s.PassiveMin).Append("\npasv_max_port=").Append(s.PassiveMax).Append('\n');
        if (s.Tls)
        {
            sb.Append("ssl_enable=YES\nrsa_cert_file=").Append(certFile).Append("\nrsa_private_key_file=").Append(certFile).Append('\n');
            sb.Append("force_local_logins_ssl=YES\nforce_local_data_ssl=YES\nssl_tlsv1=NO\nssl_sslv2=NO\nssl_sslv3=NO\nssl_tlsv1_2=YES\nrequire_ssl_reuse=NO\n");
        }
        else sb.Append("ssl_enable=NO\n");
        return sb.ToString();
    }


    /// <summary>Les points de montage de /proc/self/mountinfo (5e champ, espaces écrits en octal).</summary>
    public static IEnumerable<string> ParseMountPoints(string mountInfo)
    {
        foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(' ');
            if (f.Length > 4) yield return OctalEscape().Replace(f[4], m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
        }
    }

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex OctalEscape();
}

/// <summary>Un montage « bind » qui expose un partage dans le dossier FTP d'un utilisateur.</summary>
public sealed record FtpBind(string Target, string Source, bool ReadOnly);

/// <summary>
/// NFS et FTP. NFS : un fichier d'exports (clients autorisés par partage). FTP : vsftpd avec les comptes du NAS ; chaque
/// utilisateur est enfermé (chroot) dans un dossier à lui où seuls ses partages sont montés (bind), en lecture seule si demandé.
/// Ni l'un ni l'autre n'applique les permissions avancées (ACL NT, propres à Samba) : la liste de clients / d'utilisateurs décide.
/// </summary>
public sealed class ProtocolService(ICommandRunner run, IConfiguration cfg)
{
    public static readonly IReadOnlyList<(string Name, string Unit, int Port)> Known =
        [("smb", "smbd", 445), ("nfs", "nfs-server", 2049), ("ftp", "vsftpd", 21)];

    string ExportsPath => cfg["Nfs:ExportsFile"] ?? "/etc/exports.d/ursanas.exports";
    string FtpConf => cfg["Ftp:ConfFile"] ?? "/etc/vsftpd.conf";
    string FtpRoot => (cfg["Ftp:Root"] ?? "/srv/ursanas-ftp").TrimEnd('/');
    string StateFile => cfg["Ftp:StateFile"] ?? "/var/lib/ursanas/ftp-binds.txt";
    string CertFile => cfg["Ftp:CertFile"] ?? "/etc/ursanas/ftp.pem";
    string MountInfo => cfg["Ftp:MountInfo"] ?? "/proc/self/mountinfo";
    string UserListFile => FtpConf + ".userlist";

    // « Installé » = le dossier ou fichier de configuration existe : évite aussi d'écrire dans /etc sur une machine de développement.
    bool NfsInstalled => Directory.Exists(Path.GetDirectoryName(ExportsPath));
    bool FtpInstalled => File.Exists(FtpConf);

    // ---- Services

    public async Task<IReadOnlyList<ServiceInfo>> ListServicesAsync(CancellationToken ct = default)
    {
        var list = new List<ServiceInfo>();
        foreach (var (name, unit, port) in Known)
        {
            var installed = (await run.RunAsync("systemctl", ["cat", unit + ".service"], ct: ct)).ExitCode == 0;
            var active = installed && (await run.RunAsync("systemctl", ["is-active", "--quiet", unit], ct: ct)).ExitCode == 0;
            var enabled = installed && (await run.RunAsync("systemctl", ["is-enabled", "--quiet", unit], ct: ct)).ExitCode == 0;
            list.Add(new ServiceInfo(name, installed, active, enabled, port));
        }
        return list;
    }

    public async Task SetServiceAsync(string name, bool enabled, CancellationToken ct = default)
    {
        var svc = Known.FirstOrDefault(k => k.Name == name);
        if (svc.Name is null) throw new OperationException(Msg.F("Unknown service “{0}”.", name));
        if ((await run.RunAsync("systemctl", ["cat", svc.Unit + ".service"], ct: ct)).ExitCode != 0)
            throw new OperationException(Msg.F("{0} is not installed on this server.", name.ToUpperInvariant()));
        Check(await run.RunAsync("systemctl", [enabled ? "enable" : "disable", "--now", svc.Unit], ct: ct), "Changing the service");
    }

    // ---- Partages

    /// <summary>Refuse avant toute écriture un partage NFS/FTP quand le service n'est pas installé.</summary>
    public void EnsureAvailable(IReadOnlyList<ShareDef> shares)
    {
        if (shares.Any(s => s.Nfs is not null) && !NfsInstalled)
            throw new OperationException(Msg.F("{0} is not installed on this server.", "NFS"));
        if (shares.Any(s => s.Ftp is not null) && !FtpInstalled)
            throw new OperationException(Msg.F("{0} is not installed on this server.", "FTP"));
    }

    public async Task ApplyAsync(IReadOnlyList<ShareDef> shares, IReadOnlyDictionary<string, string> sharePaths, FtpSettings? settings, CancellationToken ct = default)
    {
        if (Rules.ValidateFtpSettings(settings) is { } bad) throw new OperationException(bad);
        EnsureAvailable(shares);
        if (NfsInstalled) await ApplyNfsAsync(shares, sharePaths, ct);
        if (FtpInstalled) await ApplyFtpAsync(shares, sharePaths, settings ?? new FtpSettings(), ct);
    }

    async Task ApplyNfsAsync(IReadOnlyList<ShareDef> shares, IReadOnlyDictionary<string, string> sharePaths, CancellationToken ct)
    {
        var previous = File.Exists(ExportsPath) ? await File.ReadAllTextAsync(ExportsPath, ct) : null;
        var text = ProtocolConf.RenderExports(shares, sharePaths, await NasGidAsync(ct));
        if (previous == text) return;
        await File.WriteAllTextAsync(ExportsPath, text, ct);

        var r = await run.RunAsync("exportfs", ["-ra"], ct: ct);
        if (r.ExitCode != 0)
        {
            if (previous is null) File.Delete(ExportsPath); else await File.WriteAllTextAsync(ExportsPath, previous, ct);
            throw new OperationException(Msg.F("NFS rejects the configuration: {0}", (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim()));
        }
    }

    async Task<int> NasGidAsync(CancellationToken ct)
    {
        var g = await run.RunAsync("getent", ["group", UserService.Group], ct: ct);
        var f = g.StdOut.Trim().Split(':');
        return g.ExitCode == 0 && f.Length >= 3 && int.TryParse(f[2], out var gid) ? gid : AnonGid;
    }

    const int AnonGid = 65534;

    async Task ApplyFtpAsync(IReadOnlyList<ShareDef> shares, IReadOnlyDictionary<string, string> sharePaths, FtpSettings settings, CancellationToken ct)
    {
        var binds = new List<FtpBind>();
        var users = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var s in shares.Where(s => s.Ftp is not null))
            foreach (var u in s.Ftp!.Users)
            {
                users.Add(u);
                binds.Add(new FtpBind($"{FtpRoot}/{u}/{s.Name}", sharePaths[s.Name], s.Ftp.ReadOnly));
            }

        if (settings.Tls) await EnsureCertificateAsync(ct);

        // Sauvegarde unique de la configuration d'origine du paquet.
        if (!File.Exists(FtpConf + ".ursanas-bak") && !(await File.ReadAllTextAsync(FtpConf, ct)).StartsWith("# Généré par UrsaNAS", StringComparison.Ordinal))
            File.Copy(FtpConf, FtpConf + ".ursanas-bak");

        var conf = ProtocolConf.RenderFtpConf(settings, CertFile, UserListFile);
        var list = string.Concat(users.Select(u => u + "\n"));
        var changed = await WriteIfChangedAsync(FtpConf, conf, ct) | await WriteIfChangedAsync(UserListFile, list, ct);

        foreach (var u in users) await EnsureHomeAsync(u, ct);

        await ReconcileBindsAsync(binds, ct);

        // vsftpd ne relit pas sa configuration à chaud : un redémarrage n'est fait que si elle a changé.
        if (changed && (await run.RunAsync("systemctl", ["is-active", "--quiet", "vsftpd"], ct: ct)).ExitCode == 0)
            Check(await run.RunAsync("systemctl", ["restart", "vsftpd"], ct: ct), "Restarting the FTP server");
    }

    /// <summary>
    /// vsftpd enferme l'utilisateur dans son dossier personnel : il doit être celui où ses partages sont montés.
    /// Les comptes du NAS n'ont pas de vrai dossier personnel (créés avec --no-create-home), on peut donc le déplacer sans rien perdre.
    /// </summary>
    async Task EnsureHomeAsync(string user, CancellationToken ct)
    {
        var home = $"{FtpRoot}/{user}";
        var r = await run.RunAsync("getent", ["passwd", user], ct: ct);
        var f = r.StdOut.Trim().Split(':');
        if (r.ExitCode == 0 && f.Length >= 6 && f[5] == home) return;
        Check(await run.RunAsync("usermod", ["--home", home, user], ct: ct), "Changing the account");
    }

    static async Task<bool> WriteIfChangedAsync(string path, string text, CancellationToken ct)
    {
        if (File.Exists(path) && await File.ReadAllTextAsync(path, ct) == text) return false;
        await File.WriteAllTextAsync(path, text, ct);
        return true;
    }

    /// <summary>Certificat auto-signé pour FTPS (10 ans), créé une seule fois.</summary>
    async Task EnsureCertificateAsync(CancellationToken ct)
    {
        if (File.Exists(CertFile)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(CertFile)!);
        Check(await run.RunAsync("openssl", ["req", "-x509", "-nodes", "-newkey", "rsa:2048", "-days", "3650", "-subj", "/CN=ursanas",
            "-keyout", CertFile, "-out", CertFile], ct: ct), "Creating the FTP certificate");
        Check(await run.RunAsync("chmod", ["0600", CertFile], ct: ct), "chmod");
    }

    // ---- Montages « bind » (un dossier par utilisateur, un sous-dossier par partage)

    async Task ReconcileBindsAsync(IReadOnlyList<FtpBind> desired, CancellationToken ct)
    {
        var previous = ReadState();
        var mounted = IsMounted();
        var want = desired.ToDictionary(b => b.Target, StringComparer.Ordinal);

        foreach (var old in previous.Values.Where(b => !want.ContainsKey(b.Target)))
            if (mounted(old.Target)) await UnmountAsync(old.Target, ct);

        foreach (var b in desired)
        {
            if (previous.TryGetValue(b.Target, out var prev) && prev == b && mounted(b.Target)) continue;
            if (mounted(b.Target)) await UnmountAsync(b.Target, ct);
            await MountAsync(b, ct);
        }

        // Les dossiers vides des utilisateurs et partages retirés disparaissent ; jamais de suppression récursive : un montage encore actif effacerait les données.
        foreach (var old in previous.Values.Where(b => !want.ContainsKey(b.Target)))
        {
            TryRemoveEmpty(old.Target);
            TryRemoveEmpty(Path.GetDirectoryName(old.Target)!);
        }
        WriteState(desired);
    }

    /// <summary>Remonte après un redémarrage les partages FTP enregistrés (les montages ne survivent pas au redémarrage).</summary>
    public async Task RestoreBindsAsync(CancellationToken ct = default)
    {
        if (!FtpInstalled) return;
        var mounted = IsMounted();
        foreach (var b in ReadState().Values)
            if (!mounted(b.Target) && Directory.Exists(b.Source)) await MountAsync(b, ct);
    }

    async Task MountAsync(FtpBind b, CancellationToken ct)
    {
        foreach (var dir in new[] { FtpRoot, Path.GetDirectoryName(b.Target)!, b.Target })
        {
            Directory.CreateDirectory(dir);
            Check(await run.RunAsync("chmod", ["0755", dir], ct: ct), "chmod"); // l'agent tourne en UMask 0007 : sinon l'utilisateur ne peut pas entrer
        }
        Check(await run.RunAsync("mount", ["--bind", b.Source, b.Target], ct: ct), "Mounting the FTP share");
        if (b.ReadOnly) Check(await run.RunAsync("mount", ["-o", "remount,bind,ro", b.Target], ct: ct), "Mounting the FTP share");
    }

    // Démontage « paresseux » : une session FTP encore ouverte dans le dossier ne bloque pas le retrait.
    async Task UnmountAsync(string target, CancellationToken ct) =>
        Check(await run.RunAsync("umount", ["-l", target], ct: ct), "Unmounting the FTP share");

    Func<string, bool> IsMounted()
    {
        var points = File.Exists(MountInfo)
            ? ProtocolConf.ParseMountPoints(File.ReadAllText(MountInfo)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        return points.Contains;
    }

    Dictionary<string, FtpBind> ReadState()
    {
        var d = new Dictionary<string, FtpBind>(StringComparer.Ordinal);
        if (!File.Exists(StateFile)) return d;
        foreach (var line in File.ReadAllLines(StateFile))
        {
            var f = line.Split('\t');
            if (f.Length == 3) d[f[0]] = new FtpBind(f[0], f[1], f[2] == "ro");
        }
        return d;
    }

    void WriteState(IEnumerable<FtpBind> binds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllLines(StateFile, binds.Select(b => $"{b.Target}\t{b.Source}\t{(b.ReadOnly ? "ro" : "rw")}"));
    }

    static void TryRemoveEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void Check(CommandResult r, string what)
    {
        if (r.ExitCode == 0) return;
        var detail = (string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim();
        throw new OperationException(detail.Length > 0 ? Msg.F("{0} failed ({1}): {2}", what, r.ExitCode, detail) : Msg.F("{0} failed ({1})", what, r.ExitCode));
    }
}
