namespace Nas.Web;

/// <summary>
/// Navigation interne de la fenêtre Docker : les pages demandent une section (« containers », « images », « stacks »,
/// « volumes », « status ») au lieu de quitter la fenêtre. Absente quand la page est ouverte seule par son adresse.
/// </summary>
public sealed class DockerNav(Action<string> go)
{
    public void Go(string section) => go(section);
}
