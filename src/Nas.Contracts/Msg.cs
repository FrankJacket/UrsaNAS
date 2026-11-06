using System.Globalization;

namespace Nas.Contracts;

/// <summary>
/// Message destiné à l'utilisateur. Le texte source est toujours en anglais ; l'interface le traduit à l'affichage
/// (voir <c>Translator</c> dans Nas.Web) en reconnaissant le modèle, par exemple « Pool {0} not found. ».
/// Passer par <see cref="F"/> permet à un test de lister tous les modèles et de vérifier qu'ils sont traduits.
/// </summary>
public static class Msg
{
    public static string F(string template, params object?[] args) =>
        args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
}
