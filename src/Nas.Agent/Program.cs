using Nas.Agent.Services;
using Nas.Contracts;

namespace Nas.Agent;

public partial class Program
{
    // Une seule opération système à la fois : useradd, smbpasswd et smb.conf ne supportent pas les écritures concurrentes.
    static readonly SemaphoreSlim Gate = new(1, 1);

    static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        await Gate.WaitAsync();
        try { return await action(); }
        catch (OperationException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest); }
        finally { Gate.Release(); }
    }

    /// <summary>Lecture : pas de verrou (zpool/zfs list sont sûrs en parallèle), mais les échecs restent lisibles côté interface.</summary>
    static async Task<IResult> Read(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (OperationException e) { return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest); }
        catch (System.ComponentModel.Win32Exception) { return Results.Problem("A required program (zpool, zfs…) was not found on this machine.", statusCode: StatusCodes.Status400BadRequest); }
    }

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var socketPath = builder.Configuration["Agent:SocketPath"];
        if (!string.IsNullOrWhiteSpace(socketPath))
        {
            builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socketPath));
        }

        if (builder.Configuration["URSANAS_FAKE"] == "1")
            builder.Services.AddSingleton<ICommandRunner, FakeCommandRunner>();
        else
            builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
        if (builder.Configuration["URSANAS_FAKE"] == "1")
            builder.Services.AddSingleton<IDiskProvider, FakeDiskProvider>();
        else
            builder.Services.AddSingleton<IDiskProvider, LsblkDiskProvider>();

        if (builder.Configuration["URSANAS_FAKE"] == "1")
            builder.Services.AddSingleton<IStatsProvider, FakeStatsProvider>();
        else
            builder.Services.AddSingleton<IStatsProvider, ProcStatsProvider>();

        builder.Services.AddSingleton<UserService>();
        builder.Services.AddSingleton<ProtocolService>();
        builder.Services.AddSingleton<BackupService>();
        builder.Services.AddSingleton<DiskFilesService>();
        builder.Services.AddSingleton<TransferService>();
        builder.Services.AddSingleton<DockerService>();
        builder.Services.AddSingleton<VmService>();
        builder.Services.AddSingleton<DiskService>();
        builder.Services.AddSingleton<RemoteService>();
        builder.Services.AddSingleton<ShareService>();
        builder.Services.AddSingleton<GroupService>();
        builder.Services.AddSingleton<AclService>();
        builder.Services.AddSingleton<ZfsService>();
        builder.Services.AddSingleton<SmartService>();

        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/disks", async (IDiskProvider disks, CancellationToken ct) => Results.Ok(await disks.ListAsync(ct)));

        app.MapGet("/disks/filesystems", (DiskService d) => Results.Ok(d.Filesystems()));
        app.MapPost("/disks/prepare", (PrepareDiskRequest r, DiskService d, CancellationToken ct) =>
            Read(async () => { await d.PrepareDiskAsync(r, ct); return Results.NoContent(); }));
        app.MapPost("/disks/partitions", (CreatePartitionRequest r, DiskService d, CancellationToken ct) =>
            Read(async () => { await d.CreatePartitionAsync(r, ct); return Results.NoContent(); }));
        app.MapPost("/disks/wipe", (WipeDiskRequest r, DiskService d, CancellationToken ct) =>
            Read(async () => { await d.WipeDiskAsync(r.Disk, ct); return Results.NoContent(); }));
        app.MapPost("/disks/grow-system", (DiskService d, CancellationToken ct) =>
            Read(async () => { await d.GrowSystemAsync(ct); return Results.NoContent(); }));
        app.MapPost("/partitions/format", (FormatPartitionRequest r, DiskService d, CancellationToken ct) =>
            Read(async () => { await d.FormatPartitionAsync(r, ct); return Results.NoContent(); }));
        app.MapPost("/partitions/mount", (MountPartitionRequest r, DiskService d, CancellationToken ct) =>
            Read(async () => { await d.MountAsync(r, ct); return Results.NoContent(); }));
        app.MapPost("/partitions/unmount", (UnmountRequest r, DiskService d, CancellationToken ct) =>
            Read(async () => { await d.UnmountAsync(r.MountPoint, ct); return Results.NoContent(); }));

        // Stockages distants (SMB, NFS)
        app.MapGet("/remote", (RemoteService s) => Read(() => Task.FromResult(Results.Ok(s.List()))));
        app.MapGet("/remote/types", (RemoteService s) => Results.Ok(s.Types()));
        app.MapPost("/remote/test", (AddRemoteRequest r, RemoteService s, CancellationToken ct) =>
            Read(async () => { await s.TestAsync(r, ct); return Results.NoContent(); }));
        app.MapPost("/remote", (AddRemoteRequest r, RemoteService s, CancellationToken ct) =>
            Read(async () => { await s.AddAsync(r, ct); return Results.NoContent(); }));
        app.MapPost("/remote/{name}/mount", (string name, RemoteService s, CancellationToken ct) =>
            Read(async () => { await s.MountAsync(name, ct); return Results.NoContent(); }));
        app.MapPost("/remote/{name}/unmount", (string name, RemoteService s, CancellationToken ct) =>
            Read(async () => { await s.UnmountAsync(name, ct); return Results.NoContent(); }));
        app.MapDelete("/remote/{name}", (string name, RemoteService s, CancellationToken ct) =>
            Read(async () => { await s.RemoveAsync(name, ct); return Results.NoContent(); }));
        app.MapGet("/stats", (IStatsProvider s) => Results.Ok(s.Read()));

        app.MapGet("/users", async (UserService u, CancellationToken ct) => Results.Ok(await u.ListAsync(ct)));
        app.MapPost("/users", (CreateUserRequest r, UserService u, CancellationToken ct) =>
            Guard(async () => { await u.CreateAsync(r, ct); return Results.NoContent(); }));
        app.MapPut("/users/{name}/password", (string name, SetPasswordRequest r, UserService u, CancellationToken ct) =>
            Guard(async () => { await u.SetPasswordAsync(name, r.Password, ct); return Results.NoContent(); }));
        app.MapDelete("/users/{name}", (string name, UserService u, CancellationToken ct) =>
            Guard(async () => { await u.DeleteAsync(name, ct); return Results.NoContent(); }));
        app.MapPut("/shares", (ApplySharesRequest r, ShareService s, CancellationToken ct) =>
            Guard(async () => { await s.ApplyAsync(r.Shares, r.Ftp, ct); return Results.NoContent(); }));

        app.MapGet("/services", (ProtocolService p, CancellationToken ct) => Read(async () => Results.Ok(await p.ListServicesAsync(ct))));
        app.MapPut("/services/{name}", (string name, SetServiceRequest r, ProtocolService p, CancellationToken ct) =>
            Guard(async () => { await p.SetServiceAsync(name, r.Enabled, ct); return Results.NoContent(); }));

        app.MapGet("/groups", (GroupService g, CancellationToken ct) => Read(async () => Results.Ok(await g.ListAsync(ct))));
        app.MapPost("/groups", (CreateGroupRequest r, GroupService g, CancellationToken ct) =>
            Guard(async () => { await g.CreateAsync(r, ct); return Results.NoContent(); }));
        app.MapPut("/groups/{name}/members", (string name, SetGroupMembersRequest r, GroupService g, CancellationToken ct) =>
            Guard(async () => { await g.SetMembersAsync(name, r.Members, ct); return Results.NoContent(); }));
        app.MapDelete("/groups/{name}", (string name, GroupService g, CancellationToken ct) =>
            Guard(async () => { await g.DeleteAsync(name, ct); return Results.NoContent(); }));

        // Un « partage » qui commence par « / » est un disque monté (USB…) : le gestionnaire de fichiers s'en occupe, sans ACL.
        app.MapGet("/acl/folders", (string share, string? path, bool? files, AclService a, DiskFilesService d, CancellationToken ct) =>
            Read(async () => Results.Ok(DiskFilesService.IsDisk(share) ? d.List(share, path ?? "", files == true) : await a.ListFoldersAsync(share, path ?? "", files == true, ct))));
        app.MapPut("/acl/folder", (SetFolderAclRequest r, AclService a, CancellationToken ct) =>
            Guard(async () => { await a.SetFolderAsync(r, ct); return Results.NoContent(); }));
        app.MapPost("/acl/folder", (CreateFolderRequest r, AclService a, DiskFilesService d, CancellationToken ct) =>
            Guard(async () => { if (DiskFilesService.IsDisk(r.Share)) d.CreateFolder(r); else await a.CreateFolderAsync(r, ct); return Results.NoContent(); }));
        app.MapPost("/acl/folder/move", (MoveFolderRequest r, AclService a, DiskFilesService d, TransferService t, CancellationToken ct) =>
            Guard(async () => { if (r.DestShare is not null && r.DestShare != r.Share) await t.MoveAsync(r, ct); else if (DiskFilesService.IsDisk(r.Share)) d.Move(r); else await a.MoveFolderAsync(r, ct); return Results.NoContent(); }));
        app.MapPost("/acl/folder/copy", (CopyFolderRequest r, AclService a, DiskFilesService d, TransferService t, CancellationToken ct) =>
            Guard(async () => { if (r.DestShare is not null && r.DestShare != r.Share) await t.CopyAsync(r, ct); else if (DiskFilesService.IsDisk(r.Share)) d.Copy(r); else await a.CopyFolderAsync(r, ct); return Results.NoContent(); }));
        app.MapDelete("/acl/folder", (string share, string path, bool? file, AclService a, DiskFilesService d, CancellationToken ct) =>
            Guard(async () => { if (DiskFilesService.IsDisk(share)) d.Delete(share, path, file == true); else await a.DeleteFolderAsync(share, path, file == true, ct); return Results.NoContent(); }));
        app.MapPost("/acl/reset", (ResetAclRequest r, AclService a, CancellationToken ct) =>
            Guard(async () => { await a.ResetTreeAsync(r.Share, r.Path, ct); return Results.NoContent(); }));

        // Sauvegardes (moteur d'Ursacopy)
        app.MapGet("/backup/targets", (BackupService b) => Read(() => Task.FromResult(Results.Ok(b.ListTargets()))));
        app.MapGet("/backup/disk", (string dir, string? id, BackupService b) => Read(() => Task.FromResult(Results.Ok(b.CheckDisk(dir, id)))));
        app.MapPost("/backup/disk/init", (InitDiskRequest r, BackupService b) =>
            Read(() => Task.FromResult(Results.Ok(new InitDiskResult(b.InitDisk(r.TargetDir))))));
        app.MapGet("/backup/runs", (BackupService b) => Results.Ok(b.Statuses()));
        app.MapPost("/backup/runs", (BackupJobDef job, BackupService b) =>
            Read(() => { b.Start(job); return Task.FromResult(Results.NoContent()); }));
        app.MapPost("/backup/runs/{id:int}/cancel", (int id, BackupService b) => { b.Cancel(id); return Results.NoContent(); });
        app.MapDelete("/backup/runs/{id:int}", (int id, BackupService b) => { b.Acknowledge(id); return Results.NoContent(); });

        // Docker : chaque opération qui modifie quelque chose passe par le verrou propre à DockerService (un téléchargement peut être long).
        app.MapGet("/docker/status", async (DockerService d, CancellationToken ct) => Results.Ok(await d.StatusAsync(ct)));
        app.MapGet("/docker/containers", (DockerService d, CancellationToken ct) => Read(async () => Results.Ok(await d.ContainersAsync(ct))));
        app.MapGet("/docker/stats", (DockerService d, CancellationToken ct) => Read(async () => Results.Ok(await d.StatsAsync(ct))));
        app.MapGet("/docker/containers/{id}/logs", (string id, int? tail, DockerService d, CancellationToken ct) =>
            Read(async () => Results.Text(await d.LogsAsync(id, tail ?? 200, ct))));
        app.MapPost("/docker/containers/{id}/{action}", (string id, string action, DockerService d, CancellationToken ct) =>
            Read(async () => { await d.ContainerActionAsync(id, action, ct); return Results.NoContent(); }));
        app.MapGet("/docker/containers/{id}/settings", (string id, DockerService d, CancellationToken ct) =>
            Read(async () => Results.Ok(await d.SettingsAsync(id, ct))));
        app.MapPut("/docker/containers/{id}", (string id, RunContainerRequest r, DockerService d, CancellationToken ct) =>
            Read(async () => { await d.UpdateContainerAsync(id, r, ct); return Results.NoContent(); }));
        app.MapPost("/docker/containers", (RunContainerRequest r, DockerService d, CancellationToken ct) =>
            Read(async () => { await d.RunContainerAsync(r, ct); return Results.NoContent(); }));
        app.MapGet("/docker/images", (DockerService d, CancellationToken ct) => Read(async () => Results.Ok(await d.ImagesAsync(ct))));
        app.MapPost("/docker/images/pull", (PullImageRequest r, DockerService d, CancellationToken ct) =>
            Read(async () => { await d.PullImageAsync(r.Image, ct); return Results.NoContent(); }));
        app.MapPost("/docker/images/prune", (DockerService d, CancellationToken ct) => Read(async () => Results.Text(await d.PruneImagesAsync(ct))));
        app.MapDelete("/docker/images", (string id, DockerService d, CancellationToken ct) =>
            Read(async () => { await d.RemoveImageAsync(id, ct); return Results.NoContent(); }));
        app.MapGet("/docker/volumes", (DockerService d, CancellationToken ct) => Read(async () => Results.Ok(await d.VolumesAsync(ct))));
        app.MapPost("/docker/volumes", (DockerNameRequest r, DockerService d, CancellationToken ct) =>
            Read(async () => { await d.CreateVolumeAsync(r.Name, ct); return Results.NoContent(); }));
        app.MapDelete("/docker/volumes", (string name, DockerService d, CancellationToken ct) =>
            Read(async () => { await d.RemoveVolumeAsync(name, ct); return Results.NoContent(); }));
        app.MapGet("/docker/networks", (DockerService d, CancellationToken ct) => Read(async () => Results.Ok(await d.NetworksAsync(ct))));
        app.MapPost("/docker/networks", (DockerNameRequest r, DockerService d, CancellationToken ct) =>
            Read(async () => { await d.CreateNetworkAsync(r.Name, ct); return Results.NoContent(); }));
        app.MapDelete("/docker/networks", (string name, DockerService d, CancellationToken ct) =>
            Read(async () => { await d.RemoveNetworkAsync(name, ct); return Results.NoContent(); }));
        app.MapGet("/docker/stacks", (DockerService d, CancellationToken ct) => Read(async () => Results.Ok(await d.StacksAsync(ct))));
        app.MapGet("/docker/stacks/{name}", (string name, DockerService d) => Read(() => Task.FromResult(Results.Ok(d.ReadStack(name)))));
        app.MapPut("/docker/stacks/{name}", (string name, SaveStackRequest r, DockerService d, CancellationToken ct) =>
            Read(async () => { await d.SaveStackAsync(name, r.Content, ct); return Results.NoContent(); }));
        app.MapPost("/docker/stacks/{name}/{action}", (string name, string action, DockerService d, CancellationToken ct) =>
            Read(async () => { await d.StackActionAsync(name, action, ct); return Results.NoContent(); }));
        // Machines virtuelles (libvirt)
        app.MapGet("/vm/status", async (VmService v, CancellationToken ct) => Results.Ok(await v.StatusAsync(ct)));
        app.MapGet("/vm/machines", (VmService v, CancellationToken ct) => Read(async () => Results.Ok(await v.ListAsync(ct))));
        app.MapPost("/vm/machines", (CreateVmRequest r, VmService v, CancellationToken ct) =>
            Read(async () => { await v.CreateAsync(r, ct); return Results.NoContent(); }));
        app.MapGet("/vm/isos", (string? shares, VmService v, CancellationToken ct) =>
            Read(async () => Results.Ok(await v.IsosAsync((shares ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), ct))));
        app.MapGet("/vm/machines/{name}/config", (string name, VmService v, CancellationToken ct) => Read(async () => Results.Ok(await v.ConfigAsync(name, ct))));
        app.MapPut("/vm/machines/{name}/config", (string name, VmSettings s, VmService v, CancellationToken ct) =>
            Read(async () => { await v.SaveSettingsAsync(name, s, ct); return Results.NoContent(); }));
        app.MapPost("/vm/machines/{name}/disks", (string name, AddVmDiskRequest r, VmService v, CancellationToken ct) =>
            Read(async () => { await v.AddDiskAsync(name, r, ct); return Results.NoContent(); }));
        app.MapDelete("/vm/machines/{name}/disks", (string name, string target, bool? deleteFile, VmService v, CancellationToken ct) =>
            Read(async () => { await v.RemoveDiskAsync(name, target, deleteFile == true, ct); return Results.NoContent(); }));
        app.MapPost("/vm/machines/{name}/disks/resize", (string name, ResizeVmDiskRequest r, VmService v, CancellationToken ct) =>
            Read(async () => { await v.ResizeDiskAsync(name, r, ct); return Results.NoContent(); }));
        app.MapPut("/vm/machines/{name}/cdrom", (string name, SetVmCdromRequest r, VmService v, CancellationToken ct) =>
            Read(async () => { await v.SetCdromAsync(name, r, ct); return Results.NoContent(); }));
        app.MapPost("/vm/machines/{name}/nics", (string name, AddVmNicRequest r, VmService v, CancellationToken ct) =>
            Read(async () => { await v.AddNicAsync(name, r, ct); return Results.NoContent(); }));
        app.MapPut("/vm/machines/{name}/nics", (string name, UpdateVmNicRequest r, VmService v, CancellationToken ct) =>
            Read(async () => { await v.UpdateNicAsync(name, r, ct); return Results.NoContent(); }));
        app.MapDelete("/vm/machines/{name}/nics", (string name, string mac, VmService v, CancellationToken ct) =>
            Read(async () => { await v.RemoveNicAsync(name, mac, ct); return Results.NoContent(); }));
        app.MapGet("/vm/machines/{name}/snapshots", (string name, VmService v, CancellationToken ct) => Read(async () => Results.Ok(await v.SnapshotsAsync(name, ct))));
        app.MapPost("/vm/machines/{name}/snapshots", (string name, CreateVmSnapshotRequest r, VmService v, CancellationToken ct) =>
            Read(async () => { await v.CreateSnapshotAsync(name, r, ct); return Results.NoContent(); }));
        app.MapPost("/vm/machines/{name}/snapshots/{snapshot}/{action}", (string name, string snapshot, string action, VmService v, CancellationToken ct) =>
            Read(async () => { await v.SnapshotActionAsync(name, snapshot, action, ct); return Results.NoContent(); }));
        app.MapGet("/vm/machines/{name}/vnc", (string name, VmService v, CancellationToken ct) =>
            Read(async () => Results.Ok(new VmPort(await v.VncPortAsync(name, ct)))));
        app.MapPost("/vm/machines/{name}/{action}", (string name, string action, VmService v, CancellationToken ct) =>
            Read(async () => { await v.ActionAsync(name, action, ct); return Results.NoContent(); }));
        // Chemin réel d'un partage, pour proposer un dossier du NAS comme volume.
        app.MapGet("/shares/{name}/path", (string name, AclService a, CancellationToken ct) =>
            Read(async () => Results.Ok(new ShareRoot(name, await a.SharePathAsync(name, ct)))));

        app.MapGet("/smart",(SmartService s, CancellationToken ct) => Read(async () => Results.Ok(await s.ListAsync(ct))));

        app.MapGet("/zfs/pools",(ZfsService z, CancellationToken ct) => Read(async () => Results.Ok(await z.ListPoolsAsync(ct))));
        app.MapPost("/zfs/pools", (CreatePoolRequest r, ZfsService z, CancellationToken ct) =>
            Guard(async () => { await z.CreatePoolAsync(r, ct); return Results.NoContent(); }));
        app.MapDelete("/zfs/pools/{name}", (string name, ZfsService z, CancellationToken ct) =>
            Guard(async () => { await z.DestroyPoolAsync(name, ct); return Results.NoContent(); }));
        app.MapPost("/zfs/pools/{name}/scrub", (string name, ZfsService z, CancellationToken ct) =>
            Guard(async () => { await z.ScrubAsync(name, ct); return Results.NoContent(); }));

        app.MapGet("/zfs/datasets", (ZfsService z, CancellationToken ct) => Read(async () => Results.Ok(await z.ListDatasetsAsync(ct))));
        app.MapPost("/zfs/datasets", (CreateDatasetRequest r, ZfsService z, CancellationToken ct) =>
            Guard(async () => { await z.CreateDatasetAsync(r.Name, ct); return Results.NoContent(); }));
        app.MapDelete("/zfs/datasets", (string name, ZfsService z, CancellationToken ct) =>
            Guard(async () => { await z.DestroyDatasetAsync(name, ct); return Results.NoContent(); }));

        app.MapGet("/zfs/snapshots", (ZfsService z, CancellationToken ct) => Read(async () => Results.Ok(await z.ListSnapshotsAsync(ct))));
        app.MapPost("/zfs/snapshots", (CreateSnapshotRequest r, ZfsService z, CancellationToken ct) =>
            Guard(async () => { await z.CreateSnapshotAsync(r, ct); return Results.NoContent(); }));
        app.MapDelete("/zfs/snapshots", (string name, ZfsService z, CancellationToken ct) =>
            Guard(async () => { await z.DestroySnapshotAsync(name, ct); return Results.NoContent(); }));

        // Les montages FTP ne survivent pas à un redémarrage : on remonte ceux qui sont enregistrés.
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            if (OperatingSystem.IsWindows()) return;
            _ = Task.Run(async () =>
            {
                await Gate.WaitAsync();
                try { await app.Services.GetRequiredService<ProtocolService>().RestoreBindsAsync(); }
                catch (Exception e) { app.Logger.LogWarning(e, "Restoring the FTP mounts failed."); }
                finally { Gate.Release(); }
            });
        });

        // Le socket n'est accessible qu'au propriétaire et au groupe (le groupe est celui de l'unité systemd).
        if (!string.IsNullOrWhiteSpace(socketPath))
        {
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                if (OperatingSystem.IsWindows()) return;
                File.SetUnixFileMode(socketPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            });
        }

        app.Run();
    }
}
