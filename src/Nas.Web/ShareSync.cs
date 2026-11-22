using Nas.Web.Data;

namespace Nas.Web;

/// <summary>
/// Réapplique les partages à chaque démarrage de l'interface. La configuration Samba est générée à partir de la base : après une mise à jour qui
/// change ce qu'on y écrit (ici, l'accès complet des administrateurs), les partages existants la reçoivent sans que personne ait à les modifier.
/// L'opération est sans danger (les dossiers existants ne sont pas touchés) ; si l'agent n'est pas encore prêt, on réessaie quelques fois.
/// </summary>
public sealed class ShareSync(IServiceScopeFactory scopes, ILogger<ShareSync> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 6 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt == 0 ? 5 : 15), ct);
                using var scope = scopes.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<Repo>();
                var shares = await repo.GetSharesAsync();
                if (shares.Count == 0) return;
                await scope.ServiceProvider.GetRequiredService<AgentClient>().ApplySharesAsync(shares, await repo.GetFtpSettingsAsync(), ct);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is AgentException or HttpRequestException)
            {
                log.LogWarning("Shares could not be reapplied at startup (attempt {Attempt}): {Message}", attempt + 1, e.Message);
            }
        }
    }
}
