namespace Nas.Web;

/// <summary>
/// Thème de la session : mode clair ou sombre et préréglage de couleurs.
/// Les deux choix sont mémorisés dans des cookies du navigateur ("ursanas-theme", "ursanas-color").
/// </summary>
public class ThemeState
{
    public bool IsDark { get; private set; }
    public ThemePreset Preset { get; private set; } = ThemePreset.Default;

    /// <summary>Thème MudBlazor du préréglage courant (recalculé seulement quand le préréglage change).</summary>
    public MudBlazor.MudTheme MudTheme { get; private set; } = ThemePreset.Default.ToMudTheme();

    /// <summary>Fond d'écran du bureau : "default" (dégradé du thème), l'identifiant d'un fond fourni, ou "custom" (image envoyée).</summary>
    public string Wallpaper { get; private set; } = Nas.Web.Wallpaper.DefaultId;

    public event Action? Changed;

    /// <summary>L'image envoyée a changé sans que le choix change : redessine le bureau.</summary>
    public void RefreshWallpaper() => Changed?.Invoke();

    public void SetWallpaper(string id)
    {
        if (Wallpaper == id) return;
        Wallpaper = id;
        Changed?.Invoke();
    }

    public void Set(bool dark)
    {
        if (IsDark == dark) return;
        IsDark = dark;
        Changed?.Invoke();
    }

    public void SetPreset(ThemePreset preset)
    {
        if (Preset == preset) return;
        Preset = preset;
        MudTheme = preset.ToMudTheme();
        Changed?.Invoke();
    }
}
