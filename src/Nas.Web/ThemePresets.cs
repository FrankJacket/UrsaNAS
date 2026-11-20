using MudBlazor;

namespace Nas.Web;

/// <summary>
/// Un préréglage de couleurs. <paramref name="Name"/> est le texte anglais, traduit à l'affichage.
/// <paramref name="Light"/> et <paramref name="Dark"/> sont la couleur principale en mode clair et en mode sombre (la variante sombre est
/// plus claire pour rester lisible). Le préréglage ne change pas que les boutons : il teinte aussi les fonds de page, les surfaces,
/// les cartes, les séparateurs et la barre du haut, pour que chaque palette se reconnaisse d'un coup d'œil.
/// </summary>
public sealed record ThemePreset(string Id, string Name, string Light, string Dark)
{
    /// <summary>La palette d'origine de MudBlazor, sans aucun réglage, présentée comme le choix « par défaut ». Les couleurs ci-dessous ne servent qu'à la pastille.</summary>
    public static readonly ThemePreset Default = new("default", "Default", "#594AE2", "#776BE7");

    public bool IsStock => Id == Default.Id;

    // Teintes de la palette Material de MudBlazor (page « Colors ») : « Darken » en mode clair, « Lighten » en mode sombre.
    public static readonly IReadOnlyList<ThemePreset> All =
    [
        Default,
        new("ocean", "Ocean", Colors.LightBlue.Darken3, Colors.LightBlue.Lighten2),
        new("teal", "Lagoon", Colors.Teal.Darken2, Colors.Teal.Lighten2),
        new("forest", "Forest", Colors.Green.Darken3, Colors.Green.Lighten1),
        new("amber", "Ember", Colors.DeepOrange.Darken3, Colors.DeepOrange.Lighten2),
        new("rose", "Rose", Colors.Pink.Darken2, Colors.Pink.Lighten2),
        new("violet", "Violet", Colors.Purple.Darken2, Colors.Purple.Lighten2),
        new("graphite", "Graphite", Colors.BlueGray.Darken2, Colors.BlueGray.Lighten2),
    ];

    /// <summary>Le préréglage d'identifiant donné, ou celui par défaut si l'identifiant est inconnu (cookie périmé).</summary>
    public static ThemePreset Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Default;

    /// <summary>Thème MudBlazor : la couleur principale et, autour d'elle, des fonds et des lignes teintés de cette couleur.
    /// Les couleurs d'état (succès, avertissement, erreur, information) et celles du texte restent celles de MudBlazor.</summary>
    public MudTheme ToMudTheme() => IsStock ? new MudTheme() : new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = Light,
            AppbarBackground = Light,
            Background = Tint("#FFFFFF", Light, 0.07),
            Surface = Tint("#FFFFFF", Light, 0.02),
            BackgroundGray = Tint("#FFFFFF", Light, 0.12),
            DrawerBackground = Tint("#FFFFFF", Light, 0.02),
            LinesDefault = Tint("#FFFFFF", Light, 0.22),
            TableLines = Tint("#FFFFFF", Light, 0.22),
            Divider = Tint("#FFFFFF", Light, 0.22),
        },
        PaletteDark = new PaletteDark
        {
            Primary = Dark,
            AppbarBackground = Tint("#1E1E1E", Dark, 0.24),
            Background = Tint("#121212", Dark, 0.08),
            Surface = Tint("#1E1E1E", Dark, 0.11),
            BackgroundGray = Tint("#181818", Dark, 0.14),
            DrawerBackground = Tint("#1E1E1E", Dark, 0.11),
            LinesDefault = Tint("#2E2E2E", Dark, 0.22),
            TableLines = Tint("#2E2E2E", Dark, 0.22),
            Divider = Tint("#2E2E2E", Dark, 0.22),
        },
    };

    /// <summary>La couleur <paramref name="baseHex"/> teintée de <paramref name="colorHex"/> à hauteur de <paramref name="amount"/> (0 à 1).</summary>
    public static string Tint(string baseHex, string colorHex, double amount)
    {
        var (br, bg, bb) = Parse(baseHex);
        var (cr, cg, cb) = Parse(colorHex);
        static int Mix(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);
        return $"#{Mix(br, cr, amount):X2}{Mix(bg, cg, amount):X2}{Mix(bb, cb, amount):X2}";
    }

    static (int R, int G, int B) Parse(string hex)
    {
        var v = Convert.ToInt32(hex.TrimStart('#'), 16);
        return ((v >> 16) & 255, (v >> 8) & 255, v & 255);
    }
}
