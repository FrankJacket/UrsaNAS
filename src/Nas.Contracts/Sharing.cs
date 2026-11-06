using System.Text.RegularExpressions;

namespace Nas.Contracts;

/// <summary>Un utilisateur du NAS (compte système sans shell + compte Samba).</summary>
public sealed record NasUser(string Name);

public sealed record CreateUserRequest(string Name, string Password);

public sealed record SetPasswordRequest(string Password);

/// <summary>
/// Un partage SMB. Le chemin n'est jamais fourni par l'appelant : sans <c>Dataset</c>, le dossier est
/// <c>/srv/nas/&lt;Name&gt;</c> ; avec <c>Dataset</c> (ex. <c>tank/photos</c>), c'est le point de montage de ce jeu de données ZFS.
/// </summary>
public sealed record ShareDef(string Name, string Comment, bool ReadOnly, IReadOnlyList<string> ValidUsers, string? Dataset = null);

/// <summary>L'ensemble complet des partages à appliquer (remplace l'existant).</summary>
public sealed record ApplySharesRequest(IReadOnlyList<ShareDef> Shares);

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

    /// <summary>Valide un jeu de partages complet : noms, doublons, utilisateurs connus.</summary>
    public static string? ValidateShares(IReadOnlyList<ShareDef> shares, IReadOnlySet<string> knownUsers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var datasets = new HashSet<string>(StringComparer.Ordinal);
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
            if (s.ValidUsers is null || s.ValidUsers.Count == 0) return Msg.F("Share “{0}” must have at least one user.", s.Name);
            foreach (var u in s.ValidUsers)
                if (!knownUsers.Contains(u)) return Msg.F("Share “{0}” references the unknown user “{1}”.", s.Name, u);
        }
        return null;
    }
}
