using System.Net.Sockets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor.Services;
using Nas.Web;
using Nas.Web.Alerting;
using Nas.Web.Auth;
using Nas.Web.Components;
using Nas.Web.Data;

var b = WebApplication.CreateBuilder(args);
b.Services.AddRazorComponents().AddInteractiveServerComponents();
b.Services.AddMudServices();
b.Services.AddScoped<ThemeState>();
b.Services.AddSingleton<Db>();
b.Services.AddSingleton<Repo>();
b.Services.AddScoped<NasOperations>();

// Authentification : cookie, tout est protégé par défaut (seules /login et /setup sont publiques).
b.Services.AddSingleton<AdminAccounts>();
b.Services.AddSingleton<LoginThrottle>();
b.Services.AddSingleton<SetupToken>();
b.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "ursanas-auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // l'interface est en HTTP sur le réseau local ; derrière HTTPS le cookie devient « Secure »
        o.LoginPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = async ctx =>
        {
            // Compte supprimé ou mot de passe changé depuis l'ouverture de cette session : on la refuse.
            if (!await ctx.HttpContext.RequestServices.GetRequiredService<AdminAccounts>().IsStillValidAsync(ctx.Principal!))
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync();
            }
        };
    });
b.Services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
b.Services.AddCascadingAuthenticationState();
b.Services.AddScoped<AuthenticationStateProvider, AdminRevalidatingStateProvider>();

b.Services.AddSingleton<IMailSender, MailKitSender>();
b.Services.AddSingleton<AlertState>();
b.Services.AddSingleton<AlertService>();
b.Services.AddSingleton<AlertMonitor>();
b.Services.AddHostedService(sp => sp.GetRequiredService<AlertMonitor>());

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

// Premier démarrage : aucun compte, donc un jeton d'installation est exigé pour créer le premier administrateur.
if (await app.Services.GetRequiredService<AdminAccounts>().CountAsync() == 0)
{
    var setup = app.Services.GetRequiredService<SetupToken>();
    var token = setup.Create(app.Services.GetRequiredService<Db>().DataDir);
    app.Logger.LogWarning("Aucun administrateur. Ouvre /setup et saisis le jeton d'installation : {Token} (aussi dans {File})", token, setup.FilePath);
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous(); // feuilles de style et scripts : nécessaires à la page de connexion
app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Redirect("/login");
}).AllowAnonymous();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
