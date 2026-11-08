using Microsoft.AspNetCore.Components.Authorization;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>Les opérations sur les machines virtuelles faites depuis l'interface : valide, demande à l'agent d'agir, journalise.</summary>
public class VmOperations(AgentClient agent, Repo repo, AuthenticationStateProvider auth)
{
    async Task<string> ActorAsync() => (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? "unknown";

    static readonly Dictionary<string, string> Audit = new()
    {
        ["start"] = "Virtual machine started", ["shutdown"] = "Virtual machine shut down",
        ["stop"] = "Virtual machine powered off", ["remove"] = "Virtual machine deleted",
    };

    public async Task ActionAsync(string name, string action)
    {
        if (Rules.ValidateVmName(name) is { } e) throw new AgentException(e);
        if (!Audit.TryGetValue(action, out var label)) throw new AgentException(Msg.F("Unknown action."));
        await agent.VmActionAsync(name, action);
        await repo.AuditAsync(await ActorAsync(), label, name);
    }

    public async Task CreateAsync(CreateVmRequest r)
    {
        if (Rules.ValidateCreateVm(r) is { } e) throw new AgentException(e);
        await agent.CreateVmAsync(r);
        await repo.AuditAsync(await ActorAsync(), "Virtual machine created", $"{r.Name} ({r.Vcpus} CPU, {r.MemoryMb} MB, {r.DiskGb} GB)");
    }

    async Task Do(string name, Func<Task> action, string label, string? detail = null)
    {
        if (Rules.ValidateVmName(name) is { } e) throw new AgentException(e);
        await action();
        await repo.AuditAsync(await ActorAsync(), label, detail is null ? name : $"{name} ({detail})");
    }

    public Task SaveSettingsAsync(string name, VmSettings s) =>
        Rules.ValidateVmSettings(s) is { } e ? throw new AgentException(e)
            : Do(name, () => agent.SaveVmSettingsAsync(name, s), "Virtual machine settings changed");

    public Task AddDiskAsync(string name, AddVmDiskRequest r) => Do(name, () => agent.AddVmDiskAsync(name, r), "Virtual machine disk added", $"{r.Bus}, {r.SizeGb} GB");
    public Task RemoveDiskAsync(string name, string target, bool deleteFile) => Do(name, () => agent.RemoveVmDiskAsync(name, target, deleteFile), "Virtual machine disk removed", target);
    public Task ResizeDiskAsync(string name, ResizeVmDiskRequest r) => Do(name, () => agent.ResizeVmDiskAsync(name, r), "Virtual machine disk resized", $"{r.Target}, {r.SizeGb} GB");
    public Task SetCdromAsync(string name, SetVmCdromRequest r) => Do(name, () => agent.SetVmCdromAsync(name, r), "Virtual machine CD/DVD drive changed", r.IsoPath);
    public Task AddNicAsync(string name, AddVmNicRequest r) => Do(name, () => agent.AddVmNicAsync(name, r), "Virtual machine network card added", r.Model);
    public Task UpdateNicAsync(string name, UpdateVmNicRequest r) => Do(name, () => agent.UpdateVmNicAsync(name, r), "Virtual machine network card changed", r.Mac);
    public Task RemoveNicAsync(string name, string mac) => Do(name, () => agent.RemoveVmNicAsync(name, mac), "Virtual machine network card removed", mac);

    public Task CreateSnapshotAsync(string name, string snapshot, string? description) =>
        Do(name, () => agent.CreateVmSnapshotAsync(name, new CreateVmSnapshotRequest(snapshot, description)), "Virtual machine snapshot created", snapshot);

    public Task SnapshotActionAsync(string name, string snapshot, string action) =>
        Do(name, () => agent.VmSnapshotActionAsync(name, snapshot, action), action == "revert" ? "Virtual machine snapshot restored" : "Virtual machine snapshot deleted", snapshot);
}
