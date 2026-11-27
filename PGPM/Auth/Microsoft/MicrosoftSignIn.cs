using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using PGPM.Audit;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Auth.Microsoft;

/// <summary>
/// Microsoft SSO (GGITSV, docs/03 §5): OpenID Connect against Entra ID with the plain ASP.NET Core handler – authorization
/// code with PKCE, a confidential client, single tenant, callback <c>/signin-oidc</c>. Adapted onto PNMTD's cookie session
/// (docs/03 §5.2): the handler never signs in itself; <see cref="MicrosoftSignInService"/> completes the sign-in and this
/// issues the <c>pgpm.auth</c> session with the auth method <c>microsoft</c>, then sends the browser on – to the requested
/// path, or to the sign-in page with the reason it was refused.
/// </summary>
public static partial class MicrosoftSignIn
{
    /// <summary>The authentication scheme; removed at startup while SSO is off.</summary>
    public const string Scheme = "Microsoft";

    /// <summary>Where Microsoft sends the browser back (the redirect URI of the app registration).</summary>
    public const string CallbackPath = "/signin-oidc";

    /// <summary>The query parameter of the sign-in page that carries the reason of a failed Microsoft sign-in.</summary>
    public const string ErrorParameter = "ssoError";

    // The access token of the token response, kept for the rest of the callback request only (Graph's /me).
    private static readonly object AccessTokenKey = new();

    public static IServiceCollection AddMicrosoftSignIn(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EntraOptions>(configuration.GetSection(EntraOptions.Section));
        // Registered always, removed at startup while SSO is off (RemoveDisabledSchemes): /signin-oidc is then not handled.
        services.AddAuthentication().AddOpenIdConnect(Scheme, "Microsoft", _ => { });
        services.AddOptions<OpenIdConnectOptions>(Scheme)
            .Configure<IOptions<EntraOptions>, IConfiguration>((options, entra, config) => Configure(options, entra.Value, PublicUrl(config)));
        services.AddHttpClient(MicrosoftDirectoryReader.ClientName, client =>
        {
            client.BaseAddress = MicrosoftDirectoryReader.GraphAddress;
            client.Timeout = TimeSpan.FromSeconds(10);
        });
        services.AddTransient<MicrosoftDirectoryReader>();
        services.AddScoped<MicrosoftSignInService>();
        return services;
    }

    /// <summary>Without SSO there is no Microsoft scheme at all: nothing reaches Entra.</summary>
    public static void RemoveDisabledSchemes(IServiceProvider services)
    {
        if (!services.GetRequiredService<IOptions<EntraOptions>>().Value.Enabled)
        {
            services.GetRequiredService<IAuthenticationSchemeProvider>().RemoveScheme(Scheme);
        }
    }

    public static Uri? PublicUrl(IConfiguration configuration) =>
        Uri.TryCreate(configuration["PublicUrl"], UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? url
            : null;

    /// <summary>The scheme's options from the settings (GGITSV's MicrosoftSignIn.Configure).</summary>
    public static void Configure(OpenIdConnectOptions options, EntraOptions entra, Uri? publicUrl)
    {
        options.Authority = $"https://login.microsoftonline.com/{entra.TenantId}/v2.0";
        options.ClientId = entra.ClientId;
        options.ClientSecret = entra.ClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        // The code comes back in the query of a top-level GET: the handshake's cookies can be SameSite=Lax, and no
        // cross-site POST is needed. PKCE binds the code to this browser.
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.CallbackPath = CallbackPath;
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.Scope.Add(MicrosoftDirectoryReader.Scope);
        // The ID token says who signed in; the access token is used once, for Graph's /me, and then dropped: no tokens are
        // kept, and the claims keep their JWT names.
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = MicrosoftClaims.Name;
        foreach (var cookie in new[] { options.CorrelationCookie, options.NonceCookie })
        {
            cookie.SameSite = SameSiteMode.Lax;
            cookie.SecurePolicy = publicUrl?.Scheme == Uri.UriSchemeHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            cookie.HttpOnly = true;
        }
        options.CorrelationCookie.Name = "pgpm.oidc.correlation.";
        options.NonceCookie.Name = "pgpm.oidc.nonce.";
        options.Events = CreateEvents(publicUrl);
    }

    /// <summary>The page the browser goes to when the Microsoft sign-in is refused: <c>failed</c>, <c>cancelled</c>, <c>deactivated</c>, …</summary>
    public static string ErrorPage(string reason) => $"/sign-in?{ErrorParameter}={reason}";

    private static OpenIdConnectEvents CreateEvents(Uri? publicUrl) => new()
    {
        // Behind the reverse proxy the request's own address may differ from the one registered in Entra.
        OnRedirectToIdentityProvider = context =>
        {
            if (publicUrl is not null)
            {
                context.ProtocolMessage.RedirectUri = new Uri(publicUrl, CallbackPath).ToString();
            }
            return Task.CompletedTask;
        },
        OnTokenResponseReceived = context =>
        {
            if (!string.IsNullOrEmpty(context.TokenEndpointResponse.AccessToken))
            {
                context.HttpContext.Items[AccessTokenKey] = context.TokenEndpointResponse.AccessToken;
            }
            return Task.CompletedTask;
        },
        OnTicketReceived = CompleteAsync,
        OnAccessDenied = async context =>
        {
            context.HandleResponse();
            await AuditFailureAsync(context.HttpContext, "The user cancelled the Microsoft sign-in.");
            context.Response.Redirect(ErrorPage("cancelled"));
        },
        OnRemoteFailure = async context =>
        {
            context.HandleResponse();
            LogRemoteFailure(Logger(context.HttpContext), context.Failure);
            await AuditFailureAsync(context.HttpContext, $"Microsoft reported a failure: {context.Failure?.Message}");
            context.Response.Redirect(ErrorPage("failed"));
        },
    };

    private static async Task CompleteAsync(TicketReceivedContext context)
    {
        context.HandleResponse();
        var http = context.HttpContext;
        var identity = MicrosoftClaims.ToExternalIdentity(context.Principal);
        if (identity is null)
        {
            await AuditFailureAsync(http, "The ID token has no valid object id (oid) or tenant (tid).");
            context.Response.Redirect(ErrorPage("failed"));
            return;
        }
        if (http.Items[AccessTokenKey] is string accessToken)
        {
            var reader = http.RequestServices.GetRequiredService<MicrosoftDirectoryReader>();
            identity = identity with { Directory = await reader.ReadAsync(accessToken, http.RequestAborted) };
        }
        var result = await http.RequestServices.GetRequiredService<MicrosoftSignInService>()
            .CompleteAsync(identity, AuditWho.From(http), http.RequestAborted);
        if (result.User is not { } user)
        {
            context.Response.Redirect(ErrorPage(MicrosoftSignInResult.ErrorCode(result.Failure!.Value)));
            return;
        }
        var now = Clock.UtcNow;
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, SessionClaims.Create(user, now, method: AuthMethod.Microsoft),
            new AuthenticationProperties { IsPersistent = true, IssuedUtc = now });
        context.Response.Redirect(ReturnPaths.Normalize(context.ReturnUri));
    }

    private static async Task AuditFailureAsync(HttpContext http, string detail)
    {
        var db = http.RequestServices.GetRequiredService<PgpmDbContext>();
        AuditLog.Record(db, AuditEvents.SsoFailed, detail, AuditWho.From(http));
        await db.SaveChangesAsync(http.RequestAborted);
    }

    private static ILogger Logger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MicrosoftSignIn));

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Microsoft sign-in failed.")]
    private static partial void LogRemoteFailure(ILogger logger, Exception? exception);
}
