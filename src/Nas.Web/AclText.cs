using Nas.Contracts;
using Nas.Web.Localization;

namespace Nas.Web;

/// <summary>Texte lisible des droits et des cibles d'une règle de permission.</summary>
public static class AclText
{
    static readonly (AclRights Flag, string Label)[] Singles =
    [
        (AclRights.Traverse, "Traverse folder / execute file"), (AclRights.ListData, "List folder / read data"),
        (AclRights.ReadAttr, "Read attributes"), (AclRights.ReadExtAttr, "Read extended attributes"),
        (AclRights.ReadPerm, "Read permissions"),
        (AclRights.CreateFiles, "Create files / write data"), (AclRights.CreateFolders, "Create folders / append data"),
        (AclRights.WriteAttr, "Write attributes"), (AclRights.WriteExtAttr, "Write extended attributes"),
        (AclRights.DeleteSelf, "Delete"), (AclRights.DeleteChildren, "Delete sub-folders and files"),
        (AclRights.Admin, "Change permissions / take ownership"),
    ];

    /// <summary>« Full control », sinon les familles complètes (« Read », « Write ») puis les droits isolés.</summary>
    public static string Rights(AclRights r, Loc L)
    {
        if (r == AclRights.All) return L["Full control"];
        var parts = new List<string>();
        var rest = r;
        if (r.HasFlag(AclRights.Read)) { parts.Add(L["Read"]); rest &= ~AclRights.Read; }
        if (r.HasFlag(AclRights.Write)) { parts.Add(L["Write"]); rest &= ~AclRights.Write; }
        foreach (var (flag, label) in Singles)
            if (rest.HasFlag(flag)) parts.Add(L[label]);
        return parts.Count == 0 ? "—" : string.Join(", ", parts);
    }

    public static string Apply(AclApply a, bool firstLevel, Loc L)
    {
        if (a == AclApply.Everything) return firstLevel ? L["This folder, sub-folders and files (first level only)"] : L["This folder, sub-folders and files"];
        var parts = new List<string>();
        if (a.HasFlag(AclApply.ThisFolder)) parts.Add(L["This folder"]);
        if (a.HasFlag(AclApply.SubFolders)) parts.Add(L["Sub-folders"]);
        if (a.HasFlag(AclApply.Files)) parts.Add(L["Files"]);
        var text = string.Join(", ", parts);
        return firstLevel && (a & ~AclApply.ThisFolder) != 0 ? text + " · " + L["first level only"] : text;
    }
}
