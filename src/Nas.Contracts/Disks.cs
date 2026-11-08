using System.Text.RegularExpressions;

namespace Nas.Contracts;

/// <summary>Efface un disque libre et y crée une partition GPT unique, formatée.</summary>
public sealed record PrepareDiskRequest(string Disk, string Fs, string? Label);
public sealed record WipeDiskRequest(string Disk);
/// <summary>Crée une partition dans l'espace non alloué d'un disque (sans rien effacer). <c>SizeMb</c> null : tout l'espace restant.</summary>
public sealed record CreatePartitionRequest(string Disk, long? SizeMb, string Fs, string? Label);
public sealed record FormatPartitionRequest(string Partition, string Fs, string? Label);
/// <summary><c>Name</c> : dossier de montage sous /mnt. <c>Persistent</c> : remonté à chaque démarrage (/etc/fstab).</summary>
public sealed record MountPartitionRequest(string Partition, string Name, bool Persistent);
public sealed record UnmountRequest(string MountPoint);
/// <summary>Systèmes de fichiers que le NAS sait créer (selon les programmes installés).</summary>
public sealed record FsOptions(IReadOnlyList<string> Available);

public static partial class Rules
{
    public static readonly IReadOnlyList<string> FileSystems = ["ext4", "btrfs", "zfs", "exfat"];

    [GeneratedRegex("^/dev/(sd[a-z]{1,2}|vd[a-z]{1,2}|xvd[a-z]{1,2}|nvme[0-9]{1,2}n[0-9]{1,2})$")]
    private static partial Regex DiskPathRegex();

    [GeneratedRegex("^/dev/(sd[a-z]{1,2}[0-9]{1,3}|vd[a-z]{1,2}[0-9]{1,3}|xvd[a-z]{1,2}[0-9]{1,3}|nvme[0-9]{1,2}n[0-9]{1,2}p[0-9]{1,3})$")]
    private static partial Regex PartitionPathRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,11}$")]
    private static partial Regex FsLabelRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,31}$")]
    private static partial Regex MountNameRegex();

    public static bool IsDiskPath(string? p) => p is not null && DiskPathRegex().IsMatch(p);
    public static bool IsPartitionPath(string? p) => p is not null && PartitionPathRegex().IsMatch(p);

    /// <summary>Système de fichiers et étiquette d'un formatage. Pour ZFS, l'étiquette est le nom (obligatoire) du pool créé sur la partition.</summary>
    public static string? ValidateFormat(string? fs, string? label) =>
        ValidateFs(fs) ?? (fs == "zfs" ? ValidatePoolName(label) : ValidateFsLabel(label));

    public static string? ValidateFs(string? fs) => FileSystems.Contains(fs ?? "") ? null : Msg.F("Unknown file system.");

    public static string? ValidateFsLabel(string? label) =>
        string.IsNullOrEmpty(label) || FsLabelRegex().IsMatch(label) ? null : Msg.F("Invalid label: 1 to 11 characters (letters, digits, _ and -).");

    public static string? ValidateMountName(string? name) =>
        string.IsNullOrEmpty(name) || !MountNameRegex().IsMatch(name)
            ? Msg.F("Invalid mount folder name: 1 to 32 characters (letters, digits, _ . and -), starting with a letter or a digit.")
            : null;
}
