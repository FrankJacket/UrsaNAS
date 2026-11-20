using MudBlazor;

namespace Nas.Web;

/// <summary>
/// Une fonction du bureau. <paramref name="Component"/> l'ouvre dans une fenêtre flottante ;
/// sans composant, l'icône mène à la page <paramref name="Href"/> (fonction pas encore passée en fenêtre).
/// </summary>
/// <param name="Id">Identifiant stable : une seule fenêtre par fonction.</param>
/// <param name="Title">Texte anglais, traduit à l'affichage.</param>
/// <param name="Category">Texte anglais du groupe d'icônes, traduit à l'affichage.</param>
public sealed record DesktopApp(
    string Id, string Title, string Category, string Icon, string Href,
    Type? Component = null, int Width = 1000, int Height = 640, bool NeedsDocker = false);

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
        new("network", "Network", "System", Icons.Material.Filled.Router, "/network"),
        new("vms", "Virtual machines", "System", Icons.Material.Filled.Computer, "/vms"),
        new("system-settings", "System settings", "System", Icons.Material.Filled.SystemUpdateAlt, "/system-settings"),

        new("disks", "Disks", "Storage", Icons.Material.Filled.Dns, "/disks", typeof(Components.Pages.Disks), 1280, 800),
        new("pools", "ZFS pools", "Storage", Icons.Material.Filled.Layers, "/pools"),
        new("datasets", "Datasets", "Storage", Icons.Material.Filled.FolderCopy, "/datasets"),
        new("remote-storage", "Remote storage", "Storage", Icons.Material.Filled.CloudSync, "/remote-storage"),
        new("backups", "Backups", "Storage", Icons.Material.Filled.Backup, "/backups"),

        new("shares", "Shares", "Sharing", Icons.Material.Filled.Share, "/shares", typeof(Components.Pages.Shares), 1100, 640),
        new("services", "Services", "Sharing", Icons.Material.Filled.SettingsEthernet, "/services"),
        new("folders", "File Manager", "Sharing", Icons.Material.Filled.Folder, "/folders"),

        new("docker-containers", "Containers", "Docker", Icons.Material.Filled.Inventory2, "/docker/containers", NeedsDocker: true),
        new("docker-images", "Images", "Docker", Icons.Material.Filled.Album, "/docker/images", NeedsDocker: true),
        new("docker-stacks", "Stacks", "Docker", Icons.Material.Filled.Layers, "/docker/stacks", NeedsDocker: true),
        new("docker-volumes", "Volumes and networks", "Docker", Icons.Material.Filled.Lan, "/docker/volumes", NeedsDocker: true),

        new("users", "Users", "Accounts", Icons.Material.Filled.People, "/users"),
        new("groups", "Groups", "Accounts", Icons.Material.Filled.Groups, "/groups"),
        new("admins", "Administrators", "Accounts", Icons.Material.Filled.AdminPanelSettings, "/admins"),

        new("alerts", "Alerts", "Monitoring", Icons.Material.Filled.NotificationsActive, "/alerts"),
        new("journal", "Audit log", "Monitoring", Icons.Material.Filled.History, "/journal"),
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
