using System.Text;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>Génère le fichier de configuration Samba des partages gérés par UrsaNAS.</summary>
public static class ShareConf
{
    /// <param name="datasetPaths">Point de montage de chaque jeu de données ZFS utilisé par un partage.</param>
    public static string Render(IReadOnlyList<ShareDef> shares, string shareRoot, IReadOnlyDictionary<string, string>? datasetPaths = null)
    {
        var sb = new StringBuilder();
        sb.Append("; Généré par UrsaNAS. Ne pas modifier à la main : ce fichier est réécrit à chaque changement.\n");
        foreach (var s in shares)
        {
            sb.Append('\n').Append('[').Append(s.Name).Append("]\n");
            if (!string.IsNullOrWhiteSpace(s.Comment)) sb.Append("   comment = ").Append(s.Comment.Trim().Replace("%", "%%")).Append('\n');
            sb.Append("   path = ").Append(s.Disk ?? (s.Dataset is not null && datasetPaths is not null && datasetPaths.TryGetValue(s.Dataset, out var p) ? p : PathFor(shareRoot, s.Name))).Append('\n'); // un disque monté (s.Disk) prime
            sb.Append("   read only = ").Append(s.ReadOnly && !s.Acl ? "yes" : "no").Append('\n'); // en mode permissions, les règles décident
            sb.Append("   guest ok = no\n");
            sb.Append("   browseable = yes\n");
            if (s.Acl)
            {
                // Les permissions fines vivent dans les ACL NT : pas de liste d'utilisateurs, le noyau garde sa base 2770 root:nasusers.
                sb.Append("   vfs objects = acl_xattr\n   acl_xattr:ignore system acls = yes\n   map acl inherit = yes\n");
                sb.Append("   admin users = ").Append(AclService.ServiceUser).Append('\n');
                sb.Append("   force group = ").Append(UserService.Group).Append("\n   force create mode = 0660\n   force directory mode = 2770\n");
            }
            else sb.Append("   valid users = ").Append(string.Join(' ', s.ValidUsers)).Append('\n');
            sb.Append("   create mask = 0660\n");
            sb.Append("   directory mask = 2770\n");
        }
        return sb.ToString();
    }

    public static string PathFor(string shareRoot, string name) => shareRoot.TrimEnd('/') + "/" + name;

    /// <summary>
    /// Place la ligne <c>include</c> à la FIN de smb.conf. Samba traite un include à l'endroit où il apparaît :
    /// placé dans [global], les sections du fichier inclus couperaient [global] en deux et les réglages qui suivent
    /// tomberaient dans le dernier partage. Une ligne mal placée (version précédente) est déplacée.
    /// Renvoie le texte inchangé quand l'include est déjà la dernière ligne.
    /// </summary>
    public static string EnsureInclude(string smbConf, string includePath)
    {
        var line = $"include = {includePath}";
        var lines = smbConf.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);

        if (lines.Count > 0 && lines[^1].Trim() == line && lines.Count(l => l.Contains(includePath, StringComparison.Ordinal)) == 1)
            return smbConf; // déjà en place, au bon endroit

        lines.RemoveAll(l => l.Contains(includePath, StringComparison.Ordinal));
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.Add("");
        lines.Add("; Partages gérés par UrsaNAS (doit rester la dernière ligne)");
        lines.Add(line);
        return string.Join('\n', lines) + "\n";
    }
}

/// <summary>
/// Applique la liste complète des partages : crée les dossiers, écrit <c>ursanas.conf</c>, le valide avec
/// <c>testparm</c> (retour arrière si Samba le refuse) puis recharge Samba. Les dossiers des partages supprimés
/// ne sont jamais effacés : les données restent sur le disque.
/// </summary>
public sealed class ShareService(ICommandRunner run, UserService users, IConfiguration cfg, AclService? acl = null, ProtocolService? protocols = null, IDiskProvider? disks = null)
{
    string ConfigDir => cfg["Samba:ConfigDir"] ?? "/etc/samba";
    string ShareRoot => cfg["Samba:ShareRoot"] ?? "/srv/nas";
    string ConfPath => Path.Combine(ConfigDir, "ursanas.conf");
    string MainConfPath => Path.Combine(ConfigDir, "smb.conf");

    public async Task ApplyAsync(IReadOnlyList<ShareDef> shares, FtpSettings? ftp = null, CancellationToken ct = default)
    {
        var known = (await users.ListAsync(ct)).Select(u => u.Name).ToHashSet();
        if (Rules.ValidateShares(shares, known) is { } error) throw new OperationException(error);

        var datasetPaths = await ResolveDatasetsAsync(shares, ct); // avant toute écriture : un jeu absent ne laisse aucune trace
        await CheckDisksAsync(shares, ct);
        if (Rules.ValidateFtpSettings(ftp) is { } ftpError) throw new OperationException(ftpError);
        protocols?.EnsureAvailable(shares); // idem pour NFS/FTP non installés

        await EnsureIncludeAsync(ct);
        if (acl is not null && shares.Any(s => s.Acl)) await acl.EnsureServiceAccountAsync(ct); // le compte doit exister avant que Samba lise « admin users »

        // Le dossier racine doit être traversable par tous les utilisateurs : l'unité systemd de l'agent a un UMask 0007,
        // sans cette étape il serait en 0770 et personne d'autre que root ne pourrait atteindre un partage.
        Directory.CreateDirectory(ShareRoot);
        Check(await run.RunAsync("chmod", ["0755", ShareRoot], ct: ct), "chmod");

        foreach (var s in shares)
        {
            string dir;
            if (s.Disk is not null) continue; // le disque garde ses droits (réglés au montage) ; on ne change rien à sa racine
            if (s.Dataset is not null) dir = datasetPaths[s.Dataset]; // déjà monté par ZFS
            else
            {
                dir = ShareConf.PathFor(ShareRoot, s.Name);
                Directory.CreateDirectory(dir);
            }
            Check(await run.RunAsync("chgrp", [UserService.Group, dir], ct: ct), "chgrp");
            Check(await run.RunAsync("chmod", ["2770", dir], ct: ct), "chmod");
        }

        var previous = File.Exists(ConfPath) ? await File.ReadAllTextAsync(ConfPath, ct) : null;
        await File.WriteAllTextAsync(ConfPath, ShareConf.Render(shares, ShareRoot, datasetPaths), ct);

        var test = await run.RunAsync("testparm", ["-s", MainConfPath], ct: ct);
        if (test.ExitCode != 0)
        {
            if (previous is null) File.Delete(ConfPath); else await File.WriteAllTextAsync(ConfPath, previous, ct);
            throw new OperationException(Msg.F("Samba rejects the configuration: {0}", (string.IsNullOrWhiteSpace(test.StdErr) ? test.StdOut : test.StdErr).Trim()));
        }

        Check(await run.RunAsync("smbcontrol", ["all", "reload-config"], ct: ct), "Reloading Samba");

        if (protocols is not null)
        {
            var paths = shares.ToDictionary(s => s.Name, s => s.Disk ?? (s.Dataset is not null ? datasetPaths[s.Dataset] : ShareConf.PathFor(ShareRoot, s.Name)), StringComparer.OrdinalIgnoreCase);
            await protocols.ApplyAsync(shares, paths, ftp, ct);
        }
    }

    /// <summary>Un disque partagé doit être réellement monté à l'endroit indiqué (sinon on partagerait le dossier vide du disque système).</summary>
    async Task CheckDisksAsync(IReadOnlyList<ShareDef> shares, CancellationToken ct)
    {
        if (disks is null || !shares.Any(s => s.Disk is not null)) return;
        var mounted = (await disks.ListAsync(ct)).SelectMany(d => d.MountPoints).ToHashSet();
        foreach (var s in shares.Where(s => s.Disk is not null))
            if (!mounted.Contains(s.Disk!)) throw new OperationException(Msg.F("The disk “{0}” is not mounted.", s.Disk));
    }

    /// <summary>Trouve le point de montage de chaque jeu de données choisi ; refuse un jeu inconnu ou non monté.</summary>
    async Task<Dictionary<string, string>> ResolveDatasetsAsync(IReadOnlyList<ShareDef> shares, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!shares.Any(s => s.Dataset is not null)) return result;

        var list = await run.RunAsync("zfs", ZfsParser.DatasetListArgs, ct: ct);
        Check(list, "Listing datasets");
        var known = ZfsParser.ParseDatasets(list.StdOut).ToDictionary(d => d.Name, d => d.Mountpoint);
        foreach (var s in shares.Where(s => s.Dataset is not null))
        {
            if (!known.TryGetValue(s.Dataset!, out var mp)) throw new OperationException(Msg.F("Dataset “{0}” does not exist.", s.Dataset));
            if (!Path.IsPathRooted(mp)) throw new OperationException(Msg.F("Dataset “{0}” has no mount point.", s.Dataset));
            if (!Directory.Exists(mp)) throw new OperationException(Msg.F("Dataset “{0}” is not mounted ({1}).", s.Dataset, mp));
            result[s.Dataset!] = mp;
        }
        return result;
    }

    async Task EnsureIncludeAsync(CancellationToken ct)
    {
        var main = await File.ReadAllTextAsync(MainConfPath, ct);
        var updated = ShareConf.EnsureInclude(main, ConfPath);
        if (updated == main) return;

        var backup = MainConfPath + ".ursanas-bak";
        if (!File.Exists(backup)) File.Copy(MainConfPath, backup);
        if (!File.Exists(ConfPath)) await File.WriteAllTextAsync(ConfPath, "", ct); // l'include doit pointer vers un fichier existant
        await File.WriteAllTextAsync(MainConfPath, updated, ct);
    }

    static void Check(CommandResult r, string what)
    {
        if (r.ExitCode == 0) return;
        throw new OperationException(Msg.F("{0} failed ({1}): {2}", what, r.ExitCode, r.StdErr.Trim()));
    }
}
