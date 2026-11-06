namespace Nas.Contracts;

/// <summary>
/// Un travail de sauvegarde : un partage (ou des dossiers de ce partage) copié vers un dossier d'un disque externe.
/// La source est toujours un partage : l'agent en retrouve le dossier, aucun chemin source ne vient de l'extérieur.
/// </summary>
public sealed record BackupJobDef(
    int Id,
    string Name,
    string Share,
    /// <summary>Jeu de données ZFS du partage (nécessaire pour la copie depuis un snapshot), sinon null.</summary>
    string? Dataset,
    /// <summary>Dossiers du partage à copier, relatifs à sa racine ; vide = tout le partage.</summary>
    IReadOnlyList<string> Includes,
    /// <summary>Point de montage du disque externe.</summary>
    string TargetDir,
    /// <summary>Identifiant du marqueur du disque (null tant que le disque n'est pas initialisé).</summary>
    string? TargetId,
    /// <summary>Dossier du disque qui reçoit la copie.</summary>
    string DestFolder,
    bool MirrorDeletes = true,
    int RetentionDays = 30,
    bool Verify = false,
    bool WindowsDisk = true,
    IReadOnlyList<string>? Excludes = null,
    /// <summary>Copier depuis un snapshot ZFS créé pour l'occasion (partages sur un jeu de données uniquement).</summary>
    bool Snapshot = false);

public enum BackupDiskState { Missing, NotInitialised, Connected, OtherDisk }

/// <summary>État du disque d'un travail : absent, pas initialisé, présent, ou un autre disque.</summary>
public sealed record BackupDiskStatus(BackupDiskState State, long FreeBytes, long TotalBytes);

/// <summary>Un disque monté que l'on peut choisir comme destination (point de montage sous /mnt, /media…).</summary>
public sealed record BackupTarget(string Dir, string? Device, string? FileSystem, long TotalBytes, long FreeBytes, string? MarkerId);

public sealed record InitDiskRequest(string TargetDir);

public sealed record InitDiskResult(string TargetId);

/// <summary>Résultat final d'une copie.</summary>
public sealed record BackupResult(string Status, string? Message, IReadOnlyList<string> ErrorSamples, double ElapsedSeconds);

/// <summary>Une copie en attente, en cours ou terminée (conservée jusqu'à ce que la web l'acquitte).</summary>
public sealed record BackupRunStatus(
    int JobId,
    bool Queued,
    string Phase,
    string? CurrentFile,
    long CurrentFileSize,
    long FilesSeen,
    long BytesSeen,
    long Copied,
    long BytesCopied,
    long Unchanged,
    long Deleted,
    long Skipped,
    long Errors,
    DateTime StartedUtc,
    DateTime? RunStartedUtc,
    /// <summary>Renseigné quand la copie est terminée.</summary>
    BackupResult? Result);

public static partial class BackupRules
{
    public const int MaxRetentionDays = 3650;

    /// <summary>Dossiers et fichiers créés par les NAS et les systèmes d'exploitation, que personne ne veut dans une sauvegarde.</summary>
    public static readonly string[] DefaultExcludes =
        ["@eaDir", "#recycle", "@Recycle", ".DS_Store", "Thumbs.db", "lost+found", "$RECYCLE.BIN", "System Volume Information"];

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$")]
    private static partial System.Text.RegularExpressions.Regex DestRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\.ursacopy|lost\+found$)")]
    private static partial System.Text.RegularExpressions.Regex ReservedDestRegex();

    /// <summary>Nom de dossier simple sur le disque : pas de séparateur, pas de « .. », rien qui ressemble aux dossiers internes du moteur.</summary>
    public static string? ValidateDestFolder(string? name)
    {
        if (string.IsNullOrEmpty(name) || !DestRegex().IsMatch(name) || name.EndsWith('.') || name.EndsWith(' ') || ReservedDestRegex().IsMatch(name))
            return Msg.F("Invalid destination folder: 1 to 64 characters (letters, digits, space, . _ -), without a leading dot.");
        return null;
    }

    public static string? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64 || name.Any(char.IsControl)
            ? Msg.F("The job name is required (64 characters at most).") : null;

    /// <summary>Un dossier relatif à la racine du partage : pas de « .. », pas de chemin absolu, pas de caractère de contrôle.</summary>
    public static string? ValidateInclude(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains('\\') || path.Any(char.IsControl)
            || path.Split('/').Any(s => s is "" or "." or ".."))
            return Msg.F("Invalid folder path.");
        return null;
    }

    public static string? Validate(BackupJobDef j)
    {
        if (ValidateName(j.Name) is { } e0) return e0;
        if (Rules.ValidateShareName(j.Share) is { } e1) return e1;
        if (ValidateDestFolder(j.DestFolder) is { } e2) return e2;
        if (j.RetentionDays is < 0 or > MaxRetentionDays) return Msg.F("The retention must be between 0 and {0} days.", MaxRetentionDays);
        foreach (var i in j.Includes) if (ValidateInclude(i) is { } e3) return e3;
        if (j.Excludes is { } ex && (ex.Count > 50 || ex.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 200 || p.Any(char.IsControl))))
            return Msg.F("Invalid exclusion patterns: 50 at most, 200 characters each.");
        if (j.Snapshot && string.IsNullOrEmpty(j.Dataset)) return Msg.F("Copying from a snapshot needs a share on a ZFS dataset.");
        return null;
    }
}
