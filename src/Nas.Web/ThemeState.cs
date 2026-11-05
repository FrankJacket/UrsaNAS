namespace Nas.Web;

/// <summary>Thème clair ou sombre de la session (le choix est mémorisé dans le cookie "ursanas-theme").</summary>
public class ThemeState
{
    public bool IsDark { get; private set; }
    public event Action? Changed;

    public void Set(bool dark)
    {
        if (IsDark == dark) return;
        IsDark = dark;
        Changed?.Invoke();
    }
}
