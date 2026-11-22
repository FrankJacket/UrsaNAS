using System.Text.RegularExpressions;

namespace Nas.Contracts;

/// <summary>Un utilisateur du NAS (compte système sans shell + compte Samba).</summary>
public sealed record NasUser(string Name, bool Admin = false);

public sealed record CreateUserRequest(string Name, string Password, bool Admin = false);

/// <summary>Vérifie un mot de passe contre le compte Samba (utilisé par la connexion à l'interface).</summary>
public sealed record VerifyUserRequest(string Name, string Password);

/// <summary>État d'un compte pour la session web : <c>Stamp</c> change quand le mot de passe change (les sessions ouvertes deviennent alors invalides).</summary>
public sealed record UserSession(bool Valid, bool Admin, string Stamp);

public sealed record SetAdminRequest(bool Admin);

public sealed record SetPasswordRequest(string Password);

/// <summary>
/// Un partage SMB. Le chemin n'est jamais fourni par l'appelant : sans <c>Dataset</c>, le dossier est
/// <c>/srv/nas/&lt;Name&gt;</c> ; avec <c>Dataset</c> (ex. <c>tank/photos</c>), c'est le point de montage de ce jeu de données ZFS.
/// </summary>
public sealed record ShareDef(string Name, string Comment, bool ReadOnly, IReadOnlyList<string> ValidUsers, string? Dataset = null, bool Acl = false, NfsExport? Nfs = null, FtpAccess? Ftp = null, string? Disk = null);

/// <summary>Export NFS d'un partage : clients autorisés (IP, réseau CIDR ou nom d'hôte). Les permissions avancées (ACL) ne s'appliquent pas par NFS.</summary>
public sealed record NfsExport(IReadOnlyList<string> Clients, bool ReadOnly = false);

/// <summary>Accès FTP à un partage : utilisateurs autorisés. Les permissions avancées (ACL) ne s'appliquent pas par FTP : cette liste décide.</summary>
public sealed record FtpAccess(IReadOnlyList<string> Users, bool ReadOnly = false);

/// <summary>Réglages globaux du serveur FTP.</summary>
public sealed record FtpSettings(bool Tls = false, int PassiveMin = 40000, int PassiveMax = 40100);

/// <summary>Un service réseau géré (smb, nfs, ftp).</summary>
public sealed record ServiceInfo(string Name, bool Installed, bool Active, bool Enabled, int Port);

public sealed record SetServiceRequest(bool Enabled);

/// <summary>L'ensemble complet des partages à appliquer (remplace l'existant), avec les réglages FTP courants.</summary>
public sealed record ApplySharesRequest(IReadOnlyList<ShareDef> Shares, FtpSettings? Ftp = null);

/// <summary>Règles de validation communes : l'interface les utilise pour guider, l'agent pour décider.</summary>
public static partial class Rules
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;

    static readonly HashSet<string> ReservedShares = new(StringComparer.OrdinalIgnoreCase)
        { "global", "homes", "printers", "print$", "ipc$", "admin$" };

    static readonly HashSet<string> ReservedUsers = new(StringComparer.Ordinal)
        { "root", "nobody", "daemon", "bin", "sys", "ursanas" };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,31}$")]
    private static partial Regex ShareNameRegex();

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$")]
    private static partial Regex UserNameRegex();

    public static string? ValidateShareName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !ShareNameRegex().IsMatch(name))
            return Msg.F("Invalid share name: 1 to 32 characters (letters, digits, _ and -), no spaces.");
        if (ReservedShares.Contains(name)) return Msg.F("“{0}” is a reserved name.", name);
        return null;
    }

    public static string? ValidateUserName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !UserNameRegex().IsMatch(name))
            return Msg.F("Invalid user name: 1 to 32 lowercase characters (letters, digits, _ and -), no spaces.");
        if (ReservedUsers.Contains(name)) return Msg.F("“{0}” is a reserved name.", name);
        return null;
    }

    public static string? ValidatePassword(string? password)
    {
        if (password is null || password.Length < MinPasswordLength)
            return Msg.F("The password must be at least {0} characters long.", MinPasswordLength);
        if (password.Length > MaxPasswordLength) return Msg.F("The password is limited to {0} characters.", MaxPasswordLength);
        if (password.Any(char.IsControl)) return Msg.F("The password cannot contain control characters.");
        return null;
    }

    /// <summary>Le commentaire finit dans smb.conf : une seule ligne, sans caractère de contrôle.</summary>
    public static string? ValidateComment(string? comment)
    {
        if (comment is null) return null;
        if (comment.Length > 200) return Msg.F("The comment is limited to 200 characters.");
        if (comment.Any(char.IsControl)) return Msg.F("The comment cannot contain control characters.");
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9-]{0,62})(\\.[A-Za-z0-9]([A-Za-z0-9-]{0,62}))*$")]
    private static partial Regex HostNameRegex();

    /// <summary>
    /// Un client NFS : adresse IP, réseau CIDR (192.168.1.0/24) ou nom d'hôte. Le texte finit dans /etc/exports :
    /// rien d'autre (espaces, parenthèses, virgules, jokers) n'est accepté.
    /// </summary>
    public static string? ValidateNfsClient(string? client)
    {
        var bad = Msg.F("Invalid NFS client “{0}”: use an IP address, a network such as 192.168.1.0/24 or a host name.", client);
        if (string.IsNullOrWhiteSpace(client) || client.Length > 253 || client != client.Trim()) return bad;
        var slash = client.IndexOf('/');
        var host = slash < 0 ? client : client[..slash];
        var dotted = host.Count(ch => ch == '.') == 3 && host.All(ch => char.IsAsciiDigit(ch) || ch == '.'); // « 1 » ou « 127.1 » ne sont pas des adresses complètes
        if ((dotted || host.Contains(':')) && System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (host.Contains('%')) return bad; // identifiant de zone IPv6
            if (slash < 0) return null;
            var max = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            return int.TryParse(client[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bits) && bits >= 0 && bits <= max ? null : bad;
        }
        return slash < 0 && HostNameRegex().IsMatch(client) && !client.All(ch => char.IsAsciiDigit(ch) || ch == '.') ? null : bad;
    }

    public static string? ValidateNfs(NfsExport? nfs, string share)
    {
        if (nfs is null) return null;
        if (nfs.Clients is null || nfs.Clients.Count == 0) return Msg.F("NFS on share “{0}” needs at least one client.", share);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in nfs.Clients)
        {
            if (ValidateNfsClient(c) is { } e) return e;
            if (!seen.Add(c)) return Msg.F("NFS client “{0}” is duplicated.", c);
        }
        return null;
    }

    public static string? ValidateFtp(FtpAccess? ftp, string share, IReadOnlySet<string> knownUsers)
    {
        if (ftp is null) return null;
        if (ftp.Users is null || ftp.Users.Count == 0) return Msg.F("FTP on share “{0}” needs at least one user.", share);
        foreach (var u in ftp.Users)
            if (!knownUsers.Contains(u)) return Msg.F("Share “{0}” references the unknown user “{1}”.", share, u);
        return null;
    }

    public static string? ValidateFtpSettings(FtpSettings? s)
    {
        if (s is null) return null;
        if (s.PassiveMin < 1024 || s.PassiveMax > 65535 || s.PassiveMin > s.PassiveMax)
            return Msg.F("The passive port range must be between 1024 and 65535, with the first port not above the last.");
        if (s.PassiveMax - s.PassiveMin > 1000) return Msg.F("The passive port range is limited to 1000 ports.");
        return null;
    }

    static readonly string[] DiskRoots = ["/mnt/", "/media/", "/run/media/"];

    /// <summary>Dossier de montage d'un disque utilisé comme partage : sous /mnt, /media… comme pour les sauvegardes.</summary>
    public static string? ValidateShareDisk(string? dir) =>
        dir is not null && DiskRoots.Any(r => dir.StartsWith(r, StringComparison.Ordinal)) && dir.Length < 256
            && !dir.Split('/').Any(p => p is ".." or ".") && !dir.Any(char.IsControl) && !dir.EndsWith('/') && !dir.Contains("//")
            ? null : Msg.F("Invalid disk folder: it must be a disk mounted under /mnt or /media.");

    /// <summary>Valide un jeu de partages complet : noms, doublons, utilisateurs connus.</summary>
    public static string? ValidateShares(IReadOnlyList<ShareDef> shares, IReadOnlySet<string> knownUsers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var datasets = new HashSet<string>(StringComparer.Ordinal);
        var disks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in shares)
        {
            if (ValidateShareName(s.Name) is { } e1) return e1;
            if (ValidateComment(s.Comment) is { } e2) return e2;
            if (!seen.Add(s.Name)) return Msg.F("Share “{0}” is duplicated.", s.Name);
            if (s.Dataset is not null)
            {
                if (ValidateDatasetName(s.Dataset) is { } e3) return e3;
                if (!datasets.Add(s.Dataset)) return Msg.F("The dataset “{0}” is already used by another share.", s.Dataset);
            }
            if (s.Disk is not null)
            {
                if (s.Dataset is not null) return Msg.F("A share cannot use both a dataset and a disk.");
                if (ValidateShareDisk(s.Disk) is { } e6) return e6;
                if (!disks.Add(s.Disk)) return Msg.F("The disk “{0}” is already used by another share.", s.Disk);
            }
            if (ValidateNfs(s.Nfs, s.Name) is { } e4) return e4;
            if (ValidateFtp(s.Ftp, s.Name, knownUsers) is { } e5) return e5;
            if (s.Acl) continue; // en mode permissions, ce sont les règles du dossier qui décident, pas la liste d'utilisateurs
            if (s.ValidUsers is null || s.ValidUsers.Count == 0) return Msg.F("Share “{0}” must have at least one user.", s.Name);
            foreach (var u in s.ValidUsers)
                if (!knownUsers.Contains(u)) return Msg.F("Share “{0}” references the unknown user “{1}”.", s.Name, u);
        }
        return null;
    }
}
