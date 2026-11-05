using Nas.Agent.Services;

namespace Nas.Agent;

public partial class Program
{
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

        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/disks", async (IDiskProvider disks, CancellationToken ct) => Results.Ok(await disks.ListAsync(ct)));

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
