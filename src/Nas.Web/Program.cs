using System.Net.Sockets;
using MudBlazor.Services;
using Nas.Web;
using Nas.Web.Components;
using Nas.Web.Data;

var b = WebApplication.CreateBuilder(args);
b.Services.AddRazorComponents().AddInteractiveServerComponents();
b.Services.AddMudServices();
b.Services.AddScoped<ThemeState>();
b.Services.AddSingleton<Db>();
b.Services.AddSingleton<Repo>();

// L'interface ne touche jamais au système : elle parle à l'agent par socket Unix (ou par URL en développement).
var socketPath = b.Configuration["Agent:SocketPath"];
var agentUrl = b.Configuration["Agent:Url"];
b.Services.AddHttpClient<AgentClient>(c => c.BaseAddress = new Uri(agentUrl ?? "http://agent/"))
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var handler = new SocketsHttpHandler();
        if (string.IsNullOrWhiteSpace(agentUrl) && !string.IsNullOrWhiteSpace(socketPath))
        {
            handler.ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }
        return handler;
    });

var app = b.Build();
app.Services.GetRequiredService<Db>().Init();
await app.Services.GetRequiredService<Repo>().AuditAsync("système", "Démarrage", "Interface UrsaNAS démarrée.");
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
