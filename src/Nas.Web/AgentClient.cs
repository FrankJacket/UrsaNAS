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

    // ---- Groupes et permissions avancées
    public Task<IReadOnlyList<NasGroup>> GetGroupsAsync(CancellationToken ct = default) => GetListAsync<NasGroup>("groups", ct);

    public async Task CreateGroupAsync(CreateGroupRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("groups", req, ct), ct);

    public async Task SetGroupMembersAsync(string name, IReadOnlyList<string> members, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PutAsJsonAsync($"groups/{Uri.EscapeDataString(name)}/members", new SetGroupMembersRequest(members), ct), ct);

    public async Task DeleteGroupAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync($"groups/{Uri.EscapeDataString(name)}", ct), ct);

    public async Task<FolderListing> GetFoldersAsync(string share, string path, bool files = false, CancellationToken ct = default)
    {
        using var r = await http.GetAsync($"acl/folders?share={Uri.EscapeDataString(share)}&path={Uri.EscapeDataString(path)}&files={(files ? "true" : "false")}", ct);
        await EnsureOkAsync(r, ct);
        return await r.Content.ReadFromJsonAsync<FolderListing>(ct) ?? new FolderListing(path, []);
    }

    public async Task SetFolderAclAsync(SetFolderAclRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PutAsJsonAsync("acl/folder", req, ct), ct);

    public async Task CreateFolderAsync(CreateFolderRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("acl/folder", req, ct), ct);

    public async Task ResetAclAsync(string share, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("acl/reset", new ResetAclRequest(share), ct), ct);

    public async Task MoveFolderAsync(MoveFolderRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("acl/folder/move", req, ct), ct);

    public async Task CopyFolderAsync(CopyFolderRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("acl/folder/copy", req, ct), ct);

    public async Task DeleteFolderAsync(string share, string path, bool isFile = false, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync($"acl/folder?share={Uri.EscapeDataString(share)}&path={Uri.EscapeDataString(path)}&file={(isFile ? "true" : "false")}", ct), ct);

    public async Task<SystemStats> GetStatsAsync(CancellationToken ct = default)
    {
        using var r = await http.GetAsync("stats", ct);
        await EnsureOkAsync(r, ct);
        return await r.Content.ReadFromJsonAsync<SystemStats>(ct) ?? throw new AgentException(Msg.F("The agent answered {0}.", 204));
    }

    public Task<IReadOnlyList<SmartInfo>> GetSmartAsync(CancellationToken ct = default) => GetListAsync<SmartInfo>("smart", ct);

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
        throw new AgentException(detail ?? Msg.F("The agent answered {0}.", (int)r.StatusCode));
    }
}
