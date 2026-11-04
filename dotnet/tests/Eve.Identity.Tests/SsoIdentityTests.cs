using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eve.Identity.Tests;

public class SsoIdentityTests
{
    private const string Issuer = "https://login.eveonline.com/";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public async Task A_signed_token_with_the_required_issuer_audiences_and_subject_is_accepted()
    {
        using var signingKey = RSA.Create(2048);
        var verifier = Verifier(signingKey);
        var token = Token(signingKey, Issuer, ["eve.rs-test-client", "EVE Online"], Now.AddMinutes(5), "CHARACTER:EVE:12345");

        var identity = await verifier.VerifyAsync(token, CancellationToken.None);

        Assert.Equal(new VerifiedSsoIdentity(Issuer, "CHARACTER:EVE:12345"), identity);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("subject")]
    public async Task Invalid_token_claims_and_signatures_are_refused(string failure)
    {
        using var trustedKey = RSA.Create(2048);
        using var signer = failure == "signature" ? RSA.Create(2048) : trustedKey;
        var issuer = failure == "issuer" ? "https://attacker.invalid/" : Issuer;
        var audiences = failure == "audience" ? new[] { "other-client", "EVE Online" } : new[] { "eve.rs-test-client", "EVE Online" };
        var expires = failure == "expired" ? Now.AddSeconds(-1) : Now.AddMinutes(5);
        var subject = failure == "subject" ? "USER:EVE:12345" : "CHARACTER:EVE:12345";
        var token = Token(signer, issuer, audiences, expires, subject);

        var identity = await Verifier(trustedKey).VerifyAsync(token, CancellationToken.None);

        Assert.Null(identity);
    }

    [Fact]
    public async Task Explicit_identity_links_resolve_repeatably_and_never_cross_accounts()
    {
        var directory = new InMemoryAccountDirectory(new AccountOptions { AutoCreate = true, NewAccountRole = 7 });
        var first = Assert.IsType<LoggedIn>(await directory.LoginAsync(Attempt("first"), CancellationToken.None));
        var second = Assert.IsType<LoggedIn>(await directory.LoginAsync(Attempt("second"), CancellationToken.None));
        var firstIdentity = new VerifiedSsoIdentity(Issuer, "CHARACTER:EVE:10001");
        var secondIdentity = new VerifiedSsoIdentity(Issuer, "CHARACTER:EVE:10002");

        Assert.True(await directory.LinkSsoIdentityAsync(first.AccountId, firstIdentity, CancellationToken.None));
        Assert.True(await directory.LinkSsoIdentityAsync(first.AccountId, firstIdentity, CancellationToken.None));
        Assert.False(await directory.LinkSsoIdentityAsync(second.AccountId, firstIdentity, CancellationToken.None));
        Assert.True(await directory.LinkSsoIdentityAsync(second.AccountId, secondIdentity, CancellationToken.None));

        var restartedView = directory.WithOptions(new AccountOptions { CountryCode = "JP" });
        var firstLogin = Assert.IsType<LoggedIn>(await restartedView.LoginSsoAsync(firstIdentity, CancellationToken.None));
        var repeatedFirstLogin = Assert.IsType<LoggedIn>(await restartedView.LoginSsoAsync(firstIdentity, CancellationToken.None));
        var secondLogin = Assert.IsType<LoggedIn>(await restartedView.LoginSsoAsync(secondIdentity, CancellationToken.None));
        Assert.Equal(first.AccountId, firstLogin.AccountId);
        Assert.Equal(firstLogin, repeatedFirstLogin);
        Assert.Equal(second.AccountId, secondLogin.AccountId);
        Assert.NotEqual(firstLogin.AccountId, secondLogin.AccountId);
        Assert.Equal("JP", firstLogin.CountryCode);
        Assert.IsType<LoginRefused>(await restartedView.LoginSsoAsync(
            new VerifiedSsoIdentity(Issuer, "CHARACTER:EVE:10003"), CancellationToken.None));
    }

    private static LoginAttempt Attempt(string name) => new(name, new byte[] { 1, 2, 3 }, "EN", "127.0.0.1");

    private static SsoTokenVerifier Verifier(RSA key)
    {
        var parameters = key.ExportParameters(false);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kid = "test-key",
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    n = Base64Url(parameters.Modulus!),
                    e = Base64Url(parameters.Exponent!),
                },
            },
        });
        var metadata = JsonSerializer.Serialize(new { issuer = Issuer, jwks_uri = "https://issuer.test/jwks" });
        var http = new HttpClient(new DiscoveryHandler(metadata, jwks));
        return new SsoTokenVerifier(
            new SsoTokenVerifierOptions(new Uri("https://issuer.test/discovery"), Issuer,
                ["EVE Online", "eve.rs-test-client"]),
            http,
            new FixedTimeProvider(Now));
    }

    private static string Token(RSA key, string issuer, string[] audiences, DateTimeOffset expires, string subject)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", kid = "test-key", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = issuer,
            aud = audiences,
            exp = expires.ToUnixTimeSeconds(),
            sub = subject,
        }));
        var input = header + "." + payload;
        var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return input + "." + Base64Url(signature);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class DiscoveryHandler(string metadata, string jwks) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsolutePath == "/jwks" ? jwks : metadata;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
