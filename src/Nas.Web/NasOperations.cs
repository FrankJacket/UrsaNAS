using Nas.Contracts;
using Nas.Web.Alerting;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>
/// Les opérations de l'interface : valide, demande à l'agent d'agir, enregistre l'état voulu en base puis journalise.
/// L'agent est appelé en premier : si Samba refuse un changement, la base n'est pas modifiée.
/// </summary>
public class NasOperations(AgentClient agent, Repo repo, IMailSender mail)
{
    // Statique : partagé par tous les circuits, car l'instance vit le temps d'une session.
    static readonly SemaphoreSlim _gate = new(1, 1);
    const string Actor = "admin"; // pas encore d'authentification : une seule identité

    // ---- Utilisateurs
    public async Task CreateUserAsync(string name, string password)
    {
        if (Rules.ValidateUserName(name) is { } e1) throw new AgentException(e1);
        if (Rules.ValidatePassword(password) is { } e2) throw new AgentException(e2);
        await _gate.WaitAsync();
        try
        {
            await agent.CreateUserAsync(new CreateUserRequest(name, password));
            await repo.AuditAsync(Actor, "Utilisateur créé", name);
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
            await repo.AuditAsync(Actor, "Mot de passe changé", name); // jamais le mot de passe lui-même
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteUserAsync(string name)
    {
        await _gate.WaitAsync();
        try
        {
            var using_ = (await repo.GetSharesAsync()).Where(s => s.ValidUsers.Contains(name)).Select(s => s.Name).ToList();
            if (using_.Count > 0)
                throw new AgentException($"« {name} » est utilisé par le partage {string.Join(", ", using_)} : retire-le d'abord de ce partage.");
            await agent.DeleteUserAsync(name);
            await repo.AuditAsync(Actor, "Utilisateur supprimé", name);
        }
        finally { _gate.Release(); }
    }

    // ---- Partages
    public async Task SaveShareAsync(ShareDef share, bool isNew)
    {
        if (Rules.ValidateShareName(share.Name) is { } e1) throw new AgentException(e1);
        if (Rules.ValidateComment(share.Comment) is { } e2) throw new AgentException(e2);
        await _gate.WaitAsync();
        try
        {
            var all = await repo.GetSharesAsync();
            var exists = all.Any(s => s.Name.Equals(share.Name, StringComparison.OrdinalIgnoreCase));
            if (isNew && exists) throw new AgentException($"Le partage « {share.Name} » existe déjà.");
            var next = all.Where(s => !s.Name.Equals(share.Name, StringComparison.OrdinalIgnoreCase)).Append(share)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

            await agent.ApplySharesAsync(next);
            await repo.UpsertShareAsync(share);
            await repo.AuditAsync(Actor, isNew ? "Partage créé" : "Partage modifié", share.Name);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteShareAsync(string name)
    {
        await _gate.WaitAsync();
        try
        {
            var all = await repo.GetSharesAsync();
            var removed = all.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            await agent.ApplySharesAsync(all.Where(s => s != removed).ToList());
            await repo.DeleteShareAsync(name);
            var where = removed?.Dataset is { } ds ? $"les fichiers restent dans le jeu de données {ds}" : $"les fichiers restent dans /srv/nas/{name}";
            await repo.AuditAsync(Actor, "Partage supprimé", $"{name} ({where})");
        }
        finally { _gate.Release(); }
    }

    // ---- ZFS : l'état vit dans ZFS lui-même, la base ne garde que le journal.
    public async Task CreatePoolAsync(string name, string layout, IReadOnlyList<string> disks)
    {
        if (Rules.ValidatePoolName(name) is { } e1) throw new AgentException(e1);
        if (Rules.ValidatePoolLayout(layout, disks.Count) is { } e2) throw new AgentException(e2);
        await Audited(() => agent.CreatePoolAsync(new CreatePoolRequest(name, layout, disks)),
            "Pool créé", $"{name} ({layout} : {string.Join(", ", disks)})");
    }

    public async Task DestroyPoolAsync(string name)
    {
        await EnsureNoShareAsync(d => d == name || d.StartsWith(name + "/", StringComparison.Ordinal), $"le pool « {name} »");
        await Audited(() => agent.DestroyPoolAsync(name), "Pool détruit", name);
    }

    /// <summary>Refuse de détruire du stockage qu'un partage utilise encore.</summary>
    async Task EnsureNoShareAsync(Func<string, bool> uses, string what)
    {
        var shares = (await repo.GetSharesAsync()).Where(s => s.Dataset is not null && uses(s.Dataset)).Select(s => s.Name).ToList();
        if (shares.Count > 0)
            throw new AgentException($"Impossible de supprimer {what} : le partage {string.Join(", ", shares)} l'utilise. Supprime ou déplace d'abord ce partage.");
    }

    public Task ScrubPoolAsync(string name) =>
        Audited(() => agent.ScrubPoolAsync(name), "Vérification lancée", name);

    public async Task CreateDatasetAsync(string name)
    {
        if (Rules.ValidateDatasetName(name) is { } e) throw new AgentException(e);
        await Audited(() => agent.CreateDatasetAsync(name), "Jeu de données créé", name);
    }

    public async Task DestroyDatasetAsync(string name)
    {
        await EnsureNoShareAsync(d => d == name || d.StartsWith(name + "/", StringComparison.Ordinal), $"le jeu de données « {name} »");
        await Audited(() => agent.DestroyDatasetAsync(name), "Jeu de données supprimé", name);
    }

    public async Task CreateSnapshotAsync(string dataset, string name)
    {
        if (Rules.ValidateDatasetName(dataset) is { } e1) throw new AgentException(e1);
        if (Rules.ValidateSnapshotName(name) is { } e2) throw new AgentException(e2);
        await Audited(() => agent.CreateSnapshotAsync(dataset, name), "Instantané créé", $"{dataset}@{name}");
    }

    public Task DestroySnapshotAsync(string fullName) =>
        Audited(() => agent.DestroySnapshotAsync(fullName), "Instantané supprimé", fullName);

    // ---- Alertes : réglages du courriel
    public async Task SaveMailSettingsAsync(MailSettings s)
    {
        if (MailRules.Validate(s) is { } e) throw new AgentException(e);
        await repo.SaveMailSettingsAsync(s);
        await repo.AuditAsync(Actor, "Réglages e-mail modifiés", s.Enabled ? $"{s.Host}:{s.Port} vers {string.Join(", ", s.Recipients)}" : "envoi désactivé");
    }

    /// <summary>Envoie un message d'essai avec les réglages enregistrés ; l'échec est expliqué à l'utilisateur.</summary>
    public async Task SendTestMailAsync()
    {
        var s = await repo.GetMailSettingsAsync() with { Enabled = true };
        if (MailRules.Validate(s) is { } e) throw new AgentException(e);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await mail.SendAsync(s, "[UrsaNAS] Message d'essai", "Si tu lis ce message, les alertes UrsaNAS peuvent t'écrire.", cts.Token);
        }
        catch (Exception ex) when (ex is not AgentException)
        {
            throw new AgentException($"Échec de l'envoi : {ex.Message}");
        }
        await repo.AuditAsync(Actor, "E-mail d'essai envoyé", string.Join(", ", s.Recipients));
    }

    async Task Audited(Func<Task> action, string what, string detail)
    {
        await _gate.WaitAsync();
        try
        {
            await action();
            await repo.AuditAsync(Actor, what, detail);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Renvoie à l'agent la définition enregistrée (après une réinstallation de l'agent, par exemple).</summary>
    public async Task ReapplySharesAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await agent.ApplySharesAsync(await repo.GetSharesAsync());
            await repo.AuditAsync(Actor, "Partages réappliqués");
        }
        finally { _gate.Release(); }
    }
}
