using System.Net.Http.Headers;
using System.Text.Json;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>Retrouve l'identifiant et le type du disque OneDrive d'un compte : <c>rclone</c> en a besoin pour s'y connecter.</summary>
public interface IDriveLookup
{
    Task<(string Id, string Type)> OneDriveAsync(string accessToken, CancellationToken ct);
}

/// <summary>Interroge Microsoft Graph (<c>/me/drive</c>) avec le jeton que l'utilisateur vient d'obtenir.</summary>
public sealed class GraphDriveLookup : IDriveLookup
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<(string Id, string Type)> OneDriveAsync(string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/drive");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var r = await Http.SendAsync(req, ct);
            if (!r.IsSuccessStatusCode)
                throw new OperationException(Msg.F("OneDrive refused the access token (answer {0}). Run “rclone authorize” again and paste the new result.", (int)r.StatusCode));
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            var id = doc.RootElement.TryGetProperty("id", out var i) ? i.GetString() : null;
            var type = doc.RootElement.TryGetProperty("driveType", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type)) throw new OperationException(Msg.F("OneDrive did not return the drive of this account."));
            return (id, type);
        }
        catch (HttpRequestException) { throw new OperationException(Msg.F("OneDrive cannot be reached from the NAS.")); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new OperationException(Msg.F("OneDrive cannot be reached from the NAS.")); }
        catch (JsonException) { throw new OperationException(Msg.F("OneDrive did not return the drive of this account.")); }
    }
}

/// <summary>Mode développement (URSANAS_FAKE=1) : aucun appel réseau.</summary>
public sealed class FakeDriveLookup : IDriveLookup
{
    public Task<(string Id, string Type)> OneDriveAsync(string accessToken, CancellationToken ct) => Task.FromResult(("fake-drive-id", "personal"));
}
