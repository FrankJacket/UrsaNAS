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

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        var socketPath = builder.Configuration["Agent:SocketPath"];
        if (!string.IsNullOrWhiteSpace(socketPath))
        {
            builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socketPath));
        }

        builder.Services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
        if (builder.Configuration["URSANAS_FAKE"] == "1")
            builder.Services.AddSingleton<IDiskProvider, FakeDiskProvider>();
        else
            builder.Services.AddSingleton<IDiskProvider, LsblkDiskProvider>();

        builder.Services.AddSingleton<UserService>();
        builder.Services.AddSingleton<ShareService>();

        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/disks", async (IDiskProvider disks, CancellationToken ct) => Results.Ok(await disks.ListAsync(ct)));

        app.MapGet("/users", async (UserService u, CancellationToken ct) => Results.Ok(await u.ListAsync(ct)));
        app.MapPost("/users", (CreateUserRequest r, UserService u, CancellationToken ct) =>
            Guard(async () => { await u.CreateAsync(r, ct); return Results.NoContent(); }));
        app.MapPut("/users/{name}/password", (string name, SetPasswordRequest r, UserService u, CancellationToken ct) =>
            Guard(async () => { await u.SetPasswordAsync(name, r.Password, ct); return Results.NoContent(); }));
        app.MapDelete("/users/{name}", (string name, UserService u, CancellationToken ct) =>
            Guard(async () => { await u.DeleteAsync(name, ct); return Results.NoContent(); }));
        app.MapPut("/shares", (ApplySharesRequest r, ShareService s, CancellationToken ct) =>
            Guard(async () => { await s.ApplyAsync(r.Shares, ct); return Results.NoContent(); }));

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
