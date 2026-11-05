using System.Text.Json;
using Nas.Contracts;

namespace Nas.Web;

/// <summary>Refus ou échec renvoyé par l'agent ; le message est destiné à l'utilisateur.</summary>
public sealed class AgentException(string message) : Exception(message);

/// <summary>Appels vers l'agent privilégié (socket Unix). Aucun accès direct au système depuis l'interface.</summary>
public class AgentClient(HttpClient http)
{
    public async Task<IReadOnlyList<DiskInfo>> GetDisksAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<DiskInfo>>("disks", ct) ?? [];

    public async Task<IReadOnlyList<NasUser>> GetUsersAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<NasUser>>("users", ct) ?? [];

    public async Task CreateUserAsync(CreateUserRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("users", req, ct), ct);

    public async Task SetPasswordAsync(string name, string password, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PutAsJsonAsync($"users/{Uri.EscapeDataString(name)}/password", new SetPasswordRequest(password), ct), ct);

    public async Task DeleteUserAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync($"users/{Uri.EscapeDataString(name)}", ct), ct);

    public async Task ApplySharesAsync(IReadOnlyList<ShareDef> shares, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PutAsJsonAsync("shares", new ApplySharesRequest(shares), ct), ct);

    /// <summary>Transforme une réponse d'erreur de l'agent (ProblemDetails) en exception lisible.</summary>
    static async Task EnsureOkAsync(HttpResponseMessage r, CancellationToken ct)
    {
        if (r.IsSuccessStatusCode) return;
        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("detail", out var d)) detail = d.GetString();
        }
        catch (JsonException) { }
        throw new AgentException(detail ?? $"L'agent a répondu {(int)r.StatusCode}.");
    }
}
