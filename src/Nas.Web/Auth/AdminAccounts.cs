using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web.Auth;

/// <summary>
/// Comptes qui peuvent se connecter à l'interface (distincts des utilisateurs Samba). Mot de passe haché (PBKDF2),
/// jamais stocké ni journalisé en clair. Le « tampon » change à chaque changement de mot de passe : les sessions ouvertes
/// avec l'ancien deviennent invalides.
/// </summary>
public sealed class AdminAccounts(Repo repo)
{
    public const string StampClaim = "ursanas:stamp";
    static readonly PasswordHasher<string> Hasher = new();
    // Haché une fois pour que « compte inconnu » coûte le même temps que « mauvais mot de passe ».
    static readonly string DummyHash = Hasher.HashPassword("", Guid.NewGuid().ToString("N"));

    public async Task<AdminRow?> ValidateAsync(string? name, string? password)
    {
        name = (name ?? "").Trim().ToLowerInvariant();
        var row = name.Length == 0 ? null : await repo.GetAdminAsync(name);
        if (row is null)
        {
            Hasher.VerifyHashedPassword("", DummyHash, password ?? "");
            return null;
        }
        var result = Hasher.VerifyHashedPassword(row.Name, row.PasswordHash, password ?? "");
        if (result == PasswordVerificationResult.Failed) return null;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            await repo.UpdateAdminPasswordAsync(row.Name, Hasher.HashPassword(row.Name, password!), row.Stamp);
        return row;
    }

    public static ClaimsPrincipal PrincipalFor(AdminRow row) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, row.Name), new Claim(StampClaim, row.Stamp)],
            "ursanas-cookie", ClaimTypes.Name, ClaimTypes.Role));

    /// <summary>Le compte existe toujours et son mot de passe n'a pas changé depuis l'ouverture de la session.</summary>
    public async Task<bool> IsStillValidAsync(ClaimsPrincipal user)
    {
        var name = user.Identity?.Name;
        var stamp = user.FindFirstValue(StampClaim);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(stamp)) return false;
        var row = await repo.GetAdminAsync(name);
        return row is not null && row.Stamp == stamp;
    }

    public Task<int> CountAsync() => repo.CountAdminsAsync();
    public Task<List<AdminRow>> ListAsync() => repo.ListAdminsAsync();

    public async Task CreateAsync(string name, string password)
    {
        name = (name ?? "").Trim();
        if (Rules.ValidateUserName(name) is { } e1) throw new AgentException(e1);
        if (Rules.ValidatePassword(password) is { } e2) throw new AgentException(e2);
        if (await repo.GetAdminAsync(name) is not null) throw new AgentException($"Le compte « {name} » existe déjà.");
        await repo.InsertAdminAsync(name, Hasher.HashPassword(name, password), NewStamp());
    }

    public async Task SetPasswordAsync(string name, string password)
    {
        if (Rules.ValidatePassword(password) is { } e) throw new AgentException(e);
        var row = await repo.GetAdminAsync(name) ?? throw new AgentException($"Le compte « {name} » n'existe pas.");
        await repo.UpdateAdminPasswordAsync(row.Name, Hasher.HashPassword(row.Name, password), NewStamp());
    }

    /// <summary>Supprime un compte, sauf le dernier et celui de la personne qui agit.</summary>
    public async Task DeleteAsync(string name, string actor)
    {
        if (string.Equals(name, actor, StringComparison.OrdinalIgnoreCase))
            throw new AgentException("Tu ne peux pas supprimer ton propre compte.");
        if (await repo.GetAdminAsync(name) is null) throw new AgentException($"Le compte « {name} » n'existe pas.");
        if (await repo.CountAdminsAsync() <= 1) throw new AgentException("Impossible de supprimer le dernier administrateur.");
        await repo.DeleteAdminAsync(name);
    }

    static string NewStamp() => Guid.NewGuid().ToString("N");
}
