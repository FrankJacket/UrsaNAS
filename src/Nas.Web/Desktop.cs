using MudBlazor;

namespace Nas.Web;

/// <summary>
/// Une fonction du bureau. <paramref name="Component"/> l'ouvre dans une fenêtre flottante ;
/// <paramref name="Href"/> est la page autonome de la fonction (adresse directe, signets).
/// </summary>
/// <param name="Id">Identifiant stable : une seule fenêtre par fonction.</param>
/// <param name="Title">Texte anglais, traduit à l'affichage.</param>
/// <param name="Category">Texte anglais du groupe d'icônes, traduit à l'affichage.</param>
/// <param name="Flush">Fenêtre sans marge intérieure : le contenu (terminal, fenêtre à barre latérale) gère sa propre mise en page.</param>
public sealed record DesktopApp(
    string Id, string Title, string Category, string Icon, string Href,
    Type? Component = null, int Width = 1000, int Height = 640, bool NeedsDocker = false, bool Flush = false);

/// <summary>Une fenêtre ouverte sur le bureau.</summary>
public sealed class DesktopWindow(DesktopApp app, int slot)
{
    public DesktopApp App { get; } = app;
    /// <summary>Rang d'ouverture (0, 1, 2…) : décale les fenêtres en cascade au lieu de les empiler au même endroit.</summary>
    public int Slot { get; } = slot;
    public bool Minimized { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// Fenêtres ouvertes du bureau, pour la session (le circuit) : elles survivent aux changements de page
/// puisque leur hôte vit dans la mise en page.
/// </summary>
public sealed class DesktopState
{
    /// <summary>Les fonctions du bureau, dans l'ordre d'affichage. Les groupes reprennent l'ancien menu latéral.</summary>
    public static readonly IReadOnlyList<DesktopApp> Apps =
    [
        new("dashboard", "Dashboard", "System", Icons.Material.Filled.Dashboard, "/dashboard", typeof(Components.Pages.Dashboard), 1100, 760),
        new("network", "Network", "System", Icons.Material.Filled.Router, "/network", typeof(Components.Pages.Network), 1000, 700),
        new("terminal", "Terminal", "System", Icons.Material.Filled.Terminal, "/terminal/window", typeof(Components.Pages.TerminalView), 960, 580, Flush: true),
        new("appearance", "Appearance", "System", Icons.Material.Filled.Palette, "/appearance", typeof(Components.Pages.Appearance), 760, 520),
        new("system-settings", "System settings", "System", Icons.Material.Filled.SystemUpdateAlt, "/system-settings", typeof(Components.Pages.SystemSettings), 900, 680),

        new("disks", "Disks", "Storage", Icons.Material.Filled.Dns, "/disks", typeof(Components.Pages.Disks), 1280, 800),
        new("pools", "ZFS pools", "Storage", Icons.Material.Filled.Layers, "/pools", typeof(Components.Pages.Pools), 1100, 640),
        new("datasets", "Datasets", "Storage", Icons.Material.Filled.FolderCopy, "/datasets", typeof(Components.Pages.Datasets), 1200, 700),
        new("remote-storage", "Remote storage", "Storage", Icons.Material.Filled.CloudSync, "/remote-storage", typeof(Components.Pages.RemoteStoragePage), 1100, 640),
        new("backups", "Backups", "Storage", Icons.Material.Filled.Backup, "/backups", typeof(Components.Pages.Backups), 1200, 700),

        new("shares", "Shares", "Sharing", Icons.Material.Filled.Share, "/shares", typeof(Components.Pages.Shares), 1100, 640),
        new("services", "Services", "Sharing", Icons.Material.Filled.SettingsEthernet, "/services", typeof(Components.Pages.Services), 1000, 640),
        new("folders", "File Manager", "Sharing", Icons.Material.Filled.Folder, "/folders", typeof(Components.Pages.Folders), 1300, 780),

        new("vms", "Virtual machines", "Virtualization", Icons.Material.Filled.Computer, "/vms", typeof(Components.Pages.VmsWindow), 1180, 720, Flush: true),
        new("docker", "Docker", "Virtualization", Icons.Material.Filled.ViewInAr, "/docker", typeof(Components.Pages.DockerWindow), 1180, 720, NeedsDocker: true, Flush: true),

        new("users", "Users", "Accounts", Icons.Material.Filled.People, "/users", typeof(Components.Pages.Users), 1000, 620),
        new("groups", "Groups", "Accounts", Icons.Material.Filled.Groups, "/groups", typeof(Components.Pages.Groups), 900, 600),
        new("admins", "Administrators", "Accounts", Icons.Material.Filled.AdminPanelSettings, "/admins", typeof(Components.Pages.Admins), 800, 560),

        new("alerts", "Alerts", "Monitoring", Icons.Material.Filled.NotificationsActive, "/alerts", typeof(Components.Pages.Alerts), 1000, 620),
        new("journal", "Audit log", "Monitoring", Icons.Material.Filled.History, "/journal", typeof(Components.Pages.Journal), 1200, 700),
    ];

    readonly List<DesktopWindow> _windows = [];
    int _opened;

    public IReadOnlyList<DesktopWindow> Windows => _windows;

    /// <summary>Déclenché à chaque ouverture, fermeture, réduction ou agrandissement.</summary>
    public event Action? Changed;

    /// <summary>Demande à l'hôte de remettre une fenêtre au premier plan.</summary>
    public event Action<string>? FrontRequested;

    /// <summary>Ouvre la fenêtre de la fonction, ou ramène celle qui existe déjà (restaurée et au premier plan).</summary>
    public void Open(DesktopApp app)
    {
        var existing = _windows.FirstOrDefault(w => w.App.Id == app.Id);
        if (existing is null)
        {
            _windows.Add(new DesktopWindow(app, _opened++ % 8));
            Changed?.Invoke();
        }
        else Show(existing.App.Id);
    }

    /// <summary>Restaure la fenêtre si elle est réduite, puis la place au premier plan.</summary>
    public void Show(string id)
    {
        var window = _windows.FirstOrDefault(w => w.App.Id == id);
        if (window is null) return;
        if (window.Minimized)
        {
            window.Minimized = false;
            Changed?.Invoke(); // le composant se place lui-même au premier plan en revenant
        }
        else FrontRequested?.Invoke(id);
    }

    public void Close(string id)
    {
        if (_windows.RemoveAll(w => w.App.Id == id) > 0) Changed?.Invoke();
    }

    public void SetMinimized(string id, bool value) => Update(id, w => w.Minimized = value);

    public void SetMaximized(string id, bool value) => Update(id, w => w.Maximized = value);

    void Update(string id, Action<DesktopWindow> change)
    {
        var window = _windows.FirstOrDefault(w => w.App.Id == id);
        if (window is null) return;
        change(window);
        Changed?.Invoke();
    }
}
