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
public class NasOperations(AgentClient agent, Repo repo, IMailSender mail, AdminAccounts admins, AuthenticationStateProvider auth)
{
    // Statique : partagé par tous les circuits, car l'instance vit le temps d'une session.
    static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>L'administrateur connecté, tel qu'il apparaît dans le journal.</summary>
    async Task<string> ActorAsync() => (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? "unknown";

    // ---- Administrateurs de l'interface
    public async Task CreateAdminAsync(string name, string password)
    {
        await admins.CreateAsync(name, password);
        await repo.AuditAsync(await ActorAsync(), "Administrator created", name);
    }

    public async Task SetAdminPasswordAsync(string name, string password)
    {
        await admins.SetPasswordAsync(name, password);
        await repo.AuditAsync(await ActorAsync(), "Administrator password changed", name); // jamais le mot de passe lui-même
    }

    public async Task DeleteAdminAsync(string name)
    {
        var actor = await ActorAsync();
        await admins.DeleteAsync(name, actor);
        await repo.AuditAsync(actor, "Administrator deleted", name);
    }

    // ---- Utilisateurs
    public async Task CreateUserAsync(string name, string password)
    {
        if (Rules.ValidateUserName(name) is { } e1) throw new AgentException(e1);
        if (Rules.ValidatePassword(password) is { } e2) throw new AgentException(e2);
        await _gate.WaitAsync();
        try
        {
            await agent.CreateUserAsync(new CreateUserRequest(name, password));
            await repo.AuditAsync(await ActorAsync(), "User created", name);
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
                throw new AgentException(Msg.F("“{0}” is used by share {1}: remove it from that share first.", name, string.Join(", ", using_)));
            await agent.DeleteUserAsync(name);
            await repo.AuditAsync(await ActorAsync(), "User deleted", name);
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
            if (isNew && exists) throw new AgentException(Msg.F("Share “{0}” already exists.", share.Name));
            var next = all.Where(s => !s.Name.Equals(share.Name, StringComparison.OrdinalIgnoreCase)).Append(share)
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

            await agent.ApplySharesAsync(next);
            await repo.UpsertShareAsync(share);
            await repo.AuditAsync(await ActorAsync(), isNew ? "Share created" : "Share modified", share.Name);
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
            var detail = removed?.Dataset is { } ds
                ? Msg.F("{0} (files remain in dataset {1})", name, ds)
                : Msg.F("{0} (files remain in {1})", name, $"/srv/nas/{name}");
            await repo.AuditAsync(await ActorAsync(), "Share deleted", detail);
        }
        finally { _gate.Release(); }
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
            await agent.ApplySharesAsync(await repo.GetSharesAsync());
            await repo.AuditAsync(await ActorAsync(), "Shares reapplied");
        }
        finally { _gate.Release(); }
    }
}
