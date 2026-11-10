using System.Globalization;
using System.Text.Json;
using Nas.Contracts;

namespace Nas.Agent.Services;

/// <summary>Connexion à un compte OneDrive depuis l'interface (flux « code d'appareil » de Microsoft) : remplace <c>rclone authorize</c>.</summary>
public interface IOneDriveSignIn
{
    /// <summary>L'application Entra ID est configurée (Remote:OneDriveClientId).</summary>
    bool Configured { get; }
    Task<DeviceCodeStart> StartAsync(CancellationToken ct);
    Task<DeviceCodePoll> PollAsync(string deviceCode, CancellationToken ct);
}

/// <summary>
/// Utilise l'application Entra ID de l'installation (client public, sans secret) : autorisations déléguées Files.ReadWrite.All et offline_access.
/// Le résultat est le jeton au format de rclone, qui le renouvellera ensuite avec ce même identifiant d'application.
/// </summary>
public sealed class MicrosoftOneDriveSignIn(IConfiguration cfg, HttpMessageHandler? handler = null) : IOneDriveSignIn
{
    readonly HttpClient _http = new(handler ?? new HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(20) };

    string ClientId => cfg["Remote:OneDriveClientId"] ?? "";
    string Authority => $"{(cfg["Remote:OneDriveAuthority"] ?? "https://login.microsoftonline.com").TrimEnd('/')}/{cfg["Remote:OneDriveTenant"] ?? "common"}/oauth2/v2.0";

    public bool Configured => Guid.TryParse(ClientId, out _);

    static OperationException Unreachable() => new(Msg.F("Microsoft cannot be reached from the NAS."));

    async Task<(bool Ok, JsonElement Body)> PostAsync(string endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var r = await _http.PostAsync($"{Authority}/{endpoint}", new FormUrlEncodedContent(form), ct);
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            return (r.IsSuccessStatusCode, doc.RootElement.Clone());
        }
        catch (HttpRequestException) { throw Unreachable(); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw Unreachable(); }
        catch (JsonException) { throw Unreachable(); }
    }

    static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int Int(JsonElement e, string name, int fallback) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;

    public async Task<DeviceCodeStart> StartAsync(CancellationToken ct)
    {
        if (!Configured) throw new OperationException(Msg.F("The OneDrive sign-in is not configured on this NAS."));
        var (ok, body) = await PostAsync("devicecode", new() { ["client_id"] = ClientId, ["scope"] = "Files.ReadWrite.All offline_access" }, ct);
        var device = Str(body, "device_code");
        var user = Str(body, "user_code");
        var uri = Str(body, "verification_uri");
        if (!ok || device is null || user is null || uri is null)
            throw new OperationException(Msg.F("Microsoft refused the sign-in request: {0}", Str(body, "error_description")?.Split('\n')[0] ?? Str(body, "error") ?? "?"));
        return new DeviceCodeStart(device, user, uri, Int(body, "expires_in", 900), Math.Clamp(Int(body, "interval", 5), 1, 60));
    }

    public async Task<DeviceCodePoll> PollAsync(string deviceCode, CancellationToken ct)
    {
        if (!Configured) throw new OperationException(Msg.F("The OneDrive sign-in is not configured on this NAS."));
        var (ok, body) = await PostAsync("token", new()
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code", ["client_id"] = ClientId, ["device_code"] = deviceCode,
        }, ct);
        if (ok)
        {
            var access = Str(body, "access_token");
            var refresh = Str(body, "refresh_token");
            if (access is null || refresh is null) return new("error", null, Msg.F("Microsoft did not return a complete token. Check that offline_access is granted to the application."));
            var expiry = DateTime.UtcNow.AddSeconds(Int(body, "expires_in", 3600)).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
            var token = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["access_token"] = access, ["token_type"] = "Bearer", ["refresh_token"] = refresh, ["expiry"] = expiry,
            });
            return new("done", token);
        }
        return Str(body, "error") switch
        {
            "authorization_pending" or "slow_down" => new("pending"),
            "authorization_declined" => new("error", null, Msg.F("The sign-in was declined.")),
            "expired_token" or "bad_verification_code" => new("error", null, Msg.F("The code has expired. Start again.")),
            var e => new("error", null, Msg.F("Microsoft refused the sign-in: {0}", Str(body, "error_description")?.Split('\n')[0] ?? e ?? "?")),
        };
    }
}

/// <summary>Mode développement (URSANAS_FAKE=1) : le code est « validé » à la troisième interrogation, sans réseau.</summary>
public sealed class FakeOneDriveSignIn : IOneDriveSignIn
{
    int _polls;
    public bool Configured => true;
    public Task<DeviceCodeStart> StartAsync(CancellationToken ct)
    {
        _polls = 0;
        return Task.FromResult(new DeviceCodeStart("fake-device", "ABCD1234", "https://microsoft.com/devicelogin", 900, 2));
    }

    public Task<DeviceCodePoll> PollAsync(string deviceCode, CancellationToken ct) =>
        Task.FromResult(++_polls < 3 ? new DeviceCodePoll("pending")
            : new DeviceCodePoll("done", "{\"access_token\":\"fake\",\"token_type\":\"Bearer\",\"refresh_token\":\"fake\",\"expiry\":\"2099-01-01T00:00:00Z\"}"));
}
