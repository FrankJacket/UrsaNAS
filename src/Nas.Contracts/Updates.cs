using System.Text.RegularExpressions;

namespace Nas.Contracts;

/// <summary>
/// Réglages de la recherche de mises à jour. <c>Channel</c> vaut <c>stable</c> ou <c>prerelease</c> (les préversions comptent aussi).
/// </summary>
public sealed record UpdateSettings(string Channel);

public sealed record SetUpdateSettingsRequest(string Channel);

/// <summary>
/// Résultat de la recherche d'une nouvelle version d'UrsaNAS. <c>Error</c> non nul : la recherche a échoué (les autres champs sont alors à ignorer, sauf <c>Current</c>).
/// <c>Tag</c> : nom exact de la release GitHub (ex. « v1.2.3 ») ; sert à retrouver ses fichiers pour l'installation, jamais affiché.
/// <c>CanInstall</c> is false when UrsaNAS cannot update itself on this processor architecture (only x86-64 packages are published).
/// </summary>
public sealed record AppUpdateInfo(string Current, string? Latest, bool Available, string? Notes, string? Url, bool Prerelease, DateTimeOffset? PublishedAt,
    DateTimeOffset CheckedAt, string? Error, string? Tag = null, bool CanInstall = true);

/// <summary>
/// Avancement de l'installation d'une mise à jour d'UrsaNAS, relu depuis le fichier que le script d'installation met à jour (survit au redémarrage de l'agent).
/// <c>Stage</c> : idle, running, done, failed ou rolled_back.
/// </summary>
public sealed record AppInstallStatus(string Stage, string? Version, string? Message, DateTimeOffset UpdatedAt);

public sealed record InstallPackagesRequest(IReadOnlyList<string> Names);

/// <summary>Avancement de l'installation de paquets en cours : <c>Lines</c> sont les dernières lignes écrites par apt (les plus récentes à la fin).</summary>
public sealed record PackageInstallProgress(bool Running, IReadOnlyList<string> Names, IReadOnlyList<string> Lines, DateTimeOffset? StartedAt);

/// <summary>
/// Un paquet Linux dont une version plus récente est disponible. <c>Tracked</c> : il fait partie de ce qu'UrsaNAS installe ou utilise.
/// <c>ManualOnly</c> : sa mise à jour redémarre un service important (Docker, libvirt, ZFS) ou touche le noyau ; jamais automatique par défaut.
/// </summary>
public sealed record PackageUpdate(string Name, string Installed, string Candidate, bool Tracked, bool ManualOnly);

public sealed record PackageUpdates(IReadOnlyList<PackageUpdate> Items, bool RebootRequired, DateTimeOffset CheckedAt, string? Error);

/// <summary>Réglages et derniers résultats connus (null : jamais vérifié depuis le démarrage de l'agent).</summary>
public sealed record UpdateStatus(UpdateSettings Settings, AppUpdateInfo? App, PackageUpdates? Packages);

/// <summary>
/// Planification de la vérification et de l'installation automatique, vue et modifiée depuis l'interface (l'heure est celle du serveur).
/// <c>CheckFrequency</c> : off, daily ou weekly (alors <c>CheckDay</c> compte). <c>AutoInstallApp</c>/<c>AutoInstallPackages</c> : installer automatiquement
/// ce qu'une vérification a trouvé, chaque semaine à <c>AutoInstallDay</c>/<c>AutoInstallTime</c>. <c>AutoInstallManualPackages</c> : inclure dans
/// l'installation automatique les paquets « Manuel seulement » (jamais par défaut). <c>AutoReboot</c> : redémarrer si une mise à jour l'exige, mais
/// seulement à cette heure planifiée, jamais aussitôt l'installation terminée.
/// </summary>
public sealed record UpdateScheduleSettings(string CheckFrequency, DayOfWeek CheckDay, string CheckTime,
    bool AutoInstallApp, bool AutoInstallPackages, bool AutoInstallManualPackages, bool AutoReboot, DayOfWeek AutoInstallDay, string AutoInstallTime)
{
    public static readonly UpdateScheduleSettings Default = new("off", DayOfWeek.Sunday, "03:00", false, false, false, false, DayOfWeek.Sunday, "04:00");
}

public static partial class Rules
{
    public static readonly IReadOnlyList<string> UpdateChannels = ["stable", "prerelease"];
    public static readonly IReadOnlyList<string> UpdateCheckFrequencies = ["off", "daily", "weekly"];

    /// <summary>Heure « HH:mm » (ou « H:mm »), comme saisie dans un champ de texte.</summary>
    public static bool IsTimeOfDay(string? t) =>
        t is not null && TimeSpan.TryParse(t, System.Globalization.CultureInfo.InvariantCulture, out var ts) && ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1);

    /// <summary>Nom de paquet Debian/Ubuntu valide (lettres minuscules, chiffres, « + », « . », « - »).</summary>
    public static bool IsPackageName(string? n) => n is { Length: >= 1 and <= 128 } && PackageNameRegex().IsMatch(n);

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9+.\-]*\z")]
    private static partial Regex PackageNameRegex();
}
