namespace Nas.Web;

public static class Fmt
{
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.0} To",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} Mo",
        _ => $"{bytes} o",
    };

    /// <summary>Date UTC affichée à l'heure locale du serveur.</summary>
    public static string Dt(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
