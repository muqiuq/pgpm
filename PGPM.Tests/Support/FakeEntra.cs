using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using PGPM.Auth.Microsoft;

namespace PGPM.Tests.Support;

/// <summary>
/// Stands in for Entra ID in the Microsoft sign-in (GGITSV's FakeEntra, docs/03 §5.6): a fixed OpenID configuration with
/// its own signing key, and a token endpoint on the handler's backchannel that returns an ID token with the claims a test
/// chooses; Graph's <c>/me</c> answers as the test says. Nothing leaves the test process; the real handshake against the
/// dev tenant is tested by hand (docs/05 P1).
/// </summary>
public sealed class FakeEntra : IDisposable
{
    public const string TenantId = "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d";
    public const string ClientId = "4c1f0d2e-3b5a-4e6f-8a7b-9c0d1e2f3a4b";

    private readonly RSA rsa = RSA.Create(2048);
    private readonly RsaSecurityKey key;

    public FakeEntra()
    {
        key = new RsaSecurityKey(rsa) { KeyId = "fake-entra-key" };
        Configuration = new OpenIdConnectConfiguration
        {
            Issuer = Issuer,
            AuthorizationEndpoint = $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/authorize",
            TokenEndpoint = $"https://login.microsoftonline.com/{TenantId}/oauth2/v2.0/token",
        };
        Configuration.SigningKeys.Add(key);
    }

    public static string Issuer => $"https://login.microsoftonline.com/{TenantId}/v2.0";

    public static Guid Tenant => Guid.Parse(TenantId);

    public OpenIdConnectConfiguration Configuration { get; }

    /// <summary>The claims of the next ID token the token endpoint issues (besides the protocol's own).</summary>
    public Dictionary<string, object> NextClaims { get; } = [];

    /// <summary>The nonce the next ID token must carry: the one of the authorization request.</summary>
    public string? Nonce { get; set; }

    /// <summary>The token requests received, for checks such as PKCE.</summary>
    public List<string> TokenRequests { get; } = [];

    /// <summary>What Graph's <c>GET /me</c> answers: this JSON with 200, or, while <see cref="GraphStatus"/> is not 200, that status.</summary>
    public string GraphMe { get; set; } = """{"userPrincipalName":"j.miller@example.com","mail":"j.miller@example.com"}""";

    public HttpStatusCode GraphStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>The Graph requests received, with the token they carried.</summary>
    public List<string> GraphRequests { get; } = [];

    /// <summary>The settings of an app with Microsoft SSO on, against this fake.</summary>
    public Dictionary<string, string?> Settings(IReadOnlyDictionary<string, string?>? more = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Entra:Enabled"] = "true",
            ["Entra:TenantId"] = TenantId,
            ["Entra:ClientId"] = ClientId,
            ["Entra:ClientSecret"] = "fake-client-secret",
        };
        foreach (var (k, v) in more ?? new Dictionary<string, string?>())
        {
            settings[k] = v;
        }
        return settings;
    }

    /// <summary>The services that point the OpenID Connect handler and the Graph client at this fake.</summary>
    public void Services(IServiceCollection services)
    {
        services.PostConfigure<OpenIdConnectOptions>(MicrosoftSignIn.Scheme, oidc =>
        {
            oidc.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(Configuration);
            oidc.Backchannel = new HttpClient(new TokenEndpoint(this));
        });
        services.AddHttpClient(MicrosoftDirectoryReader.ClientName).ConfigurePrimaryHttpMessageHandler(() => new GraphEndpoint(this));
    }

    public TestApp App(IReadOnlyDictionary<string, string?>? more = null) => new(Settings(more), Services);

    public void Dispose() => rsa.Dispose();

    private string IssueIdToken(string accessToken)
    {
        var claims = new Dictionary<string, object>(NextClaims)
        {
            ["sub"] = "fake-subject",
            ["nonce"] = Nonce ?? throw new InvalidOperationException("The test did not take the nonce from the authorization request."),
            ["at_hash"] = AtHash(accessToken),
        };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }

    // OpenID Connect Core §3.1.3.6: the left half of the SHA-256 of the access token, base64url.
    private static string AtHash(string accessToken)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
        return Base64UrlEncoder.Encode(hash[..(hash.Length / 2)]);
    }

    private sealed class GraphEndpoint(FakeEntra entra) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entra.GraphRequests.Add($"{request.RequestUri!.PathAndQuery} {request.Headers.Authorization}");
            return Task.FromResult(entra.GraphStatus == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(entra.GraphMe, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(entra.GraphStatus));
        }
    }

    private sealed class TokenEndpoint(FakeEntra entra) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entra.TokenRequests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            const string AccessToken = "fake-access-token";
            var body = $$"""{"token_type":"Bearer","access_token":"{{AccessToken}}","expires_in":3600,"id_token":"{{entra.IssueIdToken(AccessToken)}}"}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
