using Nas.Web.Data;

namespace Nas.Web;

/// <summary>Fond d'écran proposé. <paramref name="Name"/> est le texte anglais, traduit à l'affichage ; <paramref name="Css"/> est une valeur de background.</summary>
public sealed record Wallpaper(string Id, string Name, string Css)
{
    public const string DefaultId = "default";
    public const string CustomId = "custom";

    public static readonly IReadOnlyList<Wallpaper> Builtin =
    [
        new("aurora", "Aurora", "radial-gradient(at 20% 15%, #3ddc97 0, transparent 45%), radial-gradient(at 80% 25%, #5b6cff 0, transparent 50%), radial-gradient(at 50% 90%, #8e2de2 0, transparent 55%), #0b1026"),
        new("dusk", "Dusk", "radial-gradient(at 15% 85%, #ff7e5f 0, transparent 50%), radial-gradient(at 85% 20%, #feb47b 0, transparent 40%), linear-gradient(160deg, #2b1055, #7597de)"),
        new("midnight", "Midnight", "radial-gradient(at 70% 20%, #243b6b 0, transparent 55%), linear-gradient(170deg, #05070f, #111a33)"),
        new("dune", "Dune", "radial-gradient(at 80% 10%, #ffe29f 0, transparent 45%), linear-gradient(160deg, #c2703d, #6b3a2b)"),
        new("mist", "Mist", "radial-gradient(at 25% 20%, #9bd1c4 0, transparent 50%), radial-gradient(at 80% 80%, #4c7a8c 0, transparent 55%), linear-gradient(160deg, #1d3b3a, #2f5d62)"),
    ];

    public static Wallpaper? Find(string? id) => Builtin.FirstOrDefault(w => w.Id == id);
}

/// <summary>
/// Image de fond envoyée par l'administrateur : une seule pour tout le NAS, conservée dans le dossier de données.
/// Seuls PNG, JPEG et WebP sont acceptés (le type est reconnu sur le contenu, pas sur le nom du fichier).
/// </summary>
public class WallpaperStore(Db db)
{
    public const long MaxBytes = 10 * 1024 * 1024;

    string Path_(string ext) => Path.Combine(db.DataDir, "wallpaper" + ext);

    /// <summary>Fichier courant et son type MIME, ou null s'il n'y en a pas.</summary>
    public (string Path, string ContentType)? Current()
    {
        foreach (var (ext, type) in Types)
            if (File.Exists(Path_(ext))) return (Path_(ext), type);
        return null;
    }

    static readonly (string Ext, string Type)[] Types = [(".png", "image/png"), (".jpg", "image/jpeg"), (".webp", "image/webp")];

    /// <summary>Change à chaque nouvelle image : sert à contourner le cache du navigateur.</summary>
    public long Version => Current() is { } c ? File.GetLastWriteTimeUtc(c.Path).Ticks : 0;

    /// <summary>Enregistre l'image. Renvoie false si ce n'est pas un PNG, JPEG ou WebP.</summary>
    public async Task<bool> SaveAsync(Stream source, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        await source.CopyToAsync(ms, ct);
        var b = ms.ToArray();
        var ext = Detect(b);
        if (ext is null) return false;
        Clear();
        await File.WriteAllBytesAsync(Path_(ext), b, ct);
        return true;
    }

    public void Clear()
    {
        foreach (var (ext, _) in Types)
            if (File.Exists(Path_(ext))) File.Delete(Path_(ext));
    }

    static string? Detect(byte[] b)
    {
        if (b.Length > 12 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return ".webp";
        return null;
    }
}
