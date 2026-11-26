using System.Net.Sockets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using System.Globalization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Localization;
using MudBlazor.Services;
using Nas.Contracts;
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
b.Services.AddScoped<DesktopState>();
b.Services.AddSingleton<Db>();
b.Services.AddSingleton<Repo>();
b.Services.AddSingleton<WallpaperStore>();
b.Services.AddScoped<NasOperations>();
b.Services.AddScoped<Loc>();

// Authentification : cookie, tout est protégé par défaut (seules /login et /setup sont publiques).
b.Services.AddScoped<IUserDirectory, AgentUserDirectory>();
b.Services.AddScoped<AdminAccounts>();
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
b.Services.AddHostedService<ShareSync>();

b.Services.AddSingleton<DockerAvailability>();
b.Services.AddHostedService<DockerMonitor>();
b.Services.AddScoped<DockerOperations>();
b.Services.AddScoped<VmOperations>();
b.Services.AddScoped<DiskOperations>();
b.Services.AddScoped<RemoteOperations>();
b.Services.AddScoped<NetworkOperations>();
b.Services.AddScoped<PowerOperations>();
b.Services.AddScoped<UpdateOperations>();
b.Services.AddScoped<UpdateScheduler>();
b.Services.AddHostedService<UpdateMonitor>();
b.Services.AddScoped<ConfigBackupOperations>();
b.Services.AddScoped<NasFolderPicker>();

b.Services.AddSingleton<BackupRepo>();
b.Services.AddScoped<BackupCoordinator>();
b.Services.AddScoped<BackupOperations>();
b.Services.AddHostedService<BackupMonitor>();

b.Services.AddSingleton<ScheduledTaskRepo>();
b.Services.AddScoped<ScheduledTaskCoordinator>();
b.Services.AddScoped<ScheduledTaskOperations>();
b.Services.AddHostedService<ScheduledTaskMonitor>();

// L'interface ne touche jamais au système : elle parle à l'agent par socket Unix (ou par URL en développement).
var agentUrl = b.Configuration["Agent:Url"];
b.Services.AddHttpClient<AgentClient>(c =>
    {
        c.BaseAddress = new Uri(agentUrl ?? "http://agent/");
        c.Timeout = TimeSpan.FromMinutes(15); // télécharger une image Docker ou monter une stack peut prendre plusieurs minutes
    })
    .ConfigurePrimaryHttpMessageHandler(() => AgentTransport.CreateHandler(b.Configuration));

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

app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous(); // feuilles de style et scripts : nécessaires à la page de connexion
app.MapGet("/wallpaper", (WallpaperStore store) => store.Current() is { } c ? Results.File(c.Path, c.ContentType) : Results.NotFound());

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
app.MapPost("/downloads/config-backup", async (ExportConfigRequest req, ConfigBackupOperations ops, HttpContext ctx) =>
{
    try
    {
        var actor = ctx.User.Identity?.Name ?? "unknown";
        var zip = await ops.ExportAsync(req.Password, actor, ctx.RequestAborted);
        return Results.Bytes(zip, "application/zip", $"ursanas-config-{DateTime.Now:yyyyMMdd-HHmm}.zip");
    }
    catch (AgentException e) { return Results.BadRequest(e.Message); }
});
app.MapPost("/downloads/audit-csv", async (Repo repo, HttpContext ctx) =>
{
    var bytes = AuditCsv.Bytes(await repo.GetAllAuditAsync());
    await repo.AuditAsync(ctx.User.Identity?.Name ?? "unknown", "Audit log downloaded");
    return Results.Bytes(bytes, "text/csv; charset=utf-8", $"ursanas-audit-{DateTime.Now:yyyyMMdd-HHmm}.csv");
});
app.MapGet("/vm-console/{name}", (string name, HttpContext ctx, AgentClient agent) => VmConsoleProxy.HandleAsync(name, ctx, agent));
app.MapGet("/terminal-ws", (HttpContext ctx, IConfiguration cfg, Repo repo) => TerminalProxy.HandleAsync(ctx, cfg, repo));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
