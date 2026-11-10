using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Nas.Agent.Services;
using Xunit;

namespace Nas.Agent.Tests;

public class OneDriveSignInTests
{
    sealed class Handler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.RequestUri!.ToString(), body));
            var (code, json) = answer(request, body);
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    static IConfiguration Cfg(string? id = "11111111-2222-3333-4444-555555555555") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Remote:OneDriveClientId"] = id }).Build();

    [Fact]
    public async Task Demande_un_code_d_appareil_avec_les_bonnes_autorisations()
    {
        var h = new Handler((_, _) => (HttpStatusCode.OK,
            "{\"device_code\":\"DC\",\"user_code\":\"ABC123\",\"verification_uri\":\"https://microsoft.com/devicelogin\",\"expires_in\":900,\"interval\":5}"));
        var s = new MicrosoftOneDriveSignIn(Cfg(), h);

        var start = await s.StartAsync(default);

        Assert.Equal(("DC", "ABC123", "https://microsoft.com/devicelogin", 900, 5), (start.DeviceCode, start.UserCode, start.VerificationUri, start.ExpiresIn, start.Interval));
        var call = Assert.Single(h.Calls);
        Assert.Equal("https://login.microsoftonline.com/common/oauth2/v2.0/devicecode", call.Url);
        Assert.Contains("client_id=11111111-2222-3333-4444-555555555555", call.Body);
        Assert.Contains("Files.ReadWrite.All", call.Body);
        Assert.Contains("offline_access", call.Body);
    }

    [Fact]
    public async Task Sans_application_configuree_rien_n_est_propose()
    {
        var s = new MicrosoftOneDriveSignIn(Cfg(""), new Handler((_, _) => (HttpStatusCode.OK, "{}")));
        Assert.False(s.Configured);
        await Assert.ThrowsAsync<OperationException>(() => s.StartAsync(default));
    }

    [Theory]
    [InlineData("authorization_pending", "pending")]
    [InlineData("slow_down", "pending")]
    [InlineData("authorization_declined", "error")]
    [InlineData("expired_token", "error")]
    public async Task Les_reponses_d_attente_et_d_echec_sont_distinguees(string error, string status)
    {
        var s = new MicrosoftOneDriveSignIn(Cfg(), new Handler((_, _) => (HttpStatusCode.BadRequest, $"{{\"error\":\"{error}\"}}")));
        Assert.Equal(status, (await s.PollAsync("DC", default)).Status);
    }

    [Fact]
    public async Task Le_jeton_obtenu_prend_le_format_de_rclone()
    {
        var h = new Handler((_, _) => (HttpStatusCode.OK, "{\"access_token\":\"AT\",\"refresh_token\":\"RT\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"));
        var s = new MicrosoftOneDriveSignIn(Cfg(), h);

        var poll = await s.PollAsync("DC", default);

        Assert.Equal("done", poll.Status);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", h.Calls[0].Body);
        Assert.True(Nas.Contracts.Rules.IsRcloneToken(poll.Token, out _));
        using var doc = JsonDocument.Parse(poll.Token!);
        Assert.Equal("RT", doc.RootElement.GetProperty("refresh_token").GetString());
        Assert.Equal("Bearer", doc.RootElement.GetProperty("token_type").GetString());
        Assert.True(DateTime.Parse(doc.RootElement.GetProperty("expiry").GetString()!).ToUniversalTime() > DateTime.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task Un_jeton_sans_renouvellement_est_refuse()
    {
        var s = new MicrosoftOneDriveSignIn(Cfg(), new Handler((_, _) => (HttpStatusCode.OK, "{\"access_token\":\"AT\",\"expires_in\":3600}")));
        Assert.Equal("error", (await s.PollAsync("DC", default)).Status);
    }
}
