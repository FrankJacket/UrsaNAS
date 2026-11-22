using System.Globalization;
using System.Text;

namespace Nas.Web.Data;

/// <summary>
/// Export du journal en CSV (RFC 4180) : en-tête, virgule comme séparateur, fins de ligne CRLF, dates ISO 8601 en UTC.
/// Les cellules qui commencent par « = + - @ » sont précédées d'une apostrophe : ouvert dans un tableur, le texte d'une entrée
/// (un nom saisi par un utilisateur, par exemple) ne peut pas devenir une formule.
/// </summary>
public static class AuditCsv
{
    public static readonly string[] Header = ["Date (UTC)", "Author", "Action", "Detail"];

    public static string Build(IEnumerable<AuditRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Header.Select(Cell))).Append("\r\n");
        foreach (var r in rows)
        {
            sb.Append(Cell(DateTime.SpecifyKind(r.At, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))).Append(',')
              .Append(Cell(r.Actor)).Append(',')
              .Append(Cell(r.Action)).Append(',')
              .Append(Cell(r.Detail ?? "")).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Le fichier tel qu'il est envoyé : UTF-8 avec marque d'ordre des octets, pour que les tableurs lisent bien les accents.</summary>
    public static byte[] Bytes(IEnumerable<AuditRow> rows) => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(Build(rows))];

    static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
