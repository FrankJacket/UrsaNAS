using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>
/// Les opérations de l'interface : valide, demande à l'agent d'agir, enregistre l'état voulu en base puis journalise.
/// L'agent est appelé en premier : si Samba refuse un changement, la base n'est pas modifiée.
/// </summary>
public class NasOperations(AgentClient agent, Repo repo)
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
            var next = (await repo.GetSharesAsync()).Where(s => !s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            await agent.ApplySharesAsync(next);
            await repo.DeleteShareAsync(name);
            await repo.AuditAsync(Actor, "Partage supprimé", $"{name} (les fichiers restent dans /srv/nas/{name})");
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
