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

    public async Task<IReadOnlyList<string>> GetFilesystemsAsync(CancellationToken ct = default)
    {
        using var r = await http.GetAsync("disks/filesystems", ct);
        await EnsureOkAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<FsOptions>(ct))?.Available ?? [];
    }

    public async Task PrepareDiskAsync(PrepareDiskRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("disks/prepare", req, ct), ct);

    public async Task CreatePartitionAsync(CreatePartitionRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("disks/partitions", req, ct), ct);

    public async Task WipeDiskAsync(WipeDiskRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("disks/wipe", req, ct), ct);

    public async Task GrowSystemDiskAsync(CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsync("disks/grow-system", null, ct), ct);

    public async Task FormatPartitionAsync(FormatPartitionRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("partitions/format", req, ct), ct);

    public async Task MountPartitionAsync(MountPartitionRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("partitions/mount", req, ct), ct);

    public async Task UnmountPartitionAsync(UnmountRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("partitions/unmount", req, ct), ct);

    // ---- Stockages distants
    public Task<IReadOnlyList<RemoteStorage>> GetRemotesAsync(CancellationToken ct = default) => GetListAsync<RemoteStorage>("remote", ct);

    public async Task<DeviceCodeStart> StartOneDriveSignInAsync(CancellationToken ct = default)
    {
        using var r = await http.PostAsync("remote/oauth/onedrive/start", null, ct);
        await EnsureOkAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<DeviceCodeStart>(ct))!;
    }

    public async Task<DeviceCodePoll> PollOneDriveSignInAsync(string deviceCode, CancellationToken ct = default)
    {
        using var r = await http.PostAsJsonAsync("remote/oauth/onedrive/poll", new DeviceCodePollRequest(deviceCode), ct);
        await EnsureOkAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<DeviceCodePoll>(ct))!;
    }

    public async Task<OAuthStart> StartDropboxSignInAsync(CancellationToken ct = default)
    {
        using var r = await http.PostAsync("remote/oauth/dropbox/start", null, ct);
        await EnsureOkAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<OAuthStart>(ct))!;
    }

    public async Task<string> CompleteDropboxSignInAsync(string code, string state, CancellationToken ct = default)
    {
        using var r = await http.PostAsJsonAsync("remote/oauth/dropbox/complete", new OAuthCompleteRequest(code, state), ct);
        await EnsureOkAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<DeviceCodePoll>(ct))?.Token ?? "";
    }

    public async Task<RemoteTypes> GetRemoteTypesFullAsync(CancellationToken ct = default)
    {
        using var r = await http.GetAsync("remote/types", ct);
        await EnsureOkAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<RemoteTypes>(ct)) ?? new RemoteTypes([]);
    }

    public async Task<IReadOnlyList<string>> GetRemoteTypesAsync(CancellationToken ct = default)
    {
        using var r = await http.GetAsync("remote/types", ct);
        await EnsureOkAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<RemoteTypes>(ct))?.Available ?? [];
    }

    public async Task TestRemoteAsync(AddRemoteRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("remote/test", req, ct), ct);

    public async Task AddRemoteAsync(AddRemoteRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("remote", req, ct), ct);

    public async Task UpdateRemoteAsync(AddRemoteRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PutAsJsonAsync($"remote/{Uri.EscapeDataString(req.Name)}", req, ct), ct);

    public async Task MountRemoteAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsync($"remote/{Uri.EscapeDataString(name)}/mount", null, ct), ct);

    public async Task UnmountRemoteAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsync($"remote/{Uri.EscapeDataString(name)}/unmount", null, ct), ct);

    public async Task DeleteRemoteAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync($"remote/{Uri.EscapeDataString(name)}", ct), ct);

    public async Task<IReadOnlyList<NasUser>> GetUsersAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<NasUser>>("users", ct) ?? [];

    public async Task CreateUserAsync(CreateUserRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("users", req, ct), ct);

    public async Task SetPasswordAsync(string name, string password, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PutAsJsonAsync($"users/{Uri.EscapeDataString(name)}/password", new SetPasswordRequest(password), ct), ct);

    public async Task DeleteUserAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync($"users/{Uri.EscapeDataString(name)}", ct), ct);

    public async Task ApplySharesAsync(IReadOnlyList<ShareDef> shares, FtpSettings? ftp = null, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PutAsJsonAsync("shares", new ApplySharesRequest(shares, ftp), ct), ct);

    public Task<IReadOnlyList<ServiceInfo>> GetServicesAsync(CancellationToken ct = default) => GetListAsync<ServiceInfo>("services", ct);

    public async Task SetServiceAsync(string name, bool enabled, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PutAsJsonAsync($"services/{Uri.EscapeDataString(name)}", new SetServiceRequest(enabled), ct), ct);

    // ---- Sauvegardes
    public Task<IReadOnlyList<BackupTarget>> GetBackupTargetsAsync(CancellationToken ct = default) => GetListAsync<BackupTarget>("backup/targets", ct);

    public async Task<BackupDiskStatus> CheckBackupDiskAsync(string dir, string? id, CancellationToken ct = default)
    {
        using var r = await http.GetAsync($"backup/disk?dir={Uri.EscapeDataString(dir)}&id={Uri.EscapeDataString(id ?? "")}", ct);
        await EnsureOkAsync(r, ct);
        return await r.Content.ReadFromJsonAsync<BackupDiskStatus>(ct) ?? new(BackupDiskState.Missing, 0, 0);
    }

    public async Task<string> InitBackupDiskAsync(string dir, CancellationToken ct = default)
    {
        using var r = await http.PostAsJsonAsync("backup/disk/init", new InitDiskRequest(dir), ct);
        await EnsureOkAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<InitDiskResult>(ct))?.TargetId ?? throw new AgentException(Msg.F("The agent answered {0}.", 204));
    }

    public Task<IReadOnlyList<BackupRunStatus>> GetBackupRunsAsync(CancellationToken ct = default) => GetListAsync<BackupRunStatus>("backup/runs", ct);

    public async Task StartBackupAsync(BackupJobDef job, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("backup/runs", job, ct), ct);

    public async Task CancelBackupAsync(int jobId, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsync($"backup/runs/{jobId}/cancel", null, ct), ct);

    public async Task AcknowledgeBackupAsync(int jobId, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync($"backup/runs/{jobId}", ct), ct);

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

    public async Task<NetSnapshot> GetNetworkAsync(CancellationToken ct = default)
    {
        using var r = await http.GetAsync("network", ct);
        await EnsureOkAsync(r, ct);
        return await r.Content.ReadFromJsonAsync<NetSnapshot>(ct) ?? throw new AgentException(Msg.F("The agent answered {0}.", 204));
    }

    /// <summary>Applique puis vérifie : ne revient qu'une fois le résultat connu (jusqu'à une trentaine de secondes).</summary>
    public async Task EditNetworkAsync(NetEditRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("network/edit", req, ct), ct);

    public async Task<OvsStatus> GetOvsAsync(CancellationToken ct = default)
    {
        using var r = await http.GetAsync("network/ovs", ct);
        await EnsureOkAsync(r, ct);
        return await r.Content.ReadFromJsonAsync<OvsStatus>(ct) ?? throw new AgentException(Msg.F("The agent answered {0}.", 204));
    }

    public async Task SetOvsAsync(bool enabled, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("network/ovs", new OvsRequest(enabled), ct), ct);

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

    // ---- Machines virtuelles
    public async Task<VmStatus> GetVmStatusAsync(CancellationToken ct = default)
    {
        using var r = await http.GetAsync("vm/status", ct);
        await EnsureOkAsync(r, ct);
        return await r.Content.ReadFromJsonAsync<VmStatus>(ct) ?? new(VmHostState.NotInstalled, null, false, []);
    }

    public Task<IReadOnlyList<VmInfo>> GetVmsAsync(CancellationToken ct = default) => GetListAsync<VmInfo>("vm/machines", ct);

    public Task<IReadOnlyList<IsoFile>> GetIsosAsync(IEnumerable<string> shares, CancellationToken ct = default) =>
        GetListAsync<IsoFile>("vm/isos?shares=" + Uri.EscapeDataString(string.Join(',', shares)), ct);

    public async Task CreateVmAsync(CreateVmRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("vm/machines", req, ct), ct);

    public async Task VmActionAsync(string name, string action, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsync($"vm/machines/{Uri.EscapeDataString(name)}/{action}", null, ct), ct);

    public async Task<VmConfig> GetVmConfigAsync(string name, CancellationToken ct = default)
    {
        using var r = await http.GetAsync($"vm/machines/{Uri.EscapeDataString(name)}/config", ct);
        await EnsureOkAsync(r, ct);
        return await r.Content.ReadFromJsonAsync<VmConfig>(ct) ?? throw new AgentException(Msg.F("The agent answered {0}.", 204));
    }

    string VmUrl(string name, string tail) => $"vm/machines/{Uri.EscapeDataString(name)}/{tail}";

    public async Task SaveVmSettingsAsync(string name, VmSettings s, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PutAsJsonAsync(VmUrl(name, "config"), s, ct), ct);

    public async Task AddVmDiskAsync(string name, AddVmDiskRequest r, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync(VmUrl(name, "disks"), r, ct), ct);

    public async Task RemoveVmDiskAsync(string name, string target, bool deleteFile, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync(VmUrl(name, $"disks?target={Uri.EscapeDataString(target)}&deleteFile={deleteFile.ToString().ToLowerInvariant()}"), ct), ct);

    public async Task ResizeVmDiskAsync(string name, ResizeVmDiskRequest r, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync(VmUrl(name, "disks/resize"), r, ct), ct);

    public async Task SetVmCdromAsync(string name, SetVmCdromRequest r, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PutAsJsonAsync(VmUrl(name, "cdrom"), r, ct), ct);

    public async Task AddVmNicAsync(string name, AddVmNicRequest r, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync(VmUrl(name, "nics"), r, ct), ct);

    public async Task UpdateVmNicAsync(string name, UpdateVmNicRequest r, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PutAsJsonAsync(VmUrl(name, "nics"), r, ct), ct);

    public async Task RemoveVmNicAsync(string name, string mac, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync(VmUrl(name, $"nics?mac={Uri.EscapeDataString(mac)}"), ct), ct);

    public Task<IReadOnlyList<VmSnapshot>> GetVmSnapshotsAsync(string name, CancellationToken ct = default) => GetListAsync<VmSnapshot>(VmUrl(name, "snapshots"), ct);

    public async Task CreateVmSnapshotAsync(string name, CreateVmSnapshotRequest r, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync(VmUrl(name, "snapshots"), r, ct), ct);

    public async Task VmSnapshotActionAsync(string name, string snapshot, string action, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsync(VmUrl(name, $"snapshots/{Uri.EscapeDataString(snapshot)}/{action}"), null, ct), ct);

    public async Task<int> GetVmVncPortAsync(string name, CancellationToken ct = default)
    {
        using var r = await http.GetAsync($"vm/machines/{Uri.EscapeDataString(name)}/vnc", ct);
        await EnsureOkAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<VmPort>(ct))?.Port ?? throw new AgentException(Msg.F("The agent answered {0}.", 204));
    }

    // ---- Docker
    public async Task<DockerStatus> GetDockerStatusAsync(CancellationToken ct = default)
    {
        using var r = await http.GetAsync("docker/status", ct);
        await EnsureOkAsync(r, ct);
        return await r.Content.ReadFromJsonAsync<DockerStatus>(ct) ?? new(DockerState.NotInstalled, null, null);
    }

    public Task<IReadOnlyList<ContainerInfo>> GetContainersAsync(CancellationToken ct = default) => GetListAsync<ContainerInfo>("docker/containers", ct);
    public Task<IReadOnlyList<ContainerStats>> GetContainerStatsAsync(CancellationToken ct = default) => GetListAsync<ContainerStats>("docker/stats", ct);
    public Task<IReadOnlyList<ImageInfo>> GetImagesAsync(CancellationToken ct = default) => GetListAsync<ImageInfo>("docker/images", ct);
    public Task<IReadOnlyList<DockerVolume>> GetDockerVolumesAsync(CancellationToken ct = default) => GetListAsync<DockerVolume>("docker/volumes", ct);
    public Task<IReadOnlyList<DockerNetwork>> GetDockerNetworksAsync(CancellationToken ct = default) => GetListAsync<DockerNetwork>("docker/networks", ct);
    public Task<IReadOnlyList<StackInfo>> GetStacksAsync(CancellationToken ct = default) => GetListAsync<StackInfo>("docker/stacks", ct);

    public async Task<string> GetContainerLogsAsync(string id, int tail, CancellationToken ct = default)
    {
        using var r = await http.GetAsync($"docker/containers/{Uri.EscapeDataString(id)}/logs?tail={tail}", ct);
        await EnsureOkAsync(r, ct);
        return await r.Content.ReadAsStringAsync(ct);
    }

    public async Task ContainerActionAsync(string id, string action, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsync($"docker/containers/{Uri.EscapeDataString(id)}/{action}", null, ct), ct);

    public async Task RunContainerAsync(RunContainerRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("docker/containers", req, ct), ct);

    public async Task<ContainerSettings> GetContainerSettingsAsync(string id, CancellationToken ct = default)
    {
        using var r = await http.GetAsync($"docker/containers/{Uri.EscapeDataString(id)}/settings", ct);
        await EnsureOkAsync(r, ct);
        return await r.Content.ReadFromJsonAsync<ContainerSettings>(ct) ?? throw new AgentException(Msg.F("The agent answered {0}.", 204));
    }

    public async Task UpdateContainerAsync(string id, RunContainerRequest req, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PutAsJsonAsync($"docker/containers/{Uri.EscapeDataString(id)}", req, ct), ct);

    public async Task PullImageAsync(string image, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("docker/images/pull", new PullImageRequest(image), ct), ct);

    public async Task<string> PruneImagesAsync(CancellationToken ct = default)
    {
        using var r = await http.PostAsync("docker/images/prune", null, ct);
        await EnsureOkAsync(r, ct);
        return await r.Content.ReadAsStringAsync(ct);
    }

    public async Task RemoveImageAsync(string id, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync($"docker/images?id={Uri.EscapeDataString(id)}", ct), ct);

    public async Task CreateDockerVolumeAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("docker/volumes", new DockerNameRequest(name), ct), ct);

    public async Task RemoveDockerVolumeAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync($"docker/volumes?name={Uri.EscapeDataString(name)}", ct), ct);

    public async Task CreateDockerNetworkAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsJsonAsync("docker/networks", new DockerNameRequest(name), ct), ct);

    public async Task RemoveDockerNetworkAsync(string name, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.DeleteAsync($"docker/networks?name={Uri.EscapeDataString(name)}", ct), ct);

    public async Task<StackContent> GetStackAsync(string name, CancellationToken ct = default)
    {
        using var r = await http.GetAsync($"docker/stacks/{Uri.EscapeDataString(name)}", ct);
        await EnsureOkAsync(r, ct);
        return await r.Content.ReadFromJsonAsync<StackContent>(ct) ?? throw new AgentException(Msg.F("The agent answered {0}.", 204));
    }

    public async Task SaveStackAsync(string name, string content, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PutAsJsonAsync($"docker/stacks/{Uri.EscapeDataString(name)}", new SaveStackRequest(content), ct), ct);

    public async Task StackActionAsync(string name, string action, CancellationToken ct = default) =>
        await EnsureOkAsync(await http.PostAsync($"docker/stacks/{Uri.EscapeDataString(name)}/{action}", null, ct), ct);

    public async Task<string> GetShareRootAsync(string share, CancellationToken ct = default)
    {
        using var r = await http.GetAsync($"shares/{Uri.EscapeDataString(share)}/path", ct);
        await EnsureOkAsync(r, ct);
        return (await r.Content.ReadFromJsonAsync<ShareRoot>(ct))?.Path ?? throw new AgentException(Msg.F("The agent answered {0}.", 204));
    }

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
