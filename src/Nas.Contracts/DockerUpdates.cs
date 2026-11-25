namespace Nas.Contracts;

/// <summary>
/// UpToDate : même empreinte que le registre. UpdateAvailable : le registre a une autre version de la même étiquette. Recreate : la nouvelle
/// image est sur le NAS mais le conteneur utilise encore l'ancienne. Pinned : image fixée par « @sha256 ». Unverifiable : voir la raison.
/// </summary>
public enum ImageUpdateState { UpToDate, UpdateAvailable, Recreate, Pinned, Unverifiable }

/// <summary><c>ImageId</c> : l'image que le conteneur utilise (celle qu'une mise à jour remplace).</summary>
public sealed record ContainerImageUpdate(string ContainerId, string ContainerName, string Image, string? Project, ImageUpdateState State, string? Reason = null,
    string? ImageId = null);

/// <summary>Les images remplacées par une mise à jour, à supprimer si plus rien ne s'en sert.</summary>
public sealed record ReplacedImagesRequest(IReadOnlyList<string> Ids);
public sealed record RemovedImagesResult(int Removed);

/// <summary><c>CheckedAtUtc</c> : dernière interrogation des registres, null si aucune. Un conteneur jamais vérifié n'est pas listé.</summary>
public sealed record ImageUpdateReport(DateTime? CheckedAtUtc, IReadOnlyList<ContainerImageUpdate> Containers);

/// <summary><c>Updated</c> : false si le conteneur utilisait déjà la dernière image.</summary>
public sealed record ContainerUpdateResult(bool Updated);
