using Nas.Agent.Services;
using Nas.Contracts;
using Xunit;

namespace Nas.Agent.Tests;

public class AclTests
{
    static AclRule U(string n, bool allow, AclRights r, AclApply a, bool first = false) => new(AclSubjectKind.User, n, allow, r, a, first);
    static AclRule G(string n, bool allow, AclRights r, AclApply a, bool first = false) => new(AclSubjectKind.Group, n, allow, r, a, first);

    static readonly AclRights Modify = AclRights.Read | AclRights.Write;

    // ---- Compilateur

    [Theory]
    [InlineData(AclApply.ThisFolder, false, "0x0")]
    [InlineData(AclApply.ThisFolder | AclApply.SubFolders, false, "CI")]
    [InlineData(AclApply.ThisFolder | AclApply.Files, false, "OI")]
    [InlineData(AclApply.Everything, false, "OI|CI")]
    [InlineData(AclApply.SubFolders, false, "CI|IO")]
    [InlineData(AclApply.Files, false, "OI|IO")]
    [InlineData(AclApply.SubFolders | AclApply.Files, false, "OI|CI|IO")]
    [InlineData(AclApply.Everything, true, "OI|CI|NP")]
    [InlineData(AclApply.SubFolders, true, "CI|IO|NP")]
    [InlineData(AclApply.ThisFolder, true, "0x0")] // « premier niveau » n'a pas de sens sans héritage
    public void Les_drapeaux_d_heritage_suivent_les_cases_appliquer_a(AclApply apply, bool first, string expected) =>
        Assert.Equal(expected, AclCompiler.Flags(apply, first));

    [Fact]
    public void Un_droit_autorise_ajoute_synchronize_pas_un_refus()
    {
        Assert.Equal(0x1200a9u, AclCompiler.Mask(AclRights.Read, true)); // « lecture » validé sur Samba
        Assert.Equal(0x2u, AclCompiler.Mask(AclRights.CreateFiles, false));
        Assert.Equal(0x100002u, AclCompiler.Mask(AclRights.CreateFiles, true));
    }

    [Fact]
    public void Creer_et_supprimer_sont_des_bits_distincts()
    {
        var create = AclCompiler.Mask(AclRights.CreateFiles | AclRights.CreateFolders, true);
        var delete = AclCompiler.Mask(AclRights.DeleteSelf | AclRights.DeleteChildren, true);
        Assert.Equal(0u, create & 0x10040);
        Assert.Equal(0u, delete & 0x6);
    }

    [Fact]
    public void Les_refus_passent_avant_les_autorisations_et_le_service_reste_present()
    {
        var rules = new[]
        {
            U("alice", true, AclRights.Read, AclApply.Everything),
            G("temps", false, AclRights.CreateFiles, AclApply.SubFolders),
        };
        var aces = AclCompiler.Compile(rules, (k, n) => k == AclSubjectKind.User ? "S-1-5-21-1-1000" : "S-1-22-2-2000", "S-1-5-21-1-999");
        Assert.Equal(3, aces.Count);
        Assert.Equal("ACL:S-1-22-2-2000:DENIED/CI|IO/0x2", aces[0]);
        Assert.StartsWith("ACL:S-1-5-21-1-1000:ALLOWED/OI|CI/", aces[1]);
        Assert.StartsWith("ACL:S-1-5-21-1-999:ALLOWED/", aces[2]);
    }

    // ---- Validation

    [Theory]
    [InlineData("", true)]
    [InlineData("a", true)]
    [InlineData("a/b/c", true)]
    [InlineData("a b/Dossier é", true)]
    [InlineData("..", false)]
    [InlineData("a/../b", false)]
    [InlineData("a//b", false)]
    [InlineData("/a", false)]
    [InlineData("a/", false)]
    [InlineData("a\\b", false)]
    [InlineData("a*", false)]
    [InlineData(".", false)]
    public void Les_chemins_de_dossier_sont_confines_au_partage(string path, bool ok) =>
        Assert.Equal(ok, AclRules.ValidateFolderPath(path) is null);

    [Fact]
    public void Une_regle_doit_nommer_quelqu_un_donner_un_droit_et_une_cible()
    {
        Assert.Null(AclRules.ValidateRule(U("alice", true, AclRights.Read, AclApply.Everything)));
        Assert.NotNull(AclRules.ValidateRule(U("Alice!", true, AclRights.Read, AclApply.Everything)));
        Assert.NotNull(AclRules.ValidateRule(U("alice", true, AclRights.None, AclApply.Everything)));
        Assert.NotNull(AclRules.ValidateRule(U("alice", true, AclRights.Read, AclApply.None)));
        Assert.NotNull(AclRules.ValidateRule(G("Équipe", true, AclRights.Read, AclApply.Everything)));
    }

    // ---- Évaluateur : le cas du patron, du directeur, du chef d'équipe et des employés

    // Racine R : patron tout partout ; directeur lecture/écriture + création de dossiers à la racine seulement.
    // S1 : chef1 tout dans S1 et dessous ; groupe equipe1 : lecture directe dans S1, écriture seulement dans les enfants.
    static readonly IReadOnlyList<AclRule> Root =
    [
        U("patron", true, AclRights.All, AclApply.Everything),
        U("directeur", true, Modify, AclApply.ThisFolder | AclApply.Files, first: false),
        U("directeur", true, AclRights.Read | AclRights.CreateFolders, AclApply.ThisFolder),
    ];

    static readonly IReadOnlyList<AclRule> S1 =
    [
        U("chef1", true, AclRights.All, AclApply.Everything),
        G("equipe1", true, AclRights.Read, AclApply.ThisFolder | AclApply.Files),
        G("equipe1", true, Modify & ~AclRights.CreateFolders, AclApply.SubFolders),
    ];

    static AclRights At(string user, string[] groups, bool file, params IReadOnlyList<AclRule>[] chain) =>
        AclEvaluator.Effective(chain, user, groups.ToHashSet(), file);

    [Fact]
    public void Le_patron_a_tout_partout() =>
        Assert.Equal(AclRights.All, At("patron", [], false, Root, S1, []));

    [Fact]
    public void Le_directeur_cree_des_dossiers_a_la_racine_mais_pas_dans_S()
    {
        Assert.True(At("directeur", [], false, Root).HasFlag(AclRights.CreateFolders));
        Assert.False(At("directeur", [], false, Root, S1).HasFlag(AclRights.CreateFolders));
    }

    [Fact]
    public void Le_chef_a_tout_dans_son_S_et_ses_enfants_mais_rien_dans_un_autre_S()
    {
        Assert.Equal(AclRights.All, At("chef1", [], false, Root, S1));
        Assert.Equal(AclRights.All, At("chef1", [], false, Root, S1, []));
        Assert.Equal(AclRights.None, At("chef1", [], false, Root, []));
    }

    [Fact]
    public void Un_employe_ne_cree_pas_de_dossier_dans_S_mais_ecrit_dans_les_enfants()
    {
        var inS = At("emp", ["equipe1"], false, Root, S1);
        Assert.True(inS.HasFlag(AclRights.Read));
        Assert.False(inS.HasFlag(AclRights.CreateFolders));

        var inChild = At("emp", ["equipe1"], false, Root, S1, []);
        Assert.True(inChild.HasFlag(AclRights.CreateFiles));
        Assert.False(inChild.HasFlag(AclRights.CreateFolders));
    }

    [Fact]
    public void Un_employe_lit_les_fichiers_directement_dans_S_sans_pouvoir_les_modifier()
    {
        var file = At("emp", ["equipe1"], true, Root, S1);
        Assert.True(file.HasFlag(AclRights.ListData));
        Assert.False(file.HasFlag(AclRights.CreateFiles));
    }

    [Fact]
    public void Un_employe_membre_de_deux_equipes_cumule_les_droits()
    {
        var s2 = new[] { G("equipe2", true, AclRights.Read, AclApply.Everything) };
        Assert.True(At("emp", ["equipe1", "equipe2"], false, Root, s2).HasFlag(AclRights.ListData));
        Assert.Equal(AclRights.None, At("emp", ["equipe1"], false, Root, s2));
    }

    [Fact]
    public void Un_refus_pose_plus_pres_l_emporte_sur_une_autorisation_heritee()
    {
        IReadOnlyList<AclRule> deny = [U("emp", false, AclRights.DeleteSelf | AclRights.DeleteChildren, AclApply.Everything)];
        IReadOnlyList<AclRule> allow = [U("emp", true, Modify, AclApply.Everything)];
        var r = At("emp", [], false, allow, deny);
        Assert.True(r.HasFlag(AclRights.CreateFiles));
        Assert.False(r.HasFlag(AclRights.DeleteSelf));
    }

    [Fact]
    public void Une_autorisation_posee_directement_l_emporte_sur_un_refus_herite()
    {
        IReadOnlyList<AclRule> deny = [U("emp", false, AclRights.CreateFiles, AclApply.Everything)];
        IReadOnlyList<AclRule> allow = [U("emp", true, AclRights.CreateFiles, AclApply.Everything)];
        Assert.True(At("emp", [], false, deny, allow).HasFlag(AclRights.CreateFiles));
    }

    [Fact]
    public void Dans_un_meme_niveau_le_refus_l_emporte()
    {
        IReadOnlyList<AclRule> both = [U("emp", true, AclRights.Read, AclApply.Everything), G("g", false, AclRights.ListData, AclApply.Everything)];
        var r = At("emp", ["g"], false, both);
        Assert.False(r.HasFlag(AclRights.ListData));
        Assert.True(r.HasFlag(AclRights.Traverse));
    }

    [Fact]
    public void Premier_niveau_seulement_s_arrete_au_premier_niveau()
    {
        IReadOnlyList<AclRule> first = [U("emp", true, AclRights.Read, AclApply.SubFolders, first: true)];
        Assert.True(At("emp", [], false, first, []).HasFlag(AclRights.ListData));
        Assert.Equal(AclRights.None, At("emp", [], false, first, [], []));
    }
}

public class AclPlumbingTests
{
    static string Sid(AclSubjectKind k, string n) => k == AclSubjectKind.User ? "S-1-5-21-1-" + n.Length : "S-1-22-2-" + n.Length;
    static AclRule R(string n, AclApply a, bool first = false, bool allow = true) => new(AclSubjectKind.User, n, allow, AclRights.Read, a, first);

    [Theory]
    [InlineData(AclApply.Everything, false, 1, "OI|CI|I")]
    [InlineData(AclApply.Everything, true, 1, "I")]
    [InlineData(AclApply.Everything, true, 2, null)]
    [InlineData(AclApply.SubFolders, false, 3, "CI|I")]
    [InlineData(AclApply.Files, false, 1, "OI|IO|I")]
    [InlineData(AclApply.ThisFolder, false, 1, null)]
    [InlineData(AclApply.Files, true, 1, null)]
    public void L_heritage_d_un_dossier_depend_de_la_distance_et_des_cibles(AclApply a, bool first, int distance, string? expected) =>
        Assert.Equal(expected, AclCompiler.InheritedFlags(R("x", a, first), distance));

    [Fact]
    public void L_acl_d_un_dossier_met_ses_regles_puis_l_heritage_du_plus_proche_au_plus_lointain()
    {
        var own = new[] { R("own", AclApply.ThisFolder) };
        var grand = new[] { R("grand", AclApply.Everything) };
        var parent = new[] { R("parent", AclApply.Everything), R("pdeny", AclApply.Everything, allow: false) };
        var aces = AclCompiler.CompileFolder(own, [grand, parent], Sid, "S-1-5-21-1-0");
        Assert.Equal(5, aces.Count);
        Assert.Contains("S-1-5-21-1-3:ALLOWED/0x0", aces[0]);                  // propre
        Assert.Contains("S-1-5-21-1-5:DENIED/OI|CI|I", aces[1]);               // parent : refus d'abord
        Assert.Contains("S-1-5-21-1-6:ALLOWED/OI|CI|I", aces[2]);
        Assert.Contains("S-1-5-21-1-5:ALLOWED/OI|CI|I", aces[3]);               // grand-parent
        Assert.Equal(AclCompiler.ServiceAce("S-1-5-21-1-0"), aces[4]);
    }

    [Fact]
    public void Les_groupes_du_systeme_sont_ignores()
    {
        var groups = GroupService.ParseGroups("root:x:0:\nnasg-compta:x:990:alice,bob\nusers:x:100:\nnasg-vide:x:991:\n");
        Assert.Equal(["compta", "vide"], groups.Select(g => g.Name));
        Assert.Equal(["alice", "bob"], groups[0].Members);
        Assert.Empty(groups[1].Members);
    }

    [Fact]
    public void Le_sid_vient_de_la_ligne_User_SID_de_pdbedit() =>
        Assert.Equal("S-1-5-21-1-2-3-1001", AclService.ParseUserSid("Unix username:  bob\nUser SID:       S-1-5-21-1-2-3-1001\nPrimary Group SID: S-1-5-21-1-2-3-513\n"));

    [Fact]
    public void Les_chemins_parents_vont_de_la_racine_au_parent_direct()
    {
        Assert.Empty(AclInheritance.AncestorPaths(""));
        Assert.Equal([""], AclInheritance.AncestorPaths("a"));
        Assert.Equal(["", "a", "a/b"], AclInheritance.AncestorPaths("a/b/c"));
    }

    [Fact]
    public void Un_partage_en_mode_permissions_n_a_ni_valid_users_ni_lecture_seule()
    {
        var text = ShareConf.Render([new ShareDef("compta", "", true, ["alice"], null, Acl: true)], "/srv/nas");
        Assert.Contains("vfs objects = acl_xattr", text);
        Assert.Contains("admin users = ursanas-acl", text);
        Assert.Contains("read only = no", text);
        Assert.DoesNotContain("valid users", text);
        Assert.Contains("valid users = alice", ShareConf.Render([new ShareDef("p", "", false, ["alice"])], "/srv/nas"));
    }
}

public class AclInheritanceTests
{
    static AclRule G(string n, AclApply a, AclRights r = AclRights.Read, bool first = false, bool allow = true) =>
        new(AclSubjectKind.Group, n, allow, r, a, first);

    static Dictionary<string, FolderState> States() => new()
    {
        [""] = new([G("a", AclApply.Everything), G("deny", AclApply.Everything, AclRights.CreateFiles, allow: false)]),
        ["x"] = new([G("b", AclApply.SubFolders | AclApply.Files, AclRights.Read | AclRights.Write)]),
        ["x/y"] = new([G("c", AclApply.Files, first: true)]),
    };

    [Fact]
    public void La_chaine_s_arrete_au_premier_heritage_rompu()
    {
        var s = States();
        Assert.Equal(["", "x"], AclInheritance.ParentsUsed("x/y", s));
        s["x"] = s["x"] with { Inherit = false };
        Assert.Equal(["x"], AclInheritance.ParentsUsed("x/y", s));
        s["x/y"] = s["x/y"] with { Inherit = false };
        Assert.Empty(AclInheritance.ParentsUsed("x/y", s));
    }

    [Fact]
    public void Les_regles_heritees_sont_celles_qui_couvrent_les_sous_dossiers()
    {
        var inherited = AclInheritance.InheritedHere("x/y/z", States());
        Assert.Equal(["b", "a", "deny"], inherited.Select(i => i.Rule.Name)); // le plus proche d'abord ; « Files » de x/y ne touche pas un dossier
    }

    [Theory]
    [InlineData("x", false)]
    [InlineData("x/y", false)]
    [InlineData("x/y", true)]
    [InlineData("x/y/z", false)]
    [InlineData("x/y/z/w", true)]
    public void Copier_les_regles_heritees_ne_change_les_droits_de_personne(string path, bool file)
    {
        var s = States();
        var groups = new[] { "a", "b", "c", "deny" };
        var before = AclEvaluator.Effective(AclInheritance.FullChain(path, s), "u", groups.ToHashSet(), file);

        var own = s.GetValueOrDefault(path)?.Rules ?? [];
        s[path] = new FolderState([.. own, .. AclInheritance.CopyInherited(path, s)], Inherit: false);
        var after = AclEvaluator.Effective(AclInheritance.FullChain(path, s), "u", groups.ToHashSet(), file);
        // Mis à plat, les refus des parents passent avant les autorisations plus proches : on ne peut qu'y perdre des droits, jamais en gagner.
        Assert.Equal(AclRights.None, after & ~before);
    }

    [Fact]
    public void Sans_refus_la_copie_conserve_exactement_les_droits()
    {
        var s = States();
        s[""] = new([G("a", AclApply.Everything)]);
        var groups = new HashSet<string> { "a", "b", "c" };
        foreach (var (path, file) in new[] { ("x", false), ("x/y", true), ("x/y/z", false), ("x/y/z/w", true) })
        {
            var t = States();
            t[""] = s[""];
            var before = AclEvaluator.Effective(AclInheritance.FullChain(path, t), "u", groups, file);
            t[path] = new FolderState([.. t.GetValueOrDefault(path)?.Rules ?? [], .. AclInheritance.CopyInherited(path, t)], false);
            Assert.Equal(before, AclEvaluator.Effective(AclInheritance.FullChain(path, t), "u", groups, file));
        }
    }

    [Fact]
    public void Retirer_l_heritage_supprime_les_droits_venant_du_parent()
    {
        var s = States();
        s["x"] = s["x"] with { Inherit = false };
        var r = AclEvaluator.Effective(AclInheritance.FullChain("x", s), "u", new HashSet<string> { "a" }, false);
        Assert.Equal(AclRights.None, r);
    }

    [Fact]
    public void Un_dossier_au_nom_dangereux_est_refuse_a_la_creation()
    {
        Assert.Null(AclRules.ValidateNewFolderName("Factures 2026"));
        Assert.NotNull(AclRules.ValidateNewFolderName("a;rmdir x"));
        Assert.NotNull(AclRules.ValidateNewFolderName("a/b"));
        Assert.NotNull(AclRules.ValidateNewFolderName(".."));
        Assert.NotNull(AclRules.ValidateNewFolderName(""));
    }
}

static class RightsExt { public static AclRights Modify(this AclRights _) => AclRights.Read | AclRights.Write; }
