using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Traduit les règles du dialogue (utilisateur/groupe, autoriser/refuser, cases de droits, « appliquer à ») en entrées
/// d'ACL NT au format de <c>smbcacls --set</c> : <c>ACL:&lt;SID&gt;:ALLOWED/OI|CI|IO/0x1301bf</c>. Fonction pure, testée sans Samba.
/// </summary>
public static class AclCompiler
{
    // Bits de masque d'accès NT.
    const uint ReadData = 0x1, WriteData = 0x2, AppendData = 0x4, ReadEa = 0x8, WriteEa = 0x10, Execute = 0x20,
        DeleteChild = 0x40, ReadAttr = 0x80, WriteAttr = 0x100, Delete = 0x10000, ReadControl = 0x20000,
        WriteDac = 0x40000, WriteOwner = 0x80000, Synchronize = 0x100000;

    public static uint Mask(AclRights r, bool allow)
    {
        uint m = 0;
        void If(AclRights f, uint bits) { if (r.HasFlag(f)) m |= bits; }
        If(AclRights.Traverse, Execute);
        If(AclRights.ListData, ReadData);
        If(AclRights.ReadAttr, ReadAttr);
        If(AclRights.ReadExtAttr, ReadEa);
        If(AclRights.ReadPerm, ReadControl);
        If(AclRights.CreateFiles, WriteData);
        If(AclRights.CreateFolders, AppendData);
        If(AclRights.WriteAttr, WriteAttr);
        If(AclRights.WriteExtAttr, WriteEa);
        If(AclRights.DeleteSelf, Delete);
        If(AclRights.DeleteChildren, DeleteChild);
        If(AclRights.Admin, WriteDac | WriteOwner);
        // Les clients SMB demandent toujours SYNCHRONIZE à l'ouverture : l'accorder avec tout droit autorisé,
        // mais ne jamais le refuser (cela bloquerait tout, y compris ce que d'autres règles autorisent).
        if (allow && m != 0) m |= Synchronize;
        return m;
    }

    /// <summary>Drapeaux d'héritage : « ce dossier » seul = aucun ; sous-dossiers = CI ; fichiers = OI ; sans « ce dossier » = IO.</summary>
    public static string Flags(AclApply a, bool firstLevelOnly)
    {
        var parts = new List<string>();
        if (a.HasFlag(AclApply.Files)) parts.Add("OI");
        if (a.HasFlag(AclApply.SubFolders)) parts.Add("CI");
        if (parts.Count == 0) return "0x0";
        if (!a.HasFlag(AclApply.ThisFolder)) parts.Add("IO");
        if (firstLevelOnly) parts.Add("NP");
        return string.Join('|', parts);
    }

    /// <summary>Entrées explicites seules, dans l'ordre canonique de Windows (refus avant autorisations).</summary>
    public static IReadOnlyList<string> Compile(IReadOnlyList<AclRule> rules, Func<AclSubjectKind, string, string> sidOf, string serviceSid) =>
        CompileFolder(rules, [], sidOf, serviceSid);

    /// <summary>Droits de l'agent sur tout l'arbre : l'ACL n'est ainsi jamais vide. Même valeur partout pour pouvoir la « modifier » à l'identique et déclencher la propagation.</summary>
    public static string ServiceAce(string serviceSid) => $"ACL:{serviceSid}:ALLOWED/OI|CI/0x1f01ff";

    /// <summary>
    /// ACL complète d'un dossier : ses règles posées directement, puis celles héritées des dossiers parents
    /// (du plus proche au plus lointain, marquées <c>I</c>) car <c>smbcacls --set</c> remplace toute l'ACL.
    /// </summary>
    /// <param name="ancestors">Règles des dossiers parents, de la racine du partage jusqu'au parent direct.</param>
    /// <param name="sidOf">SID d'un utilisateur ou d'un groupe.</param>
    public static IReadOnlyList<string> CompileFolder(IReadOnlyList<AclRule> own, IReadOnlyList<IReadOnlyList<AclRule>> ancestors,
        Func<AclSubjectKind, string, string> sidOf, string serviceSid)
    {
        var aces = new List<string>();
        static IEnumerable<AclRule> Ordered(IEnumerable<AclRule> rules) => rules.Where(r => !r.Allow).Concat(rules.Where(r => r.Allow));

        foreach (var r in Ordered(own))
            Add(aces, r, Flags(r.Apply, r.FirstLevelOnly), sidOf);
        for (var j = ancestors.Count - 1; j >= 0; j--)
            foreach (var r in Ordered(ancestors[j]))
                if (InheritedFlags(r, ancestors.Count - j) is { } flags) Add(aces, r, flags, sidOf);
        aces.Add(ServiceAce(serviceSid));
        return aces;
    }

    static void Add(List<string> aces, AclRule r, string flags, Func<AclSubjectKind, string, string> sidOf)
    {
        var mask = Mask(r.Rights, r.Allow);
        if (mask != 0) aces.Add($"ACL:{sidOf(r.Kind, r.Name)}:{(r.Allow ? "ALLOWED" : "DENIED")}/{flags}/0x{mask:x}");
    }

    /// <summary>
    /// Drapeaux de l'ACE qu'un dossier hérite de la règle d'un ancêtre situé <paramref name="distance"/> niveaux plus haut
    /// (1 = le parent direct) ; <c>null</c> si la règle ne le concerne pas ni ses descendants.
    /// </summary>
    public static string? InheritedFlags(AclRule r, int distance)
    {
        var appliesHere = r.Apply.HasFlag(AclApply.SubFolders) && (!r.FirstLevelOnly || distance == 1);
        var oi = !r.FirstLevelOnly && r.Apply.HasFlag(AclApply.Files);
        var ci = !r.FirstLevelOnly && r.Apply.HasFlag(AclApply.SubFolders);
        if (!appliesHere && !oi && !ci) return null;
        var parts = new List<string>();
        if (oi) parts.Add("OI");
        if (ci) parts.Add("CI");
        if (!appliesHere) parts.Add("IO");
        parts.Add("I");
        return string.Join('|', parts);
    }
}
