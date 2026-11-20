using MudBlazor;
using Nas.Web;

namespace Nas.Agent.Tests;

public class ThemePresetTests
{
    static string Hex(MudBlazor.Utilities.MudColor c) => c.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex).ToUpperInvariant();

    [Fact]
    public void Chaque_prereglage_se_retrouve_par_son_identifiant()
    {
        foreach (var p in ThemePreset.All) Assert.Same(p, ThemePreset.Find(p.Id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("inconnu")]
    [InlineData("indigo")]        // ancien identifiant du préréglage par défaut
    [InlineData("mudblazor")]     // identifiant d'une version intermédiaire
    [InlineData("custom-1565C0")] // ancien cookie de la couleur personnalisée, retirée
    public void Un_identifiant_inconnu_donne_le_prereglage_par_defaut(string? id) =>
        Assert.Equal(ThemePreset.Default, ThemePreset.Find(id));

    [Fact]
    public void Le_prereglage_par_defaut_est_la_palette_d_origine_de_MudBlazor()
    {
        Assert.True(ThemePreset.Default.IsStock);
        Assert.Single(ThemePreset.All, p => p.IsStock);
        var theme = ThemePreset.Default.ToMudTheme();
        var original = new MudTheme();
        Assert.Equal(Hex(original.PaletteLight.Primary), Hex(theme.PaletteLight.Primary));
        Assert.Equal(Hex(original.PaletteLight.Background), Hex(theme.PaletteLight.Background));
        Assert.Equal(Hex(original.PaletteDark.Background), Hex(theme.PaletteDark.Background));
        Assert.Equal(Hex(original.PaletteDark.Surface), Hex(theme.PaletteDark.Surface));
    }

    [Fact]
    public void Chaque_prereglage_change_aussi_les_fonds_pas_seulement_la_couleur_principale()
    {
        var light = ThemePreset.All.Select(p => Hex(p.ToMudTheme().PaletteLight.Background)).ToList();
        var dark = ThemePreset.All.Select(p => Hex(p.ToMudTheme().PaletteDark.Surface)).ToList();
        Assert.Equal(ThemePreset.All.Count, light.Distinct().Count());
        Assert.Equal(ThemePreset.All.Count, dark.Distinct().Count());
    }

    [Fact]
    public void Les_prereglages_teintes_ont_un_fond_teinte_et_une_barre_de_la_couleur_principale()
    {
        foreach (var p in ThemePreset.All.Where(p => !p.IsStock))
        {
            var t = p.ToMudTheme();
            Assert.NotEqual("#FFFFFF", Hex(t.PaletteLight.Background));
            Assert.Equal(p.Light.ToUpperInvariant(), Hex(t.PaletteLight.Primary));
            Assert.Equal(p.Light.ToUpperInvariant(), Hex(t.PaletteLight.AppbarBackground));
        }
    }

    [Fact]
    public void Tint_melange_les_deux_couleurs()
    {
        Assert.Equal("#FFFFFF", ThemePreset.Tint("#FFFFFF", "#000000", 0));
        Assert.Equal("#000000", ThemePreset.Tint("#FFFFFF", "#000000", 1));
        Assert.Equal("#808080", ThemePreset.Tint("#FFFFFF", "#000000", 0.5));
    }
}
