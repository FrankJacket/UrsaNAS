using System.Net.Sockets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using System.Globalization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Localization;
using MudBlazor.Services;
using Nas.Web;
using Nas.Web.Alerting;
using Nas.Web.Auth;
using Nas.Web.Components;
using Nas.Web.Data;
using Nas.Web.Localization;

var b = WebApplication.CreateBuilder(args);
b.Services.AddRazorComponents().AddInteractiveServerComponents();
b.Services.AddMudServices();
b.Services.AddScoped<ThemeState>();
b.Services.AddSingleton<Db>();
b.Services.AddSingleton<Repo>();
b.Services.AddScoped<NasOperations>();
b.Services.AddScoped<Loc>();

// Authentification : cookie, tout est protégé par défaut (seules /login et /setup sont publiques).
b.Services.AddSingleton<AdminAccounts>();
b.Services.AddSingleton<LoginThrottle>();
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

b.Services.AddSingleton<DockerAvailability>();
b.Services.AddHostedService<DockerMonitor>();
b.Services.AddScoped<DockerOperations>();
b.Services.AddScoped<NasFolderPicker>();

b.Services.AddSingleton<BackupRepo>();
b.Services.AddScoped<BackupCoordinator>();
b.Services.AddScoped<BackupOperations>();
b.Services.AddHostedService<BackupMonitor>();

// L'interface ne touche jamais au système : elle parle à l'agent par socket Unix (ou par URL en développement).
var socketPath = b.Configuration["Agent:SocketPath"];
var agentUrl = b.Configuration["Agent:Url"];
b.Services.AddHttpClient<AgentClient>(c =>
    {
        c.BaseAddress = new Uri(agentUrl ?? "http://agent/");
        c.Timeout = TimeSpan.FromMinutes(15); // télécharger une image Docker ou monter une stack peut prendre plusieurs minutes
    })
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
await app.Services.GetRequiredService<Repo>().AuditAsync("system", "Startup", "UrsaNAS interface started.");

// Langue : cookie choisi par l'utilisateur, sinon langue du navigateur, sinon anglais (aussi pour toute langue non prise en charge).
var cultures = Translator.Supported.Select(c => new CultureInfo(c)).ToList();
var localization = new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("en"),
    SupportedCultures = cultures,
    SupportedUICultures = cultures,
};
localization.RequestCultureProviders.Insert(0, new CookieRequestCultureProvider { CookieName = Loc.Cookie });
app.UseRequestLocalization(localization);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous(); // feuilles de style et scripts : nécessaires à la page de connexion
app.MapGet("/lang/{code}", (string code, string? returnUrl, HttpContext ctx) =>
{
    ctx.Response.Cookies.Append(Loc.Cookie, CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(Translator.Normalize(code))),
        new CookieOptions { Path = "/", MaxAge = TimeSpan.FromDays(365), SameSite = SameSiteMode.Lax });
    // Seules les adresses locales sont acceptées (pas de redirection vers un autre site).
    var local = !string.IsNullOrEmpty(returnUrl) && returnUrl[0] == '/' && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\");
    return Results.Redirect(local ? returnUrl! : "/");
}).AllowAnonymous();
app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.Redirect("/login");
}).AllowAnonymous();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
