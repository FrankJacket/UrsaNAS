using Microsoft.AspNetCore.Components.Authorization;
using Nas.Contracts;
using Nas.Web.Data;

namespace Nas.Web;

/// <summary>Les opérations sur les disques faites depuis l'interface : valide, demande à l'agent d'agir, journalise.</summary>
public class DiskOperations(AgentClient agent, Repo repo, AuthenticationStateProvider auth)
{
    async Task<string> ActorAsync() => (await auth.GetAuthenticationStateAsync()).User.Identity?.Name ?? "unknown";

    public async Task PrepareAsync(PrepareDiskRequest r)
    {
        if (!Rules.IsDiskPath(r.Disk)) throw new AgentException(Msg.F("Unknown disk."));
        if (Rules.ValidateFormat(r.Fs, r.Label) is { } e1) throw new AgentException(e1);
        await agent.PrepareDiskAsync(r);
        await repo.AuditAsync(await ActorAsync(), "Disk partitioned and formatted", $"{r.Disk} ({r.Fs})");
    }

    public async Task CreatePartitionAsync(CreatePartitionRequest r)
    {
        if (!Rules.IsDiskPath(r.Disk)) throw new AgentException(Msg.F("Unknown disk."));
        if (Rules.ValidateFormat(r.Fs, r.Label) is { } e1) throw new AgentException(e1);
        await agent.CreatePartitionAsync(r);
        await repo.AuditAsync(await ActorAsync(), "Partition created", $"{r.Disk} ({r.Fs}, {(r.SizeMb is { } mb ? mb + " MB" : "all")})");
    }

    public async Task WipeAsync(string disk)
    {
        if (!Rules.IsDiskPath(disk)) throw new AgentException(Msg.F("Unknown disk."));
        await agent.WipeDiskAsync(new WipeDiskRequest(disk));
        await repo.AuditAsync(await ActorAsync(), "Disk erased", disk);
    }

    public async Task FormatAsync(FormatPartitionRequest r)
    {
        if (!Rules.IsPartitionPath(r.Partition)) throw new AgentException(Msg.F("Unknown partition."));
        if (Rules.ValidateFormat(r.Fs, r.Label) is { } e1) throw new AgentException(e1);
        await agent.FormatPartitionAsync(r);
        await repo.AuditAsync(await ActorAsync(), "Partition formatted", $"{r.Partition} ({r.Fs})");
    }

    public async Task MountAsync(MountPartitionRequest r)
    {
        if (!Rules.IsPartitionPath(r.Partition)) throw new AgentException(Msg.F("Unknown partition."));
        if (Rules.ValidateMountName(r.Name) is { } e) throw new AgentException(e);
        await agent.MountPartitionAsync(r);
        await repo.AuditAsync(await ActorAsync(), "Partition mounted", $"{r.Partition} → {r.Name}");
    }

    public async Task UnmountAsync(string mountPoint)
    {
        if ((await repo.GetSharesAsync()).FirstOrDefault(s => s.Disk == mountPoint) is { } used)
            throw new AgentException(Msg.F("The disk is shared as “{0}”: change or delete that share first.", used.Name));
        await agent.UnmountPartitionAsync(new UnmountRequest(mountPoint));
        await repo.AuditAsync(await ActorAsync(), "Partition unmounted", mountPoint);
    }

    public async Task GrowSystemAsync()
    {
        await agent.GrowSystemDiskAsync();
        await repo.AuditAsync(await ActorAsync(), "System disk extended", "/");
    }
}
