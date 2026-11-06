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
        builder.Services.AddSingleton<ShareService>();
        builder.Services.AddSingleton<GroupService>();
        builder.Services.AddSingleton<AclService>();
        builder.Services.AddSingleton<ZfsService>();
        builder.Services.AddSingleton<SmartService>();

        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/disks", async (IDiskProvider disks, CancellationToken ct) => Results.Ok(await disks.ListAsync(ct)));

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

        app.MapGet("/acl/folders", (string share, string? path, bool? files, AclService a, CancellationToken ct) =>
            Read(async () => Results.Ok(await a.ListFoldersAsync(share, path ?? "", files == true, ct))));
        app.MapPut("/acl/folder", (SetFolderAclRequest r, AclService a, CancellationToken ct) =>
            Guard(async () => { await a.SetFolderAsync(r, ct); return Results.NoContent(); }));
        app.MapPost("/acl/folder", (CreateFolderRequest r, AclService a, CancellationToken ct) =>
            Guard(async () => { await a.CreateFolderAsync(r, ct); return Results.NoContent(); }));
        app.MapPost("/acl/folder/move", (MoveFolderRequest r, AclService a, CancellationToken ct) =>
            Guard(async () => { await a.MoveFolderAsync(r, ct); return Results.NoContent(); }));
        app.MapPost("/acl/folder/copy", (CopyFolderRequest r, AclService a, CancellationToken ct) =>
            Guard(async () => { await a.CopyFolderAsync(r, ct); return Results.NoContent(); }));
        app.MapDelete("/acl/folder", (string share, string path, bool? file, AclService a, CancellationToken ct) =>
            Guard(async () => { await a.DeleteFolderAsync(share, path, file == true, ct); return Results.NoContent(); }));
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

        app.MapGet("/smart", (SmartService s, CancellationToken ct) => Read(async () => Results.Ok(await s.ListAsync(ct))));

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
