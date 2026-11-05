using System.Text.RegularExpressions;

namespace Nas.Contracts;

/// <summary>Un utilisateur du NAS (compte système sans shell + compte Samba).</summary>
public sealed record NasUser(string Name);

public sealed record CreateUserRequest(string Name, string Password);

public sealed record SetPasswordRequest(string Password);

/// <summary>Un partage SMB. Son dossier est toujours <c>/srv/nas/&lt;Name&gt;</c> : le chemin n'est jamais fourni par l'appelant.</summary>
public sealed record ShareDef(string Name, string Comment, bool ReadOnly, IReadOnlyList<string> ValidUsers);

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
            return "Nom de partage invalide : 1 à 32 caractères (lettres, chiffres, _ et -), sans espace.";
        if (ReservedShares.Contains(name)) return $"« {name} » est un nom réservé.";
        return null;
    }

    public static string? ValidateUserName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !UserNameRegex().IsMatch(name))
            return "Nom d'utilisateur invalide : 1 à 32 caractères en minuscules (lettres, chiffres, _ et -), sans espace.";
        if (ReservedUsers.Contains(name)) return $"« {name} » est un nom réservé.";
        return null;
    }

    public static string? ValidatePassword(string? password)
    {
        if (password is null || password.Length < MinPasswordLength)
            return $"Le mot de passe doit contenir au moins {MinPasswordLength} caractères.";
        if (password.Length > MaxPasswordLength) return $"Le mot de passe est limité à {MaxPasswordLength} caractères.";
        if (password.Any(char.IsControl)) return "Le mot de passe ne peut pas contenir de caractère de contrôle.";
        return null;
    }

    /// <summary>Le commentaire finit dans smb.conf : une seule ligne, sans caractère de contrôle.</summary>
    public static string? ValidateComment(string? comment)
    {
        if (comment is null) return null;
        if (comment.Length > 200) return "Le commentaire est limité à 200 caractères.";
        if (comment.Any(char.IsControl)) return "Le commentaire ne peut pas contenir de caractère de contrôle.";
        return null;
    }

    /// <summary>Valide un jeu de partages complet : noms, doublons, utilisateurs connus.</summary>
    public static string? ValidateShares(IReadOnlyList<ShareDef> shares, IReadOnlySet<string> knownUsers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in shares)
        {
            if (ValidateShareName(s.Name) is { } e1) return e1;
            if (ValidateComment(s.Comment) is { } e2) return e2;
            if (!seen.Add(s.Name)) return $"Le partage « {s.Name} » est en double.";
            if (s.ValidUsers is null || s.ValidUsers.Count == 0) return $"Le partage « {s.Name} » doit avoir au moins un utilisateur.";
            foreach (var u in s.ValidUsers)
                if (!knownUsers.Contains(u)) return $"Le partage « {s.Name} » référence l'utilisateur inconnu « {u} ».";
        }
        return null;
    }
}
