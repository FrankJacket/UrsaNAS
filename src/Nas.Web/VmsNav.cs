namespace Nas.Web;

/// <summary>
/// Navigation interne de la fenêtre « Machines virtuelles » : liste, détail d'une machine, console.
/// Les pages la reçoivent en cascade ; sans elle (page ouverte seule par son adresse), elles gardent leurs liens habituels.
/// </summary>
public sealed class VmsNav(Action<string, string?> go)
{
    public void ShowList() => go("list", null);
    public void ShowDetail(string name) => go("detail", name);
    public void ShowConsole(string name) => go("console", name);
}
