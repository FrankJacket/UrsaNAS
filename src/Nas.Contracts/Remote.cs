using System.Text.RegularExpressions;

namespace Nas.Contracts;

/// <summary>
/// Une connexion à un stockage distant (SMB ou NFS), montée en permanence sous le dossier des stockages distants.
/// <c>Path</c> : nom du partage (et sous-dossier éventuel) en SMB, chemin exporté en NFS.
/// </summary>
public sealed record RemoteStorage(string Name, string Type, string Host, string Path, string? User, string? Domain, string? Version, bool ReadOnly,
    string MountPoint, bool Mounted, long TotalBytes, long FreeBytes);

/// <summary>Ce que l'utilisateur saisit. <c>User</c> vide en SMB : accès invité. <c>Version</c> vide : négociée automatiquement.</summary>
public sealed record AddRemoteRequest(string Name, string Type, string Host, string Path, string? User, string? Password, string? Domain, string? Version, bool ReadOnly);

/// <summary>Types de stockage distant que le NAS sait monter (selon les programmes installés).</summary>
public sealed record RemoteTypes(IReadOnlyList<string> Available);

public static partial class Rules
{
    public static readonly IReadOnlyList<string> RemoteKinds = ["smb", "nfs"];
    public static readonly IReadOnlyList<string> SmbVersions = ["3.1.1", "3.0", "2.1", "2.0", "1.0"];
    public static readonly IReadOnlyList<string> NfsVersions = ["4.2", "4.1", "4.0", "3"];

    // Jamais de « - » en tête : en NFS le serveur ouvre l'argument de mount (« -o:/chemin »), qui serait pris pour une option.
    [GeneratedRegex(@"^[A-Za-z0-9:][A-Za-z0-9._:\-]{0,252}$")]
    private static partial Regex RemoteHostRegex();

    [GeneratedRegex(@"^[^\x00-\x1f\\,:""'/]{1,64}$")]
    private static partial Regex RemoteUserRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,64}$")]
    private static partial Regex RemoteDomainRegex();

    /// <summary>Contrôle complet d'une connexion avant tout appel système : rien de ce texte ne doit pouvoir sortir d'un champ de /etc/fstab ni d'une option de montage.</summary>
    public static string? ValidateRemote(AddRemoteRequest r)
    {
        if (ValidateMountName(r.Name) is { } n) return n;
        if (!RemoteKinds.Contains(r.Type)) return Msg.F("Unknown type of remote storage.");
        if (string.IsNullOrEmpty(r.Host) || !RemoteHostRegex().IsMatch(r.Host)) return Msg.F("Invalid server name or address.");
        var path = r.Path ?? "";
        if (path.Length is 0 or > 400 || path.Any(char.IsControl) || path.Contains('\\') || path.Contains(',') || path.Contains('"')
            || path.Split('/').Contains("..") || path.Contains("//"))
            return Msg.F("Invalid path or share name.");
        if (r.Type == "nfs")
        {
            if (!path.StartsWith('/')) return Msg.F("An NFS path starts with /.");
            if (r.Version is { Length: > 0 } nv && !NfsVersions.Contains(nv)) return Msg.F("Unknown protocol version.");
            return null;
        }
        if (path.StartsWith('/')) return Msg.F("An SMB share is a name, such as “photos”, without a leading /.");
        if (r.Version is { Length: > 0 } sv && !SmbVersions.Contains(sv)) return Msg.F("Unknown protocol version.");
        if (!string.IsNullOrEmpty(r.User))
        {
            if (!RemoteUserRegex().IsMatch(r.User)) return Msg.F("Invalid user name.");
            if (string.IsNullOrEmpty(r.Password) || r.Password.Length > 128 || r.Password.Any(char.IsControl)) return Msg.F("Invalid password: 1 to 128 characters, no control character.");
        }
        if (!string.IsNullOrEmpty(r.Domain) && !RemoteDomainRegex().IsMatch(r.Domain)) return Msg.F("Invalid domain.");
        return null;
    }
}
