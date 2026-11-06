using System.Globalization;
using System.Text.RegularExpressions;

namespace Nas.Web.Localization;

/// <summary>
/// Traduit les textes de l'application. La langue source est l'anglais : un texte sans traduction reste en anglais,
/// ce qui sert de repli pour toute langue non reconnue. Les modèles à trous (« Pool {0} not found. ») sont reconnus
/// aussi dans un message déjà formaté (« Pool tank not found. »), même s'il vient de l'agent ou de la base.
/// </summary>
public static class Translator
{
    public static readonly IReadOnlyList<string> Supported = ["en", "fr"];

    /// <summary>"fr", "fr-CA", "FR" → "fr" ; tout le reste (langue inconnue, vide) → "en".</summary>
    public static string Normalize(string? language) =>
        language is not null && language.Trim().StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";

    sealed record Template(Regex Pattern, string Target, int Literal);

    static readonly Lazy<List<Template>> FrenchTemplates = new(() => BuildTemplates(French.Table));

    static List<Template> BuildTemplates(IReadOnlyDictionary<string, string> table) =>
        table.Where(kv => PlaceholderRegex.IsMatch(kv.Key))
            .Select(kv =>
            {
                var parts = PlaceholderRegex.Split(kv.Key); // alterne texte, {n}, texte, {n}…
                var pattern = string.Concat(parts.Select(p => PlaceholderRegex.IsMatch(p)
                    ? $"(?<g{p.Trim('{', '}')}>.+?)" : Regex.Escape(p)));
                return new Template(new Regex($"^{pattern}$", RegexOptions.Singleline | RegexOptions.CultureInvariant),
                    kv.Value, parts.Where(p => !PlaceholderRegex.IsMatch(p)).Sum(p => p.Length));
            })
            .OrderByDescending(t => t.Literal) // le modèle le plus précis d'abord
            .ToList();

    static readonly Regex PlaceholderRegex = new(@"(\{\d+\})", RegexOptions.Compiled);
    static readonly Regex TargetPlaceholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    /// <summary>Traduit un texte source (exact) ou un message déjà formaté qui correspond à un modèle connu.</summary>
    public static string Translate(string? text, string language)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (Normalize(language) != "fr") return text;
        return TranslateFrench(text);
    }

    static string TranslateFrench(string text)
    {
        if (French.Table.TryGetValue(text, out var exact)) return exact;
        foreach (var t in FrenchTemplates.Value)
        {
            var m = t.Pattern.Match(text);
            if (!m.Success) continue;
            // Les valeurs insérées peuvent être elles-mêmes des textes connus (ex. « Creating the group »).
            return TargetPlaceholder.Replace(t.Target, g => TranslateFrench(m.Groups["g" + g.Groups[1].Value].Value));
        }
        return text;
    }

    /// <summary>Texte d'interface avec arguments : <c>Format("Delete {0}?", "fr", name)</c>.</summary>
    public static string Format(string template, string language, params object?[] args)
    {
        var lang = Normalize(language);
        var text = lang == "fr" && French.Table.TryGetValue(template, out var fr) ? fr : template;
        return args.Length == 0 ? text : string.Format(CultureInfo.GetCultureInfo(lang), text, args);
    }
}
