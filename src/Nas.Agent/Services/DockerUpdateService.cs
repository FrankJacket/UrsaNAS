using System.Text.Json;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>
/// Détection des nouvelles versions d'images. Seules les empreintes lues dans les registres sont gardées (fichier « Docker:UpdatesFile ») ;
/// l'état de chaque conteneur est recalculé à partir de Docker à chaque demande, donc un conteneur mis à jour n'est plus signalé aussitôt.
/// </summary>
public sealed class DockerUpdateService(DockerService docker, IRegistryClient registry, IConfiguration cfg, ILogger<DockerUpdateService>? log = null)
{
    sealed record Cache(DateTime CheckedAtUtc, Dictionary<string, RegistryDigest> Images);

    readonly SemaphoreSlim _checking = new(1, 1);

    string CacheFile => cfg["Docker:UpdatesFile"] ?? "/var/lib/ursanas/docker-updates.json";

    Cache? Load()
    {
        try { return File.Exists(CacheFile) ? JsonSerializer.Deserialize<Cache>(File.ReadAllText(CacheFile)) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log?.LogWarning(e, "The Docker update cache could not be read");
            return null;
        }
    }

    void Save(Cache cache)
    {
        try
        {
            if (Path.GetDirectoryName(CacheFile) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            File.WriteAllText(CacheFile + ".tmp", JsonSerializer.Serialize(cache));
            File.Move(CacheFile + ".tmp", CacheFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new OperationException(Msg.F("The result of the check could not be saved: {0}", e.Message));
        }
    }

    public async Task<ImageUpdateReport> ReportAsync(CancellationToken ct = default)
    {
        var cache = Load();
        if (cache is null) return new(null, []);
        return new(cache.CheckedAtUtc, Evaluate(await docker.ContainerImagesAsync(ct), cache));
    }

    /// <summary>Interroge les registres pour les images des conteneurs, puis renvoie le rapport.</summary>
    public async Task<ImageUpdateReport> CheckAsync(CancellationToken ct = default)
    {
        await _checking.WaitAsync(ct);
        try
        {
            var containers = await docker.ContainerImagesAsync(ct);
            // Rien à demander pour une image épinglée, absente du NAS ou construite sur place (sans empreinte de registre).
            var wanted = containers.Where(c => !c.Image.Contains('@') && c.Local is { RepoDigests.Count: > 0 }).Select(c => c.Image).Distinct().ToList();
            using var slots = new SemaphoreSlim(4);
            var results = await Task.WhenAll(wanted.Select(async image =>
            {
                await slots.WaitAsync(ct);
                try
                {
                    return (Image: image, Digest: ImageReference.Parse(image) is { } reference
                        ? await registry.DigestAsync(reference, ct)
                        : new RegistryDigest(null, Msg.F("This image name cannot be checked.")));
                }
                finally { slots.Release(); }
            }));
            var cache = new Cache(DateTime.UtcNow, results.ToDictionary(r => r.Image, r => r.Digest));
            Save(cache);
            return new(cache.CheckedAtUtc, Evaluate(containers, cache));
        }
        finally { _checking.Release(); }
    }

    static List<ContainerImageUpdate> Evaluate(IReadOnlyList<ContainerImage> containers, Cache cache)
    {
        var list = new List<ContainerImageUpdate>();
        foreach (var c in containers)
        {
            void Add(ImageUpdateState state, string? reason = null) =>
                list.Add(new(c.Id, c.Name, c.Image, c.Project, state, reason, c.ImageId));

            if (c.Image.Contains('@')) { Add(ImageUpdateState.Pinned); continue; }
            if (c.Local is not { } local) { Add(ImageUpdateState.Unverifiable, Msg.F("The image is no longer on the NAS under this name.")); continue; }
            var outdated = local.Id != c.ImageId;
            if (local.RepoDigests.Count == 0)
            {
                if (outdated) Add(ImageUpdateState.Recreate);
                else Add(ImageUpdateState.Unverifiable, Msg.F("The image was built or loaded on this NAS: no registry to compare with."));
                continue;
            }
            var known = cache.Images.GetValueOrDefault(c.Image);
            if (known?.Digest is { } digest && !local.RepoDigests.Any(d => d.EndsWith("@" + digest, StringComparison.Ordinal))) Add(ImageUpdateState.UpdateAvailable);
            else if (outdated) Add(ImageUpdateState.Recreate);
            else if (known?.Digest is not null) Add(ImageUpdateState.UpToDate);
            else if (known?.Error is { } error) Add(ImageUpdateState.Unverifiable, error);
            // sinon : image apparue depuis la dernière vérification, rien à en dire
        }
        return list;
    }
}
