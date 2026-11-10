using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nas.Contracts;

/// <summary>
/// Une connexion à un stockage distant, montée en permanence sous le dossier des stockages distants.
/// SMB et NFS : <c>Host</c> et <c>Path</c> (nom du partage, ou chemin exporté). OneDrive et Dropbox : <c>Path</c> est un sous-dossier facultatif
/// du compte, <c>Host</c> est vide. <c>Cache</c>, <c>CacheGb</c> et <c>BwLimitMbps</c> ne concernent que les services en ligne.
/// </summary>
public sealed record RemoteStorage(string Name, string Type, string Host, string Path, string? User, string? Domain, string? Version, bool ReadOnly,
    string MountPoint, bool Mounted, long TotalBytes, long FreeBytes, string? Cache = null, int CacheGb = 0, int BwLimitMbps = 0);

/// <summary>
/// Ce que l'utilisateur saisit. SMB : <c>User</c> vide = accès invité. <c>Version</c> vide : négociée automatiquement.
/// OneDrive et Dropbox : <c>Token</c> est le jeton JSON produit par <c>rclone authorize</c> ; <c>Cache</c> vaut off, writes ou full ;
/// <c>BwLimitMbps</c> 0 = pas de limite.
/// </summary>
public sealed record AddRemoteRequest(string Name, string Type, string Host, string Path, string? User, string? Password, string? Domain, string? Version, bool ReadOnly,
    string? Token = null, string Cache = "full", int CacheGb = 10, int BwLimitMbps = 0, bool OwnApp = false);

/// <summary>Types de stockage distant que le NAS sait monter (selon les programmes installés). <c>OneDriveSignIn</c> : la connexion intégrée à OneDrive est configurée.</summary>
public sealed record RemoteTypes(IReadOnlyList<string> Available, bool OneDriveSignIn = false);

/// <summary>Début d'une connexion par code d'appareil : l'utilisateur saisit <c>UserCode</c> sur <c>VerificationUri</c>.</summary>
public sealed record DeviceCodeStart(string DeviceCode, string UserCode, string VerificationUri, int ExpiresIn, int Interval);

/// <summary>État d'une connexion par code d'appareil : <c>pending</c>, <c>done</c> (avec le jeton au format rclone) ou <c>error</c>.</summary>
public sealed record DeviceCodePoll(string Status, string? Token = null, string? Message = null);

public sealed record DeviceCodePollRequest(string DeviceCode);

public static partial class Rules
{
    public static readonly IReadOnlyList<string> RemoteKinds = ["smb", "nfs", "onedrive", "dropbox"];
    public static readonly IReadOnlyList<string> CloudKinds = ["onedrive", "dropbox"];
    public static readonly IReadOnlyList<string> SmbVersions = ["3.1.1", "3.0", "2.1", "2.0", "1.0"];
    public static readonly IReadOnlyList<string> NfsVersions = ["4.2", "4.1", "4.0", "3"];
    public static readonly IReadOnlyList<string> CacheModes = ["off", "writes", "full"];

    public static bool IsCloud(string? type) => CloudKinds.Contains(type ?? "");

    // Jamais de « - » en tête : en NFS le serveur ouvre l'argument de mount (« -o:/chemin »), qui serait pris pour une option.
    [GeneratedRegex(@"^[A-Za-z0-9:][A-Za-z0-9._:\-]{0,252}$")]
    private static partial Regex RemoteHostRegex();

    [GeneratedRegex(@"^[^\x00-\x1f\\,:""'/]{1,64}$")]
    private static partial Regex RemoteUserRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,64}$")]
    private static partial Regex RemoteDomainRegex();

    /// <summary>Le jeton que <c>rclone authorize</c> affiche : un objet JSON avec au moins un <c>access_token</c>, sur une seule ligne une fois nettoyé.</summary>
    public static bool IsRcloneToken(string? token, out string compact)
    {
        compact = "";
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192) return false;
        try
        {
            using var doc = JsonDocument.Parse(token.Trim());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("access_token", out var a) || a.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(a.GetString())) return false;
            compact = JsonSerializer.Serialize(root); // sans mise en forme : le jeton tient sur une seule ligne du fichier rclone
            return !compact.Any(char.IsControl);
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Contrôle complet d'une connexion avant tout appel système : rien de ce texte ne doit pouvoir sortir d'un champ de /etc/fstab, d'une option de montage ni d'un fichier d'unité systemd.</summary>
    public static string? ValidateRemote(AddRemoteRequest r)
    {
        if (ValidateMountName(r.Name) is { } n) return n;
        if (!RemoteKinds.Contains(r.Type)) return Msg.F("Unknown type of remote storage.");
        if (IsCloud(r.Type)) return ValidateCloud(r);
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

    static string? ValidateCloud(AddRemoteRequest r)
    {
        // Le sous-dossier finit dans une ligne ExecStart : ni « % » ni « $ » (systemd les interprète), ni guillemet, ni antislash.
        var folder = r.Path ?? "";
        if (folder.Length > 300 || folder.Any(char.IsControl) || folder.IndexOfAny(['\\', ',', '"', '\'', '%', '$']) >= 0
            || folder.StartsWith('/') || folder.Split('/').Contains("..") || folder.Contains("//"))
            return Msg.F("Invalid folder: a path inside the account, without a leading /.");
        if (r.OwnApp && r.Type != "onedrive") return Msg.F("Unknown type of remote storage.");
        if (!IsRcloneToken(r.Token, out _)) return Msg.F("Invalid access token: paste the whole result of “rclone authorize”.");
        if (!CacheModes.Contains(r.Cache)) return Msg.F("Unknown cache mode.");
        if (r.CacheGb is < 1 or > 1000) return Msg.F("The cache size is between 1 and 1000 GB.");
        if (r.BwLimitMbps is < 0 or > 100000) return Msg.F("The speed limit is between 0 (none) and 100000 MB/s.");
        return null;
    }
}
