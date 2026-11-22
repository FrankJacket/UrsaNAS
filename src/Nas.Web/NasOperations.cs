using Microsoft.AspNetCore.Components.Authorization;
using Nas.Contracts;
using Nas.Web.Alerting;
using Nas.Web.Auth;
using Nas.Web.Data;
using Nas.Web.Localization;

namespace Nas.Web;

/// <summary>
/// Les opérations de l'interface : valide, demande à l'agent d'agir, enregistre l'état voulu en base puis journalise.
/// L'agent est appelé en premier : si Samba refuse un changement, la base n'est pas modifiée.
/// </summary>
public class NasOperations(AgentClient agent, Repo repo, IMailSender mail, AuthenticationStateProvider auth)
{
    // Statique : partagé par tous les circuits, car l'instance vit le temps d'une session.
    static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>L'administrateur connecté, tel qu'il apparaît dans le journal.</summary>
    async Task<string> ActorAsync() => (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? "unknown";

    // ---- Utilisateurs (le rôle d'administrateur est l'appartenance au groupe « admins »)
    public const int MaxFullNameLength = 100;

    /// <summary>Nom complet et courriel sont facultatifs ; un courriel renseigné doit être une adresse valide.</summary>
    public static string? ValidateProfile(string fullName, string email)
    {
        if (fullName.Length > MaxFullNameLength || fullName.Any(char.IsControl)) return Msg.F("The full name must be at most {0} characters, without control characters.", MaxFullNameLength);
        if (email.Length > 0 && !MailRules.IsEmail(email)) return Msg.F("Invalid email address.");
        return null;
    }

    public async Task CreateUserAsync(string name, string password, bool admin = false, string fullName = "", string email = "")
    {
        fullName = fullName.Trim();
        email = email.Trim();
        name = name.Trim().ToLowerInvariant(); // comptes Linux et Samba : minuscules ; la casse se garde dans le nom complet
        if (Rules.ValidateUserName(name) is { } e1) throw new AgentException(e1);
        if (Rules.ValidatePassword(password) is { } e2) throw new AgentException(e2);
        if (ValidateProfile(fullName, email) is { } e3) throw new AgentException(e3);
        await _gate.WaitAsync();
        try
        {
            await agent.CreateUserAsync(new CreateUserRequest(name, password, admin));
            await repo.SetUserProfileAsync(name, fullName, email);
            await repo.AuditAsync(await ActorAsync(), admin ? "Administrator created" : "User created", name);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Modifie un utilisateur : nom complet, courriel et groupes dont il est membre. « allusers » ne se change pas (tout le monde en est membre).
    /// On ne peut pas se retirer soi-même du groupe « admins » ; l'agent refuse de retirer le dernier administrateur.
    /// </summary>
    public async Task UpdateUserAsync(string name, string fullName, string email, IReadOnlyCollection<string> groups)
    {
        fullName = fullName.Trim();
        email = email.Trim();
        if (ValidateProfile(fullName, email) is { } e) throw new AgentException(e);
        var actor = await ActorAsync();
        await _gate.WaitAsync();
        try
        {
            var all = await agent.GetGroupsAsync();
            var changes = new List<(string Group, List<string> Members)>();
            foreach (var g in all.Where(g => g.Name != AclRules.AllUsersGroup))
            {
                var wanted = groups.Contains(g.Name);
                var current = g.Members.Contains(name);
                if (wanted == current) continue;
                if (g.Name == AclRules.AdminsGroup && !wanted && string.Equals(name, actor, StringComparison.OrdinalIgnoreCase))
                    throw new AgentException(Msg.F("You cannot remove your own administrator role."));
                changes.Add((g.Name, (wanted ? [.. g.Members, name] : g.Members.Where(m => m != name)).ToList()));
            }

            foreach (var (group, members) in changes) await agent.SetGroupMembersAsync(group, members);
            await repo.SetUserProfileAsync(name, fullName, email);
            AdminAccounts.Forget(name);
            await repo.AuditAsync(actor, "User changed", changes.Count == 0 ? name
                : $"{name}: {string.Join(", ", changes.Select(c => (c.Members.Contains(name) ? "+" : "-") + c.Group))}");
        }
        finally { _gate.Release(); }
    }

    public async Task SetPasswordAsync(string name, string password)
    {
        if (Rules.ValidatePassword(password) is { } e) throw new AgentException(e);
        await _gate.WaitAsync();
        try
        {
            await agent.SetPasswordAsync(name, password);
            await repo.AuditAsync(await ActorAsync(), "Password changed", name); // jamais le mot de passe lui-même
            AdminAccounts.Forget(name);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteUserAsync(string name)
    {
        if (string.Equals(name, await ActorAsync(), StringComparison.OrdinalIgnoreCase))
            throw new AgentException(Msg.F("You cannot delete your own account."));
        await _gate.WaitAsync();
        try
        {
            var using_ = (await repo.GetSharesAsync()).Where(s => (!s.Acl && s.ValidUsers.Contains(name)) || s.Ftp?.Users.Contains(name) == true).Select(s => s.Name).ToList();
            if (using_.Count > 0)
                throw new AgentException(Msg.F("“{0}” is used by share {1}: remove it from that share first.", name, string.Join(", ", using_)));
            var ruled = await ShareFoldersUsingAsync(AclSubjectKind.User, name);
            if (ruled.Count > 0)
                throw new AgentException(Msg.F("“{0}” is used by permissions on {1}: remove those permissions first.", name, string.Join(", ", ruled)));
            await agent.DeleteUserAsync(name);
            await repo.DeleteUserProfileAsync(name);
            await repo.AuditAsync(await ActorAsync(), "User deleted", name);
        }
        finally { _gate.Release(); }
    }

    // ---- Partages
    public async Task SaveShareAsync(ShareDef share, bool isNew)
    {
        if (Rules.ValidateShareName(share.Name) is { } e1) throw new AgentException(e1);
        if (Rules.ValidateComment(share.Comment) is { } e2) throw new AgentException(e2);
        if (share.Acl && share.Disk is not null)
        {
            string? fs = null;
            try { fs = Rules.FsTypeOfMount(await agent.GetDisksAsync(), share.Disk); }
            catch (AgentException) { }
            catch (HttpRequestException) { }
            if (!Rules.SupportsAdvancedPermissions(fs)) throw new AgentException(Msg.F("Advanced permissions are not available on a {0} file system: it cannot store them.", fs));
        }
        await _gate.WaitAsync();
        try
        {
            var all = await repo.GetSharesAsync();
            var exists = all.Any(s => s.Name.Equals(share.Name, StringComparison.OrdinalIgnoreCase));
            if (isNew && exists) throw new AgentException(Msg.F("Share “{0}” already exists.", share.Name));
            var next = all.Where(s => !s.Name.Equals(share.Name, StringComparison.OrdinalIgnoreCase)).Append(share)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

            var before = all.FirstOrDefault(s => s.Name.Equals(share.Name, StringComparison.OrdinalIgnoreCase));
            await agent.ApplySharesAsync(next, await repo.GetFtpSettingsAsync());
            await repo.UpsertShareAsync(share);
            await repo.AuditAsync(await ActorAsync(), isNew ? "Share created" : "Share modified", share.Name);

            if (share.Acl && before?.Acl != true) await EnableAclAsync(share, existingData: before is not null);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Passage au mode permissions : ce qui existe déjà reçoit une ACL neutre, puis les utilisateurs autorisés jusque-là
    /// reçoivent les droits qu'ils avaient (modification, ou lecture seule) sur tout le partage, pour ne rien casser.
    /// </summary>
    async Task EnableAclAsync(ShareDef share, bool existingData)
    {
        if (existingData) await agent.ResetAclAsync(share.Name);
        if ((await repo.GetFolderAclsAsync(share.Name)).ContainsKey("")) return;
        var rights = share.ReadOnly ? AclRights.Read : AclRights.Read | AclRights.Write;
        var seed = share.ValidUsers.Select(u => new AclRule(AclSubjectKind.User, u, true, rights, AclApply.Everything)).ToList();
        await agent.SetFolderAclAsync(new SetFolderAclRequest(share.Name, "", seed, []));
        await repo.SetFolderAclAsync(share.Name, "", seed);
        await repo.AuditAsync(await ActorAsync(), "Advanced permissions enabled", share.Name);
    }

    public async Task DeleteShareAsync(string name)
    {
        await _gate.WaitAsync();
        try
        {
            var all = await repo.GetSharesAsync();
            var removed = all.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            await agent.ApplySharesAsync(all.Where(s => s != removed).ToList(), await repo.GetFtpSettingsAsync());
            await repo.DeleteShareAsync(name);
            var detail = removed?.Dataset is { } ds
                ? Msg.F("{0} (files remain in dataset {1})", name, ds)
                : Msg.F("{0} (files remain in {1})", name, $"/srv/nas/{name}");
            await repo.AuditAsync(await ActorAsync(), "Share deleted", detail);
        }
        finally { _gate.Release(); }
    }

    // ---- Groupes (groupes Linux préfixés nasg-) et permissions avancées
    public async Task CreateGroupAsync(string name, IReadOnlyList<string> members)
    {
        name = name.Trim().ToLowerInvariant();
        if (AclRules.ValidateGroupName(name) is { } e) throw new AgentException(e);
        await Audited(() => agent.CreateGroupAsync(new CreateGroupRequest(name, members)), "Group created", name);
    }

    public Task SetGroupMembersAsync(string name, IReadOnlyList<string> members) =>
        Audited(() => agent.SetGroupMembersAsync(name, members), "Group members changed", $"{name}: {string.Join(", ", members)}");

    public async Task DeleteGroupAsync(string name)
    {
        await _gate.WaitAsync();
        try
        {
            var where = await ShareFoldersUsingAsync(AclSubjectKind.Group, name);
            if (where.Count > 0)
                throw new AgentException(Msg.F("“{0}” is used by permissions on {1}: remove those permissions first.", name, string.Join(", ", where)));
            await agent.DeleteGroupAsync(name);
            await repo.AuditAsync(await ActorAsync(), "Group deleted", name);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Dossiers (« partage/chemin ») dont les règles nomment cet utilisateur ou ce groupe.</summary>
    async Task<List<string>> ShareFoldersUsingAsync(AclSubjectKind kind, string name)
    {
        var found = new List<string>();
        foreach (var s in (await repo.GetSharesAsync()).Where(s => s.Acl))
            foreach (var (path, rules) in await repo.GetFolderAclsAsync(s.Name))
                if (rules.Any(r => r.Kind == kind && r.Name == name)) found.Add(path.Length == 0 ? s.Name : $"{s.Name}/{path}");
        return found;
    }

    /// <summary>
    /// Remplace les règles d'un dossier : l'agent écrit l'ACL (héritage compris, sauf rupture) et la propage à ce qui existe déjà.
    /// <paramref name="inherit"/> faux = ce dossier ne reçoit plus les permissions de son parent.
    /// </summary>
    public async Task SaveFolderAclAsync(string share, string path, IReadOnlyList<AclRule> rules, bool? inherit = null)
    {
        if (Rules.ValidateShareName(share) is { } e1) throw new AgentException(e1);
        if (AclRules.ValidateFolderPath(path) is { } e2) throw new AgentException(e2);
        if (AclRules.ValidateRules(rules) is { } e3) throw new AgentException(e3);
        await _gate.WaitAsync();
        try
        {
            await RequireAclShareAsync(share);
            var states = await repo.GetFolderStatesAsync(share);
            var keep = inherit ?? states.GetValueOrDefault(path)?.Inherit ?? true;
            states[path] = new FolderState(rules, keep);
            await agent.SetFolderAclAsync(new SetFolderAclRequest(share, path, rules, AclInheritance.Ancestors(path, states), keep));
            await repo.SetFolderStateAsync(share, path, new FolderState(rules, keep));
            await repo.AuditAsync(await ActorAsync(), inherit switch { false => "Permission inheritance broken", true => "Permission inheritance restored", _ => "Permissions changed" },
                path.Length == 0 ? share : $"{share}/{path}");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Rompt l'héritage d'un dossier ; <paramref name="copy"/> garde les droits actuels en les recopiant comme règles propres.</summary>
    public async Task BreakInheritanceAsync(string share, string path, bool copy)
    {
        if (path.Length == 0) throw new AgentException(Msg.F("The share root has no parent folder."));
        var states = await repo.GetFolderStatesAsync(share);
        var own = states.GetValueOrDefault(path)?.Rules ?? [];
        var rules = copy ? own.Concat(AclInheritance.CopyInherited(path, states)).ToList() : own.ToList();
        await SaveFolderAclAsync(share, path, rules, inherit: false);
    }

    public async Task RestoreInheritanceAsync(string share, string path)
    {
        var states = await repo.GetFolderStatesAsync(share);
        await SaveFolderAclAsync(share, path, states.GetValueOrDefault(path)?.Rules ?? [], inherit: true);
    }

    /// <summary>Crée un sous-dossier par Samba : il reçoit tout seul les permissions héritées de son parent.</summary>
    public async Task CreateFolderAsync(string share, string path, string name)
    {
        if (!IsDisk(share) && Rules.ValidateShareName(share) is { } e1) throw new AgentException(e1);
        if (AclRules.ValidateFolderPath(path) is { } e2) throw new AgentException(e2);
        if (AclRules.ValidateNewFolderName(name) is { } e3) throw new AgentException(e3);
        await _gate.WaitAsync();
        try
        {
            await UsesAclAsync(share);
            await agent.CreateFolderAsync(new CreateFolderRequest(share, path, name));
            await repo.AuditAsync(await ActorAsync(), "Folder created", path.Length == 0 ? $"{share}/{name}" : $"{share}/{path}/{name}");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Un « partage » qui commence par « / » est un disque monté (USB…) : mêmes opérations, sans permissions.</summary>
    public static bool IsDisk(string location) => location.Contains('/'); // un nom de partage n'a jamais de barre

    static string JoinPath(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;
    public static string ParentOf(string path) => path.LastIndexOf('/') is var i and >= 0 ? path[..i] : "";

    static void CheckFolderArgs(string share, string path, string destParent, string newName)
    {
        if (!IsDisk(share) && Rules.ValidateShareName(share) is { } e1) throw new AgentException(e1);
        if (path.Length == 0) throw new AgentException(Msg.F("The share root has no parent folder."));
        if (AclRules.ValidateFolderPath(path) is { } e2) throw new AgentException(e2);
        if (AclRules.ValidateFolderPath(destParent) is { } e3) throw new AgentException(e3);
        if (AclRules.ValidateNewFolderName(newName) is { } e4) throw new AgentException(e4);
    }

    /// <summary>Réécrit l'ACL d'un dossier avec ce que la base sait de lui et de ses parents (propagée à ce qu'il contient).</summary>
    async Task ReapplyFolderAsync(string share, string path, Dictionary<string, FolderState> states)
    {
        var st = states.GetValueOrDefault(path) ?? new FolderState([]);
        await agent.SetFolderAclAsync(new SetFolderAclRequest(share, path, st.Rules, AclInheritance.Ancestors(path, states), st.Inherit));
    }

    /// <summary>
    /// Déplace ou renomme (même parent, autre nom). Les droits propres suivent le dossier ; ceux qu'il héritait
    /// de l'ancien parent laissent la place à ceux du nouveau.
    /// </summary>
    public async Task MoveFolderAsync(string share, string path, string destParent, string newName, bool isFile = false)
    {
        CheckFolderArgs(share, path, destParent, newName);
        await _gate.WaitAsync();
        try
        {
            var acl = await UsesAclAsync(share);
            var newPath = JoinPath(destParent, newName);
            await agent.MoveFolderAsync(new MoveFolderRequest(share, path, destParent, newName, isFile));
            if (!acl)
            {
                await repo.AuditAsync(await ActorAsync(), destParent == ParentOf(path) ? "Folder renamed" : "Folder moved", $"{share}/{path} → {share}/{newPath}");
                return;
            }
            if (!isFile) await repo.RenameFolderStatesAsync(share, path, newPath);
            // Un fichier change de dossier : le dossier d'arrivée repropage ses droits (le fichier perd ceux de l'ancien parent).
            if (destParent != ParentOf(path))
                await ReapplyFolderAsync(share, isFile ? destParent : newPath, await repo.GetFolderStatesAsync(share));
            await repo.AuditAsync(await ActorAsync(), destParent == ParentOf(path) ? "Folder renamed" : "Folder moved", $"{share}/{path} → {share}/{newPath}");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Copie un dossier : la copie garde les règles propres de l'original et reçoit l'héritage de sa destination.</summary>
    public async Task CopyFolderAsync(string share, string path, string destParent, string newName, bool isFile = false)
    {
        CheckFolderArgs(share, path, destParent, newName);
        await _gate.WaitAsync();
        try
        {
            var acl = await UsesAclAsync(share);
            var newPath = JoinPath(destParent, newName);
            await agent.CopyFolderAsync(new CopyFolderRequest(share, path, destParent, newName, isFile));
            if (!acl)
            {
                await repo.AuditAsync(await ActorAsync(), "Folder copied", $"{share}/{path} → {share}/{newPath}");
                return;
            }
            if (isFile)
            {
                await ReapplyFolderAsync(share, destParent, await repo.GetFolderStatesAsync(share)); // le fichier reçoit l'héritage de sa destination
                await repo.AuditAsync(await ActorAsync(), "Folder copied", $"{share}/{path} → {share}/{newPath}");
                return;
            }
            await repo.CopyFolderStatesAsync(share, path, newPath);
            var states = await repo.GetFolderStatesAsync(share);
            // De haut en bas : chaque dossier à règles propres est réécrit après son parent.
            foreach (var p in new[] { newPath }.Concat(states.Keys.Where(k => k.StartsWith(newPath + "/", StringComparison.Ordinal)).OrderBy(k => k.Count(c => c == '/'))))
                await ReapplyFolderAsync(share, p, states);
            await repo.AuditAsync(await ActorAsync(), "Folder copied", $"{share}/{path} → {share}/{newPath}");
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Copie ou déplace vers un autre partage ou un disque monté. Les permissions propres du dossier ne suivent pas : dans un partage à
    /// permissions avancées, l'élément reçoit celles de sa destination ; en quittant un partage, ses règles propres sont oubliées.
    /// </summary>
    public async Task TransferFolderAsync(string share, string path, string destShare, string destParent, string newName, bool isFile, bool copy)
    {
        CheckFolderArgs(share, path, destParent, newName);
        if (!IsDisk(destShare) && Rules.ValidateShareName(destShare) is { } e1) throw new AgentException(e1);
        await _gate.WaitAsync();
        try
        {
            var srcAcl = await UsesAclAsync(share);
            var dstAcl = await UsesAclAsync(destShare);
            var newPath = JoinPath(destParent, newName);
            if (copy) await agent.CopyFolderAsync(new CopyFolderRequest(share, path, destParent, newName, isFile, destShare));
            else await agent.MoveFolderAsync(new MoveFolderRequest(share, path, destParent, newName, isFile, destShare));
            if (!copy && !isFile && srcAcl) await repo.DeleteFolderStatesAsync(share, path);
            if (dstAcl) // le nouvel élément reçoit l'héritage de son nouveau parent
                await ReapplyFolderAsync(destShare, isFile ? destParent : newPath, await repo.GetFolderStatesAsync(destShare));
            await repo.AuditAsync(await ActorAsync(), copy ? "Folder copied" : "Folder moved", $"{share}/{path} → {destShare}/{newPath}");
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteFolderAsync(string share, string path, bool isFile = false)
    {
        if (!IsDisk(share) && Rules.ValidateShareName(share) is { } e1) throw new AgentException(e1);
        if (path.Length == 0) throw new AgentException(Msg.F("The share root cannot be deleted."));
        if (AclRules.ValidateFolderPath(path) is { } e2) throw new AgentException(e2);
        await _gate.WaitAsync();
        try
        {
            var acl = await UsesAclAsync(share);
            await agent.DeleteFolderAsync(share, path, isFile);
            if (!isFile && acl) await repo.DeleteFolderStatesAsync(share, path);
            await repo.AuditAsync(await ActorAsync(), "Folder deleted", $"{share}/{path}");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Le lieu a-t-il des permissions avancées ? Faux pour un disque monté et pour un partage ordinaire (droits Unix de base) ; refuse un partage inconnu.</summary>
    async Task<bool> UsesAclAsync(string location)
    {
        if (IsDisk(location)) return false;
        var share = (await repo.GetSharesAsync()).FirstOrDefault(s => s.Name.Equals(location, StringComparison.OrdinalIgnoreCase))
            ?? throw new AgentException(Msg.F("Share “{0}” not found.", location));
        return share.Acl;
    }

    async Task RequireAclShareAsync(string share)
    {
        if (!(await repo.GetSharesAsync()).Any(s => s.Acl && s.Name.Equals(share, StringComparison.OrdinalIgnoreCase)))
            throw new AgentException(Msg.F("Share “{0}” does not use advanced permissions.", share));
    }

    // ---- ZFS : l'état vit dans ZFS lui-même, la base ne garde que le journal.
    public async Task CreatePoolAsync(string name, string layout, IReadOnlyList<string> disks)
    {
        if (Rules.ValidatePoolName(name) is { } e1) throw new AgentException(e1);
        if (Rules.ValidatePoolLayout(layout, disks.Count) is { } e2) throw new AgentException(e2);
        await Audited(() => agent.CreatePoolAsync(new CreatePoolRequest(name, layout, disks)),
            "Pool created", $"{name} ({layout}: {string.Join(", ", disks)})");
    }

    public async Task DestroyPoolAsync(string name)
    {
        await EnsureNoShareAsync(d => d == name || d.StartsWith(name + "/", StringComparison.Ordinal), Msg.F("pool “{0}”", name));
        await Audited(() => agent.DestroyPoolAsync(name), "Pool destroyed", name);
    }

    /// <summary>Refuse de détruire du stockage qu'un partage utilise encore.</summary>
    async Task EnsureNoShareAsync(Func<string, bool> uses, string what)
    {
        var shares = (await repo.GetSharesAsync()).Where(s => s.Dataset is not null && uses(s.Dataset)).Select(s => s.Name).ToList();
        if (shares.Count > 0)
            throw new AgentException(Msg.F("Cannot delete {0}: share {1} uses it. Delete or move that share first.", what, string.Join(", ", shares)));
    }

    public Task ScrubPoolAsync(string name) =>
        Audited(() => agent.ScrubPoolAsync(name), "Scrub started", name);

    public async Task CreateDatasetAsync(string name)
    {
        if (Rules.ValidateDatasetName(name) is { } e) throw new AgentException(e);
        await Audited(() => agent.CreateDatasetAsync(name), "Dataset created", name);
    }

    public async Task DestroyDatasetAsync(string name)
    {
        await EnsureNoShareAsync(d => d == name || d.StartsWith(name + "/", StringComparison.Ordinal), Msg.F("dataset “{0}”", name));
        await Audited(() => agent.DestroyDatasetAsync(name), "Dataset deleted", name);
    }

    public async Task CreateSnapshotAsync(string dataset, string name)
    {
        if (Rules.ValidateDatasetName(dataset) is { } e1) throw new AgentException(e1);
        if (Rules.ValidateSnapshotName(name) is { } e2) throw new AgentException(e2);
        await Audited(() => agent.CreateSnapshotAsync(dataset, name), "Snapshot created", $"{dataset}@{name}");
    }

    public Task DestroySnapshotAsync(string fullName) =>
        Audited(() => agent.DestroySnapshotAsync(fullName), "Snapshot deleted", fullName);

    // ---- Alertes : réglages du courriel
    public async Task SaveMailSettingsAsync(MailSettings s)
    {
        if (MailRules.Validate(s) is { } e) throw new AgentException(e);
        await repo.SaveMailSettingsAsync(s);
        await repo.AuditAsync(await ActorAsync(), "Email settings changed", s.Enabled ? Msg.F("{0}:{1} to {2}", s.Host, s.Port, string.Join(", ", s.Recipients)) : "sending disabled");
    }

    /// <summary>Envoie un message d'essai avec les réglages enregistrés ; l'échec est expliqué à l'utilisateur.</summary>
    public async Task SendTestMailAsync()
    {
        var s = await repo.GetMailSettingsAsync() with { Enabled = true };
        if (MailRules.Validate(s) is { } e) throw new AgentException(e);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await mail.SendAsync(s, "[UrsaNAS] " + Translator.Translate("Test message", s.Language),
                Translator.Translate("If you are reading this, UrsaNAS alerts can email you.", s.Language), cts.Token);
        }
        catch (Exception ex) when (ex is not AgentException)
        {
            throw new AgentException(Msg.F("Sending failed: {0}", ex.Message));
        }
        await repo.AuditAsync(await ActorAsync(), "Test email sent", string.Join(", ", s.Recipients));
    }

    async Task Audited(Func<Task> action, string what, string detail)
    {
        await _gate.WaitAsync();
        try
        {
            await action();
            await repo.AuditAsync(await ActorAsync(), what, detail);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Renvoie à l'agent la définition enregistrée (après une réinstallation de l'agent, par exemple).</summary>
    public async Task ReapplySharesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await agent.ApplySharesAsync(await repo.GetSharesAsync(), await repo.GetFtpSettingsAsync());
            await repo.AuditAsync(await ActorAsync(), "Shares reapplied");
        }
        finally { _gate.Release(); }
    }

    // ---- Services réseau (SMB, NFS, FTP)

    /// <summary>
    /// Active ou arrête un service. Avant de démarrer NFS ou FTP, leur configuration est écrite : sinon le service démarrerait
    /// avec celle du paquet (pour vsftpd, une configuration qui n'est pas la nôtre).
    /// </summary>
    public async Task SetServiceAsync(string name, bool enabled)
    {
        await _gate.WaitAsync();
        try
        {
            if (enabled) await agent.ApplySharesAsync(await repo.GetSharesAsync(), await repo.GetFtpSettingsAsync());
            await agent.SetServiceAsync(name, enabled);
            await repo.AuditAsync(await ActorAsync(), enabled ? "Service enabled" : "Service disabled", name.ToUpperInvariant());
        }
        finally { _gate.Release(); }
    }

    public async Task SaveFtpSettingsAsync(FtpSettings settings)
    {
        if (Rules.ValidateFtpSettings(settings) is { } e) throw new AgentException(e);
        await _gate.WaitAsync();
        try
        {
            await agent.ApplySharesAsync(await repo.GetSharesAsync(), settings);
            await repo.SaveFtpSettingsAsync(settings);
            await repo.AuditAsync(await ActorAsync(), "FTP settings changed");
        }
        finally { _gate.Release(); }
    }
}
