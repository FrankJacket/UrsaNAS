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

    // ---- ZFS
    public Task<IReadOnlyList<PoolInfo>> GetPoolsAsync(CancellationToken ct = default) => GetListAsync<PoolInfo>("zfs/pools", ct);
    public Task<IReadOnlyList<DatasetInfo>> GetDatasetsAsync(CancellationToken ct = default) => GetListAsync<DatasetInfo>("zfs/datasets", ct);
    public Task<IReadOnlyList<SnapshotInfo>> GetSnapshotsAsync(CancellationToken ct = default) => GetListAsync<SnapshotInfo>("zfs/snapshots", ct);

    public async Task CreatePoolAsync(CreatePoolRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("zfs/pools", req, ct), ct);

    public async Task DestroyPoolAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync($"zfs/pools/{Uri.EscapeDataString(name)}", ct), ct);

    public async Task ScrubPoolAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsync($"zfs/pools/{Uri.EscapeDataString(name)}/scrub", null, ct), ct);

    public async Task CreateDatasetAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("zfs/datasets", new CreateDatasetRequest(name), ct), ct);

    public async Task DestroyDatasetAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync($"zfs/datasets?name={Uri.EscapeDataString(name)}", ct), ct);

    public async Task CreateSnapshotAsync(string dataset, string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("zfs/snapshots", new CreateSnapshotRequest(dataset, name), ct), ct);

    public async Task DestroySnapshotAsync(string fullName, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync($"zfs/snapshots?name={Uri.EscapeDataString(fullName)}", ct), ct);

    /// <summary>GET d'une liste ; une erreur de l'agent (ZFS absent, commande en échec) devient une AgentException lisible.</summary>
    async Task<IReadOnlyList<T>> GetListAsync<T>(string url, CancellationToken ct)
    {
        using var r = await http.GetAsync(url, ct);
        await EnsureOkAsync(r, ct);
        return await r.Content.ReadFromJsonAsync<List<T>>(ct) ?? [];
    }

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
