using MudBlazor;

namespace Nas.Web;

/// <summary>
/// Un préréglage de couleurs. <paramref name="Name"/> est le texte anglais, traduit à l'affichage.
/// La couleur principale pilote la barre du haut, les boutons, les icônes et le fond d'écran par défaut ;
/// la variante sombre est plus claire pour rester lisible sur fond sombre.
/// </summary>
public sealed record ThemePreset(string Id, string Name, string Light, string Dark)
{
    public static readonly ThemePreset Default = new("indigo", "Indigo", "#594AE2", "#776BE7");

    public static readonly IReadOnlyList<ThemePreset> All =
    [
        Default,
        new("ocean", "Ocean", "#0277BD", "#4FB3F0"),
        new("teal", "Lagoon", "#00796B", "#4DB6AC"),
        new("forest", "Forest", "#2E7D32", "#66BB6A"),
        new("amber", "Amber", "#D84315", "#FF8A65"),
        new("rose", "Rose", "#C2185B", "#F06292"),
        new("violet", "Violet", "#7B1FA2", "#BA68C8"),
        new("graphite", "Graphite", "#455A64", "#90A4AE"),
    ];

    /// <summary>Le préréglage d'identifiant donné, ou celui par défaut si l'identifiant est inconnu (cookie périmé).</summary>
    public static ThemePreset Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Default;

    /// <summary>Thème MudBlazor correspondant : seule la couleur principale change, le reste suit les valeurs par défaut.</summary>
    public MudTheme ToMudTheme() => new()
    {
        PaletteLight = new PaletteLight { Primary = Light, AppbarBackground = Light },
        PaletteDark = new PaletteDark { Primary = Dark },
    };
}
