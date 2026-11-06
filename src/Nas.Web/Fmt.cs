using System.Globalization;
using Nas.Web.Localization;

namespace Nas.Web;

public static class Fmt
{
    /// <summary>Taille en unités binaires ; « TB GB MB KB B » en anglais, « To Go Mo Ko o » en français.</summary>
    public static string Size(long bytes, string lang = "en")
    {
        var fr = Translator.Normalize(lang) == "fr";
        var culture = CultureInfo.GetCultureInfo(fr ? "fr" : "en");
        string[] unit = fr ? ["To", "Go", "Mo", "Ko", "o"] : ["TB", "GB", "MB", "KB", "B"];
        return bytes switch
        {
            >= 1L << 40 => string.Create(culture, $"{bytes / (double)(1L << 40):0.0} {unit[0]}"),
            >= 1L << 30 => string.Create(culture, $"{bytes / (double)(1L << 30):0.0} {unit[1]}"),
            >= 1L << 20 => string.Create(culture, $"{bytes / (double)(1L << 20):0.0} {unit[2]}"),
            >= 1L << 10 => string.Create(culture, $"{bytes / 1024.0:0.0} {unit[3]}"),
            _ => $"{bytes} {unit[4]}",
        };
    }

    /// <summary>Débit : « 581 B/s », « 3.2 KB/s » (« 581 o/s », « 3,2 Ko/s » en français).</summary>
    public static string Rate(double bytesPerSec, string lang = "en") => Size((long)Math.Max(0, bytesPerSec), lang) + "/s";

    /// <summary>Date UTC affichée à l'heure locale du serveur.</summary>
    public static string Dt(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
