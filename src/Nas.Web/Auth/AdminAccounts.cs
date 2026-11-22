using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web.Auth;

/// <summary>Ce dont la connexion a besoin des comptes du NAS (l'agent, qui garde les utilisateurs et leurs groupes).</summary>
public interface IUserDirectory
{
    Task<UserSession> VerifyAsync(string name, string password, CancellationToken ct = default);
    Task<UserSession> SessionAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<NasUser>> ListAsync(CancellationToken ct = default);
    Task CreateAsync(string name, string password, bool admin, CancellationToken ct = default);
    Task SetAdminAsync(string name, bool admin, CancellationToken ct = default);
    Task SetPasswordAsync(string name, string password, CancellationToken ct = default);
}

public sealed class AgentUserDirectory(AgentClient agent) : IUserDirectory
{
    public Task<UserSession> VerifyAsync(string name, string password, CancellationToken ct = default) => agent.VerifyUserAsync(name, password, ct);
    public Task<UserSession> SessionAsync(string name, CancellationToken ct = default) => agent.GetUserSessionAsync(name, ct);
    public Task<IReadOnlyList<NasUser>> ListAsync(CancellationToken ct = default) => agent.GetUsersAsync(ct);
    public Task CreateAsync(string name, string password, bool admin, CancellationToken ct = default) => agent.CreateUserAsync(new CreateUserRequest(name, password, admin), ct);
    public Task SetAdminAsync(string name, bool admin, CancellationToken ct = default) => agent.SetUserAdminAsync(name, admin, ct);
    public Task SetPasswordAsync(string name, string password, CancellationToken ct = default) => agent.SetPasswordAsync(name, password, ct);
}

/// <summary>Résultat d'une tentative de connexion : un compte, ou la raison du refus.</summary>
public sealed record SignIn(string? Name, string? Stamp, bool NotAdmin = false)
{
    public bool Ok => Name is not null;
}

/// <summary>
/// Connexion à l'interface : les administrateurs sont les utilisateurs du NAS qui sont membres du groupe « admins ». Le mot de passe est
/// celui de leur compte (vérifié par l'agent contre Samba) : il n'y a plus de compte séparé. Le « tampon » change à chaque changement de
/// mot de passe : les sessions ouvertes avec l'ancien deviennent invalides.
/// Les administrateurs d'une ancienne version (table AdminUsers) sont convertis en utilisateurs à leur première connexion, seul moment
/// où l'on dispose de leur mot de passe.
/// </summary>
public sealed class AdminAccounts(Repo repo, IUserDirectory users, TimeProvider? clock = null)
{
    public const string StampClaim = "ursanas:stamp";
    static readonly PasswordHasher<string> Hasher = new();
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    // Une vérification par requête serait lourde (une commande par appel) : on retient la réponse de l'agent quelques secondes.
    static readonly ConcurrentDictionary<string, (DateTimeOffset At, UserSession Session)> Sessions = new();
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<SignIn> ValidateAsync(string? name, string? password, CancellationToken ct = default)
    {
        name = (name ?? "").Trim().ToLowerInvariant();
        password ??= "";
        if (name.Length == 0 || password.Length == 0) return new SignIn(null, null);

        var session = await users.VerifyAsync(name, password, ct);
        if (!session.Valid && await MigrateLegacyAsync(name, password, ct))
            session = await users.VerifyAsync(name, password, ct);

        if (!session.Valid) return new SignIn(null, null);
        if (!session.Admin) return new SignIn(null, null, NotAdmin: true);
        return new SignIn(name, session.Stamp);
    }

    /// <summary>Ancien administrateur de l'interface : crée l'utilisateur correspondant (ou, s'il existe déjà, lui donne ce mot de passe et le rôle).</summary>
    async Task<bool> MigrateLegacyAsync(string name, string password, CancellationToken ct)
    {
        var row = await repo.GetAdminAsync(name);
        if (row is null || Hasher.VerifyHashedPassword(row.Name, row.PasswordHash, password) == PasswordVerificationResult.Failed) return false;

        var exists = (await users.ListAsync(ct)).Any(u => u.Name == row.Name.ToLowerInvariant());
        if (exists)
        {
            await users.SetPasswordAsync(row.Name.ToLowerInvariant(), password, ct);
            await users.SetAdminAsync(row.Name.ToLowerInvariant(), true, ct);
        }
        else await users.CreateAsync(row.Name.ToLowerInvariant(), password, admin: true, ct);

        await repo.DeleteAdminAsync(row.Name);
        await repo.AuditAsync("system", "Administrator converted to user", row.Name);
        return true;
    }

    public static ClaimsPrincipal PrincipalFor(SignIn s) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, s.Name!), new Claim(StampClaim, s.Stamp!)],
            "ursanas-cookie", ClaimTypes.Name, ClaimTypes.Role));

    /// <summary>Le compte existe toujours, est toujours administrateur, et son mot de passe n'a pas changé depuis l'ouverture de la session.
    /// Si l'agent ne répond pas (redémarrage), la session est conservée : on ne déconnecte personne pour une panne passagère.</summary>
    public async Task<bool> IsStillValidAsync(ClaimsPrincipal user)
    {
        var name = user.Identity?.Name;
        var stamp = user.FindFirstValue(StampClaim);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(stamp)) return false;

        var now = _clock.GetUtcNow();
        if (!Sessions.TryGetValue(name, out var cached) || now - cached.At > CacheFor)
        {
            try { cached = (now, await users.SessionAsync(name)); }
            catch (Exception e) when (e is AgentException or HttpRequestException or TaskCanceledException) { return true; }
            Sessions[name] = cached;
        }
        return cached.Session.Valid && cached.Session.Admin && cached.Session.Stamp == stamp;
    }

    /// <summary>Oublie ce qui est retenu pour ce compte (mot de passe ou rôle modifié : la prochaine vérification relit l'agent).</summary>
    public static void Forget(string name) => Sessions.TryRemove(name, out _);

    /// <summary>Faut-il créer le premier administrateur ? Vrai quand aucun utilisateur n'est administrateur et qu'aucun ancien compte n'existe.</summary>
    public async Task<bool> NeedsSetupAsync(CancellationToken ct = default)
    {
        if (await repo.CountAdminsAsync() > 0) return false;
        try { return !(await users.ListAsync(ct)).Any(u => u.Admin); }
        catch (Exception e) when (e is AgentException or HttpRequestException or TaskCanceledException) { return false; } // agent muet : la page de connexion l'expliquera
    }

    /// <summary>Crée le premier administrateur : un utilisateur du NAS membre du groupe « admins ».</summary>
    public async Task CreateFirstAsync(string name, string password, CancellationToken ct = default)
    {
        name = (name ?? "").Trim().ToLowerInvariant();
        if (Rules.ValidateUserName(name) is { } e1) throw new AgentException(e1);
        if (Rules.ValidatePassword(password) is { } e2) throw new AgentException(e2);
        if ((await users.ListAsync(ct)).Any(u => u.Name == name))
            throw new AgentException(Msg.F("User “{0}” already exists.", name));
        await users.CreateAsync(name, password, admin: true, ct);
    }
}
