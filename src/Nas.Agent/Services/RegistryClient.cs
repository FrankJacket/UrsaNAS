using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>Une référence d'image décomposée comme Docker le fait : « nginx » = registry-1.docker.io, library/nginx, latest.</summary>
public sealed record ImageReference(string Registry, string Repository, string Tag)
{
    public static ImageReference? Parse(string image)
    {
        if (Rules.ValidateImageRef(image) is not null || image.Contains('@')) return null;
        var registry = "docker.io";
        var path = image;
        var slash = image.IndexOf('/');
        if (slash > 0 && image[..slash] is var first && (first.Contains('.') || first.Contains(':') || first == "localhost"))
            (registry, path) = (first, image[(slash + 1)..]);
        var tag = "latest";
        var colon = path.LastIndexOf(':');
        if (colon > path.LastIndexOf('/')) (path, tag) = (path[..colon], path[(colon + 1)..]);
        if (path.Length == 0 || tag.Length == 0 || path.Contains(':') || path.Split('/').Any(p => p is "" or "." or "..")) return null;
        if (registry is "docker.io" or "index.docker.io")
        {
            registry = "registry-1.docker.io";
            if (!path.Contains('/')) path = "library/" + path;
        }
        return new(registry, path, tag);
    }
}

/// <summary>L'empreinte publiée par le registre, ou la raison (message à traduire) pour laquelle elle n'a pas pu être lue.</summary>
public sealed record RegistryDigest(string? Digest, string? Error);

public interface IRegistryClient
{
    Task<RegistryDigest> DigestAsync(ImageReference image, CancellationToken ct = default);
}

/// <summary>
/// Lit l'empreinte d'une étiquette par une requête HEAD sur le manifeste (API des registres OCI) : rien n'est téléchargé. Accès anonyme
/// seulement (les identifiants de « docker login » ne sont pas lus), en HTTPS seulement.
/// </summary>
public sealed partial class RegistryClient(HttpMessageHandler? handler = null) : IRegistryClient
{
    static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json", "application/vnd.docker.distribution.manifest.v2+json",
    ];

    readonly HttpClient _http = new(handler ?? new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(15) };

    [GeneratedRegex("(\\w+)=\"([^\"]*)\"")]
    private static partial Regex ChallengeRegex();

    public async Task<RegistryDigest> DigestAsync(ImageReference image, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://{image.Registry}/v2/{image.Repository}/manifests/{Uri.EscapeDataString(image.Tag)}";
            var response = await HeadAsync(url, null, ct);
            try
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && await TokenAsync(response, ct) is { } token)
                {
                    response.Dispose();
                    response = await HeadAsync(url, token, ct);
                }
                if (response.StatusCode == HttpStatusCode.TooManyRequests) return new(null, Msg.F("The registry refuses more requests for now. Try again later."));
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                    return new(null, Msg.F("The registry does not give access to this image: it is private, local or no longer published."));
                if (!response.IsSuccessStatusCode) return new(null, Msg.F("The registry answered {0}.", (int)response.StatusCode));
                return response.Headers.TryGetValues("Docker-Content-Digest", out var values) && values.FirstOrDefault() is { Length: > 0 } digest
                    ? new(digest, null)
                    : new(null, Msg.F("The registry did not give the fingerprint of the image."));
            }
            finally { response.Dispose(); }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or UriFormatException || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new(null, Msg.F("The registry could not be reached."));
        }
    }

    async Task<HttpResponseMessage> HeadAsync(string url, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        foreach (var type in ManifestTypes) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(type));
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _http.SendAsync(request, ct);
    }

    // « WWW-Authenticate: Bearer realm="https://…",service="…",scope="…" » : le jeton anonyme se demande à « realm ».
    async Task<string?> TokenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var challenge = response.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))?.Parameter;
        if (challenge is null) return null;
        var values = ChallengeRegex().Matches(challenge).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("realm", out var realm) || !realm.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;
        var query = string.Join("&", new[] { "service", "scope" }.Where(values.ContainsKey).Select(k => $"{k}={Uri.EscapeDataString(values[k])}"));
        using var r = await _http.GetAsync(query.Length == 0 ? realm : $"{realm}{(realm.Contains('?') ? '&' : '?')}{query}", ct);
        if (!r.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "token", "access_token" })
            if (doc.RootElement.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String) return t.GetString();
        return null;
    }
}

/// <summary>Mode simulé : seule « nginx:latest » a une nouvelle version.</summary>
public sealed class FakeRegistryClient : IRegistryClient
{
    public const string SameDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    public const string NewDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    public Task<RegistryDigest> DigestAsync(ImageReference image, CancellationToken ct = default) =>
        Task.FromResult(new RegistryDigest(image is { Repository: "library/nginx", Tag: "latest" } ? NewDigest : SameDigest, null));
}
