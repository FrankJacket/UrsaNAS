using System.Text.RegularExpressions;
using Nas.Contracts;
using Nas.Web.Localization;
using Xunit;

namespace Nas.Agent.Tests;

public partial class LocalizationTests
{
    [Theory]
    [InlineData("fr", "fr")]
    [InlineData("fr-CA", "fr")]
    [InlineData("FR", "fr")]
    [InlineData("en", "en")]
    [InlineData("de", "en")]
    [InlineData("", "en")]
    [InlineData(null, "en")]
    public void Une_langue_inconnue_retombe_sur_l_anglais(string? input, string expected) =>
        Assert.Equal(expected, Translator.Normalize(input));

    [Fact]
    public void L_anglais_et_les_langues_inconnues_gardent_le_texte_source()
    {
        Assert.Equal("Delete", Translator.Translate("Delete", "en"));
        Assert.Equal("Delete", Translator.Translate("Delete", "de"));
        Assert.Equal("Something never translated", Translator.Translate("Something never translated", "fr"));
    }

    [Fact]
    public void Un_texte_exact_est_traduit_en_francais() =>
        Assert.Equal("Supprimer", Translator.Translate("Delete", "fr"));

    [Fact]
    public void Un_message_deja_formate_est_reconnu_et_ses_valeurs_sont_conservees() =>
        Assert.Equal("Le pool « tank » n'existe pas.", Translator.Translate(Msg.F("Pool “{0}” does not exist.", "tank"), "fr"));

    [Fact]
    public void Les_valeurs_inserees_sont_traduites_a_leur_tour() =>
        Assert.Equal("Création du pool a échoué : cannot create 'x'",
            Translator.Translate(Msg.F("{0} failed: {1}", "Creating the pool", "cannot create 'x'"), "fr"));

    [Fact]
    public void Un_detail_compose_de_plusieurs_phrases_est_traduit()
    {
        var text = Msg.F("{0} Affected devices: {1}.", "The pool is down or inaccessible.", "sdb (OFFLINE)");
        Assert.Equal("Le pool est en panne ou inaccessible. Périphérique(s) concerné(s) : sdb (OFFLINE).", Translator.Translate(text, "fr"));
    }

    [Fact]
    public void Format_remplace_les_valeurs_dans_la_langue_choisie()
    {
        Assert.Equal("Delete bob?", Translator.Format("Delete {0}?", "en", "bob"));
        Assert.Equal("Supprimer bob ?", Translator.Format("Delete {0}?", "fr", "bob"));
    }

    [Fact]
    public void Les_tailles_suivent_la_langue()
    {
        Assert.Equal("1.5 GB", Nas.Web.Fmt.Size(1610612736, "en"));
        Assert.Equal("1,5 Go", Nas.Web.Fmt.Size(1610612736, "fr"));
    }

    [Fact]
    public void Un_message_de_validation_est_traduisible()
    {
        var english = Rules.ValidatePassword("abc")!;
        Assert.Equal("The password must be at least 8 characters long.", english);
        Assert.Equal("Le mot de passe doit contenir au moins 8 caractères.", Translator.Translate(english, "fr"));
    }

    /// <summary>Tout texte source du code (L["…"] et Msg.F("…")) doit avoir sa traduction française.</summary>
    [Fact]
    public void Chaque_texte_source_a_une_traduction_francaise()
    {
        var root = FindRoot();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".razor")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (var regex in new[] { UiKey(), MsgKey() })
                foreach (Match m in regex.Matches(text))
                {
                    var key = Unescape(m.Groups[1].Value);
                    if (!French.Table.ContainsKey(key)) missing.Add($"{Path.GetFileName(file)}: {key}");
                }
        }
        Assert.True(missing.Count == 0, "Textes sans traduction française :\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Les_traductions_gardent_les_memes_trous_que_le_texte_source()
    {
        var placeholders = new Regex(@"\{\d+\}");
        foreach (var (en, fr) in French.Table)
            Assert.True(placeholders.Matches(en).Select(m => m.Value).Order().SequenceEqual(placeholders.Matches(fr).Select(m => m.Value).Order()),
                $"Trous différents : « {en} » / « {fr} »");
    }

    static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\\\", "\\");

    [GeneratedRegex("""\bL\["((?:[^"\\]|\\.)*)"[\],]""")]
    private static partial Regex UiKey();

    [GeneratedRegex("""\bMsg\.F\("((?:[^"\\]|\\.)*)"[\),]""")]
    private static partial Regex MsgKey();

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UrsaNAS.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("UrsaNAS.slnx introuvable");
    }
}
