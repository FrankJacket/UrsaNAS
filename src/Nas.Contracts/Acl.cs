using System.Text.RegularExpressions;

namespace Nas.Contracts;

public enum AclSubjectKind { User = 0, Group = 1 }

/// <summary>Les cases du dialogue « Ajouter une permission » : lecture, écriture, administration.</summary>
[Flags]
public enum AclRights
{
    None = 0,
    // Lecture
    Traverse = 1 << 0,        // traverser / exécuter
    ListData = 1 << 1,        // lister le dossier / lire les données
    ReadAttr = 1 << 2,
    ReadExtAttr = 1 << 3,
    ReadPerm = 1 << 4,
    // Écriture
    CreateFiles = 1 << 5,     // créer des fichiers / écrire des données
    CreateFolders = 1 << 6,   // créer des dossiers / ajouter des données
    WriteAttr = 1 << 7,
    WriteExtAttr = 1 << 8,
    DeleteSelf = 1 << 9,      // supprimer l'élément lui-même
    DeleteChildren = 1 << 10, // supprimer les sous-dossiers et fichiers
    // Administration
    Admin = 1 << 11,          // modifier les permissions, prendre possession

    Read = Traverse | ListData | ReadAttr | ReadExtAttr | ReadPerm,
    Write = CreateFiles | CreateFolders | WriteAttr | WriteExtAttr | DeleteSelf | DeleteChildren,
    All = Read | Write | Admin,
}

/// <summary>À quoi la règle s'applique : le dossier lui-même, ses sous-dossiers, ses fichiers (combinables).</summary>
[Flags]
public enum AclApply { None = 0, ThisFolder = 1, SubFolders = 2, Files = 4, Everything = 7 }

/// <summary>
/// Une règle de permission sur un dossier : qui (utilisateur ou groupe), autoriser ou refuser, quels droits,
/// à quels éléments, et jusqu'à quelle profondeur (<see cref="FirstLevelOnly"/> = seulement les éléments directement dedans).
/// </summary>
public sealed record AclRule(AclSubjectKind Kind, string Name, bool Allow, AclRights Rights, AclApply Apply, bool FirstLevelOnly = false);

/// <summary>Les règles posées sur un dossier d'un partage (<c>Path</c> vide = la racine du partage).</summary>
public sealed record FolderAcl(string Share, string Path, IReadOnlyList<AclRule> Rules);

/// <param name="Ancestors">Règles des dossiers parents (de la racine du partage au parent direct) : l'agent en déduit l'héritage à écrire.</param>
public sealed record SetFolderAclRequest(string Share, string Path, IReadOnlyList<AclRule> Rules, IReadOnlyList<IReadOnlyList<AclRule>> Ancestors, bool Inherit = true);

/// <summary>Règles voulues d'un dossier et s'il hérite des permissions de son parent (faux = héritage rompu).</summary>
public sealed record FolderState(IReadOnlyList<AclRule> Rules, bool Inherit = true);

/// <summary>Crée le sous-dossier <c>Name</c> dans <c>Path</c> (vide = racine) en passant par Samba, qui lui applique l'héritage.</summary>
public sealed record CreateFolderRequest(string Share, string Path, string Name);

/// <summary>Réinitialise l'ACL de tout ce qui existe déjà dans un partage (passage d'un partage existant au mode permissions).</summary>
public sealed record ResetAclRequest(string Share, string Path = "");

/// <summary>Déplace ou renomme un dossier : il devient <c>DestParent/NewName</c> (renommer = même parent, autre nom).</summary>
public sealed record MoveFolderRequest(string Share, string Path, string DestParent, string NewName);

/// <summary>Copie un dossier (contenu compris) vers <c>DestParent/NewName</c> ; la copie reçoit les permissions héritées de sa destination.</summary>
public sealed record CopyFolderRequest(string Share, string Path, string DestParent, string NewName);

/// <summary>Un groupe du NAS (groupe Linux géré par UrsaNAS) et ses membres.</summary>
public sealed record NasGroup(string Name, IReadOnlyList<string> Members);

public sealed record SetGroupMembersRequest(IReadOnlyList<string> Members);

public sealed record CreateGroupRequest(string Name, IReadOnlyList<string> Members);

/// <summary>Sous-dossiers directs d'un dossier d'un partage, pour l'arborescence de l'interface.</summary>
public sealed record FolderEntry(string Name, DateTime ModifiedUtc);

public sealed record FileEntry(string Name, long Size, DateTime ModifiedUtc);

/// <summary>Contenu d'un dossier : sous-dossiers, et fichiers si on les a demandés.</summary>
public sealed record FolderListing(string Path, IReadOnlyList<FolderEntry> Folders, IReadOnlyList<FileEntry>? Files = null);

/// <summary>
/// Calcule les droits effectifs d'un utilisateur sur un dossier ou un fichier, avec la même logique que Windows/Samba :
/// règles posées directement sur l'élément d'abord, puis celles héritées du parent le plus proche au plus lointain ;
/// à chaque niveau les refus passent avant les autorisations ; un droit déjà accordé ne peut plus être refusé, et inversement.
/// Sert à l'aperçu de l'interface et aux tests ; l'application réelle est faite par Samba.
/// </summary>
public static class AclEvaluator
{
    /// <param name="chain">Règles de chaque dossier de la racine du partage jusqu'au dossier visé (le dernier élément).</param>
    /// <param name="targetIsFile">Vrai pour un fichier situé directement dans le dernier dossier de la chaîne.</param>
    public static AclRights Effective(IReadOnlyList<IReadOnlyList<AclRule>> chain, string user, IReadOnlySet<string> groups, bool targetIsFile = false)
    {
        AclRights granted = AclRights.None, denied = AclRights.None;
        // Distance k entre l'élément visé et le dossier qui porte la règle : un fichier est à distance 1 de son dossier.
        for (var level = chain.Count - 1; level >= 0; level--)
        {
            var k = chain.Count - 1 - level + (targetIsFile ? 1 : 0);
            var applicable = chain[level].Where(r => Matches(r, user, groups) && AppliesAt(r, k, targetIsFile));
            foreach (var r in applicable.Where(r => !r.Allow)) denied |= r.Rights & ~granted;
            foreach (var r in applicable.Where(r => r.Allow)) granted |= r.Rights & ~denied;
        }
        return granted;
    }

    static bool Matches(AclRule r, string user, IReadOnlySet<string> groups) =>
        r.Kind == AclSubjectKind.User ? r.Name == user : groups.Contains(r.Name);

    static bool AppliesAt(AclRule r, int k, bool targetIsFile)
    {
        if (k == 0) return r.Apply.HasFlag(AclApply.ThisFolder);
        if (r.FirstLevelOnly && k != 1) return false;
        return r.Apply.HasFlag(targetIsFile ? AclApply.Files : AclApply.SubFolders);
    }
}

public static partial class AclRules
{
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,25}$")]
    private static partial Regex GroupNameRegex();

    /// <summary>Préfixe du groupe Linux : les groupes d'UrsaNAS sont reconnaissables et ceux du système ne sont jamais touchés.</summary>
    public const string GroupPrefix = "nasg-";

    public const int MaxDepth = 32;

    public static string? ValidateGroupName(string? name) =>
        string.IsNullOrEmpty(name) || !GroupNameRegex().IsMatch(name)
            ? Msg.F("Invalid group name: 1 to 26 lowercase characters (letters, digits, _ and -), starting with a letter.")
            : null;

    /// <summary>Chemin relatif dans le partage (« » = racine) : jamais de « .. », de séparateur Windows ni de caractère spécial.</summary>
    public static string? ValidateFolderPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var parts = path.Split('/');
        if (parts.Length > MaxDepth) return Msg.F("The folder path is too deep.");
        foreach (var p in parts)
            if (!ValidSegment(p)) return Msg.F("Invalid folder path.");
        return null;
    }

    /// <summary>Nom d'un dossier à créer : un seul niveau, et pas de « ; » (séparateur de commandes de smbclient).</summary>
    public static string? ValidateNewFolderName(string? name) =>
        name is not null && ValidSegment(name) && !name.Contains(';') ? null : Msg.F("Invalid folder name.");

    public static bool ValidSegment(string p) =>
        p.Length is > 0 and <= 255 && p != "." && p != ".." && p.Trim() == p
        && !p.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c));

    public static string? ValidateRule(AclRule r)
    {
        if (r.Kind == AclSubjectKind.User ? Rules.ValidateUserName(r.Name) is not null : ValidateGroupName(r.Name) is not null)
            return Msg.F("Invalid user or group in a permission.");
        if (r.Rights == AclRights.None || (r.Rights & ~AclRights.All) != 0) return Msg.F("A permission must grant or deny at least one right.");
        if (r.Apply == AclApply.None || (r.Apply & ~AclApply.Everything) != 0) return Msg.F("A permission must apply to at least one kind of item.");
        return null;
    }

    /// <summary>Une même personne ne peut pas avoir deux règles identiques (même sujet, mêmes cibles, même sens).</summary>
    public static string? ValidateRules(IReadOnlyList<AclRule> rules)
    {
        if (rules.Count > 200) return Msg.F("Too many permissions on one folder.");
        foreach (var r in rules) if (ValidateRule(r) is { } e) return e;
        return null;
    }
}

/// <summary>Héritage des permissions entre dossiers : chaîne des parents qui comptent, règles héritées, copie lors d'une rupture.</summary>
public static class AclInheritance
{
    /// <summary>Chemins des dossiers parents, de la racine (« ») au parent direct : « a/b/c » → «», «a», «a/b».</summary>
    public static IReadOnlyList<string> AncestorPaths(string path)
    {
        var result = new List<string>();
        if (path.Length == 0) return result;
        result.Add("");
        var parts = path.Split('/');
        for (var i = 1; i < parts.Length; i++) result.Add(string.Join('/', parts.Take(i)));
        return result;
    }

    static bool Inherits(IReadOnlyDictionary<string, FolderState> states, string path) => states.GetValueOrDefault(path)?.Inherit ?? true;

    /// <summary>Parents dont les règles atteignent ce dossier, de la racine au parent direct, en s'arrêtant au premier héritage rompu.</summary>
    public static IReadOnlyList<string> ParentsUsed(string path, IReadOnlyDictionary<string, FolderState> states)
    {
        var parents = AncestorPaths(path);
        if (!Inherits(states, path)) return [];
        var used = new List<string>();
        for (var j = parents.Count - 1; j >= 0; j--)
        {
            used.Insert(0, parents[j]);
            if (!Inherits(states, parents[j])) break;
        }
        return used;
    }

    /// <summary>Règles des parents utilisés, dans l'ordre racine → parent direct (ce que l'agent compile en ACE héritées).</summary>
    public static IReadOnlyList<IReadOnlyList<AclRule>> Ancestors(string path, IReadOnlyDictionary<string, FolderState> states) =>
        ParentsUsed(path, states).Select(p => states.GetValueOrDefault(p)?.Rules ?? []).ToList();

    /// <summary>Chaîne complète pour <see cref="AclEvaluator"/> : parents utilisés puis les règles propres du dossier.</summary>
    public static IReadOnlyList<IReadOnlyList<AclRule>> FullChain(string path, IReadOnlyDictionary<string, FolderState> states) =>
        [.. Ancestors(path, states), states.GetValueOrDefault(path)?.Rules ?? []];

    /// <summary>
    /// Règles héritées qui touchent ce dossier lui-même (sous-dossiers couverts, et à la bonne distance si « premier niveau »),
    /// du parent le plus proche au plus lointain, avec le dossier d'où elles viennent.
    /// </summary>
    public static IReadOnlyList<(string From, AclRule Rule)> InheritedHere(string path, IReadOnlyDictionary<string, FolderState> states)
    {
        var used = ParentsUsed(path, states);
        var result = new List<(string, AclRule)>();
        for (var j = used.Count - 1; j >= 0; j--)
        {
            var k = used.Count - j;
            foreach (var r in states.GetValueOrDefault(used[j])?.Rules ?? [])
                if (r.Apply.HasFlag(AclApply.SubFolders) && (!r.FirstLevelOnly || k == 1)) result.Add((used[j], r));
        }
        return result;
    }

    /// <summary>
    /// Règles à poser sur ce dossier pour que rompre l'héritage ne change rien aux droits de personne : chaque règle héritée,
    /// ré-ancrée ici (ce dossier, ses sous-dossiers et fichiers selon ce qu'elle couvrait, jusqu'où elle allait).
    /// </summary>
    public static IReadOnlyList<AclRule> CopyInherited(string path, IReadOnlyDictionary<string, FolderState> states)
    {
        var used = ParentsUsed(path, states);
        var copies = new List<AclRule>();
        for (var j = used.Count - 1; j >= 0; j--)
        {
            var k = used.Count - j;
            foreach (var r in states.GetValueOrDefault(used[j])?.Rules ?? [])
            {
                var here = r.Apply.HasFlag(AclApply.SubFolders) && (!r.FirstLevelOnly || k == 1);
                var below = r.FirstLevelOnly ? AclApply.None : r.Apply & (AclApply.SubFolders | AclApply.Files);
                var apply = (here ? AclApply.ThisFolder : AclApply.None) | below;
                if (apply != AclApply.None) copies.Add(r with { Apply = apply, FirstLevelOnly = false });
            }
        }
        return copies;
    }
}
