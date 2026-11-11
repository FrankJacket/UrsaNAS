using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>Connexion à un compte Dropbox depuis l'interface (OAuth 2 avec PKCE et code à coller) : remplace <c>rclone authorize</c>.</summary>
public interface IDropboxSignIn
{
    /// <summary>La clé de l'application Dropbox est configurée (Remote:DropboxAppKey).</summary>
    bool Configured { get; }
    OAuthStart Start();
    Task<string> CompleteAsync(string code, string state, CancellationToken ct);
}

/// <summary>
/// Utilise l'application Dropbox de l'installation (client public, sans secret). L'utilisateur autorise sur dropbox.com, qui affiche un code à coller ;
/// l'agent l'échange contre un jeton de renouvellement, au format de rclone.
/// </summary>
public sealed class DropboxAppSignIn(IConfiguration cfg, HttpMessageHandler? handler = null) : IDropboxSignIn
{
    readonly HttpClient _http = new(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(20) };

    string Key => cfg["Remote:DropboxAppKey"] ?? "";
    string TokenUrl => cfg["Remote:DropboxTokenUrl"] ?? "https://api.dropboxapi.com/oauth2/token";

    public bool Configured => Rules.IsAppId(Key);

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public OAuthStart Start()
    {
        if (!Configured) throw new OperationException(Msg.F("The Dropbox sign-in is not configured on this NAS."));
        var verifier = B64Url(RandomNumberGenerator.GetBytes(48)); // 64 caractères : entre 43 et 128, comme l'exige PKCE
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var url = "https://www.dropbox.com/oauth2/authorize?response_type=code&token_access_type=offline&code_challenge_method=S256"
            + $"&client_id={Uri.EscapeDataString(Key)}&code_challenge={challenge}";
        return new OAuthStart(url, verifier);
    }

    public async Task<string> CompleteAsync(string code, string state, CancellationToken ct)
    {
        if (!Configured) throw new OperationException(Msg.F("The Dropbox sign-in is not configured on this NAS."));
        code = code.Trim();
        if (code.Length is 0 or > 512 || code.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || state.Length is < 43 or > 128)
            throw new OperationException(Msg.F("Invalid Dropbox code: paste the code shown by Dropbox."));
        try
        {
            using var r = await _http.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = state, ["client_id"] = Key,
            }), ct);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            var body = doc.RootElement;
            string? Str(string n) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var access = Str("access_token");
            var refresh = Str("refresh_token");
            if (!r.IsSuccessStatusCode || access is null || refresh is null)
                throw new OperationException(Msg.F("Dropbox refused the code: {0}", Str("error_description") ?? Str("error") ?? "?"));
            var seconds = body.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 14400;
            var expiry = DateTime.UtcNow.AddSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
            return JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["access_token"] = access, ["token_type"] = "bearer", ["refresh_token"] = refresh, ["expiry"] = expiry,
            });
        }
        catch (HttpRequestException) { throw new OperationException(Msg.F("Dropbox cannot be reached from the NAS.")); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new OperationException(Msg.F("Dropbox cannot be reached from the NAS.")); }
        catch (JsonException) { throw new OperationException(Msg.F("Dropbox cannot be reached from the NAS.")); }
    }
}

/// <summary>Mode développement (URSANAS_FAKE=1) : aucun appel réseau.</summary>
public sealed class FakeDropboxSignIn : IDropboxSignIn
{
    public bool Configured => true;
    public OAuthStart Start() => new("https://www.dropbox.com/oauth2/authorize?fake=1", new string('a', 64));
    public Task<string> CompleteAsync(string code, string state, CancellationToken ct) =>
        Task.FromResult("{\"access_token\":\"fake\",\"token_type\":\"bearer\",\"refresh_token\":\"fake\",\"expiry\":\"2099-01-01T00:00:00Z\"}");
}
