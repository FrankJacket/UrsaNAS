using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Nas.Agent.Services;
using Xunit;

namespace Nas.Agent.Tests;

public class DropboxSignInTests
{
    sealed class Handler(HttpStatusCode code, string json) : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    static IConfiguration Cfg(string? key = "dbxkey1234567") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Remote:DropboxAppKey"] = key }).Build();

    [Fact]
    public void L_adresse_d_autorisation_utilise_pkce_et_demande_un_jeton_de_renouvellement()
    {
        var s = new DropboxAppSignIn(Cfg());
        var start = s.Start();

        var uri = new Uri(start.AuthUrl);
        Assert.Equal("www.dropbox.com", uri.Host);
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("dbxkey1234567", q["client_id"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("offline", q["token_access_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Null(q["redirect_uri"]); // sans redirection, Dropbox affiche le code
        Assert.InRange(start.State.Length, 43, 128);
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(start.State))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, q["code_challenge"]);
        Assert.NotEqual(start.State, s.Start().State); // un secret différent à chaque connexion
    }

    [Fact]
    public async Task Le_code_est_echange_contre_un_jeton_au_format_de_rclone()
    {
        var h = new Handler(HttpStatusCode.OK, "{\"access_token\":\"AT\",\"refresh_token\":\"RT\",\"expires_in\":14400,\"token_type\":\"bearer\"}");
        var s = new DropboxAppSignIn(Cfg(), h);
        var start = s.Start();

        var token = await s.CompleteAsync(" CODE123 ", start.State, default);

        Assert.Contains("grant_type=authorization_code", h.Body);
        Assert.Contains("code=CODE123", h.Body);
        Assert.Contains("code_verifier=" + start.State, h.Body);
        Assert.DoesNotContain("client_secret", h.Body);
        Assert.True(Nas.Contracts.Rules.IsRcloneToken(token, out _));
        using var doc = JsonDocument.Parse(token);
        Assert.Equal("RT", doc.RootElement.GetProperty("refresh_token").GetString());
    }

    [Fact]
    public async Task Un_code_refuse_ou_mal_forme_donne_une_erreur_lisible()
    {
        var s = new DropboxAppSignIn(Cfg(), new Handler(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"code expired\"}"));
        var state = s.Start().State;
        var e = await Assert.ThrowsAsync<OperationException>(() => s.CompleteAsync("CODE", state, default));
        Assert.Contains("code expired", e.Message);
        await Assert.ThrowsAsync<OperationException>(() => s.CompleteAsync("a b", state, default)); // espace dans le code
        await Assert.ThrowsAsync<OperationException>(() => s.CompleteAsync("", state, default));
    }

    [Fact]
    public void Sans_cle_configuree_rien_n_est_propose()
    {
        var s = new DropboxAppSignIn(Cfg(""));
        Assert.False(s.Configured);
        Assert.Throws<OperationException>(() => s.Start());
    }
}
