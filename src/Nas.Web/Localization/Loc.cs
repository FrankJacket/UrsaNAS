using System.Globalization;

namespace Nas.Web.Localization;

/// <summary>
/// Textes de l'interface dans la langue de la requête (cookie <c>ursanas-lang</c>, sinon en-tête Accept-Language,
/// sinon anglais). Usage : <c>@L["Delete"]</c>, <c>@L["Delete {0}?", name]</c>, <c>@L.Translate(e.Message)</c>.
/// </summary>
public sealed class Loc
{
    public const string Cookie = "ursanas-lang";

    public string Lang => Translator.Normalize(CultureInfo.CurrentUICulture.Name);

    public string this[string text] => Translator.Format(text, Lang);
    public string this[string template, params object?[] args] => Translator.Format(template, Lang, args);

    /// <summary>Message déjà formaté (erreur de l'agent, entrée de journal, alerte) : traduit si on le reconnaît.</summary>
    public string Translate(string? text) => Translator.Translate(text, Lang);
}
