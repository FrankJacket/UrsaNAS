using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace Nas.Web.Auth;

/// <summary>
/// Les pages interactives vivent dans un circuit qui survit au cookie : toutes les minutes on vérifie que le compte existe encore
/// et que son mot de passe n'a pas changé, sinon la session passe à « non connecté ».
/// </summary>
public sealed class AdminRevalidatingStateProvider(ILoggerFactory loggerFactory, IServiceScopeFactory scopes)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AdminAccounts>().IsStillValidAsync(authenticationState.User);
    }
}

/// <summary>Redirection après connexion : uniquement vers une adresse locale (jamais vers un autre site).</summary>
public static class LocalUrl
{
    public static string Safe(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal) && !url.Any(char.IsControl) ? url : "/";
}
