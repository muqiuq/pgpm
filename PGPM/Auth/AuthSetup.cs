using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using PGPM.Auth.Microsoft;

namespace PGPM.Auth;

/// <summary>
/// PNMTD's authentication (docs/03 §4) with GGITSV's Microsoft SSO on it (§5): the server protects itself.
/// <list type="bullet">
/// <item>Cookie authentication only (<c>pgpm.auth</c>, SameSite=Strict, 7 days sliding). It never redirects: 401 when
///   signed out, 403 when a right is missing. Every request is checked against the user (<see cref="SessionCache"/>).</item>
/// <item>Deny by default: an endpoint without [AllowAnonymous] requires a signed-in user.</item>
/// <item>Data Protection keys on the keys volume, so sessions and the TOTP secrets survive restarts.</item>
/// <item>Antiforgery for unsafe v1 calls with the session cookie (<see cref="CsrfProtectionAttribute"/>).</item>
/// <item>The login endpoints: 10 attempts a minute per address.</item>
/// <item>Rights, not roles: <c>[RequireRight]</c>, <c>[RequireAdmin]</c> become policies (<see cref="Rights.RightPolicyProvider"/>).</item>
/// </list>
/// </summary>
public static class AuthSetup
{
    public const string CookieName = "pgpm.auth";

    /// <summary>A sign-in between the password and the code: 5 minutes, on the auth endpoints only.</summary>
    public const string TwoFactorCookieName = "pgpm.2fa";

    public const string TwoFactorCookiePath = "/api/v1/auth";

    /// <summary>The rate limiter policy of the login endpoints.</summary>
    public const string LoginRateLimit = "login";

    /// <summary>The antiforgery cookie token (HttpOnly) …</summary>
    public const string AntiforgeryCookieName = "pgpm.antiforgery";

    /// <summary>… and the request token, readable by the SPA, which sends it back as <see cref="CsrfHeaderName"/>.</summary>
    public const string CsrfCookieName = "XSRF-TOKEN";

    public const string CsrfHeaderName = "X-XSRF-TOKEN";

    /// <summary>Where the Data Protection key ring lives: the pgpm-keys volume in the image.</summary>
    public const string DefaultKeysPath = "/var/lib/pgpm/keys";

    public static IDataProtectionBuilder AddPgpmDataProtection(this IServiceCollection services, IConfiguration configuration) =>
        services.AddDataProtection()
            .SetApplicationName("PGPM")
            .PersistKeysToFileSystem(new DirectoryInfo(configuration["DataProtection:KeysPath"] ?? DefaultKeysPath));

    /// <summary>
    /// Whether every cookie is <c>Secure</c> whatever the request looked like: when <c>PublicUrl</c> is https. Behind a
    /// reverse proxy without the <c>Proxy</c> setting the app sees plain http and would otherwise send its session cookie
    /// without the flag (docs/06 S-03).
    /// </summary>
    public static bool SecureCookies(IConfiguration configuration) =>
        Microsoft.MicrosoftSignIn.PublicUrl(configuration)?.Scheme == Uri.UriSchemeHttps;

    /// <summary>The cookies' Secure policy: always with an https PublicUrl, else as the request came.</summary>
    public static CookieSecurePolicy SecurePolicy(IConfiguration configuration) =>
        SecureCookies(configuration) ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;

    /// <summary>A cookie set by hand is Secure on https, or with an https PublicUrl.</summary>
    public static bool SecureCookie(HttpContext http) =>
        http.Request.IsHttps || SecureCookies(http.RequestServices.GetRequiredService<IConfiguration>());

    public static IServiceCollection AddPgpmAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPgpmDataProtection(configuration);
        var securePolicy = SecurePolicy(configuration);

        // Settings a server can't work with stop the start: a bad network, SSO without its registration, nobody able to sign in.
        var localLogin = LocalLoginRule.Parse(configuration["Auth:LocalLoginNetworks"]);
        var entra = configuration.GetSection(EntraOptions.Section).Get<EntraOptions>() ?? new EntraOptions();
        entra.Validate();
        if (localLogin.Mode == LocalLoginMode.None && !entra.Enabled)
        {
            throw new InvalidOperationException(
                "Auth:LocalLoginNetworks=none switches the local login off, which needs Microsoft SSO (Entra:Enabled=true): otherwise nobody could sign in.");
        }
        services.AddSingleton(localLogin);

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = securePolicy;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = TimeSpan.FromDays(configuration.GetValue("Auth:SessionDays", 7));
                options.SlidingExpiration = true;
                // An API and a SPA: no redirects to a login page.
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToLogout = _ => Task.CompletedTask;
                options.Events.OnRedirectToReturnUrl = _ => Task.CompletedTask;
                options.Events.OnValidatePrincipal = SessionCache.ValidateAsync;
            });
        services.AddMicrosoftSignIn(configuration);

        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.Section));
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.AddSingleton<SessionCache>();
        services.AddScoped(sp => new UserAccounts(sp.GetRequiredService<Data.PgpmDbContext>(), sp.GetRequiredService<SessionCache>()));
        services.AddScoped<AuthService>();
        services.AddSingleton<TotpSecrets>();
        services.AddScoped<TwoFactorService>();
        services.AddScoped<GroupService>();

        services.AddAntiforgery(options =>
        {
            options.HeaderName = CsrfHeaderName;
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.SameSite = SameSiteMode.Strict;
            // As the request came, even with an https PublicUrl: antiforgery refuses Always on a plain-http request (a proxy
            // without the Proxy setting). The cookie token is worth nothing without the session cookie, which is Secure then.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.Path = "/";
            // The security headers set X-Frame-Options already.
            options.SuppressXFrameOptionsHeader = true;
        });

        // 10 attempts a minute per client address (Auth:LoginRateLimit:PermitLimit, read when a client first comes), on the
        // login endpoints only. Behind a reverse proxy the address needs the Proxy setting.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = context.RequestServices.GetRequiredService<IConfiguration>().GetValue("Auth:LoginRateLimit:PermitLimit", 10),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
            options.OnRejected = async (context, _) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromMinutes(1);
                context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, detail: "Too many sign-in attempts. Try again later.",
                    extensions: new Dictionary<string, object?> { ["code"] = Api.ApiErrorCodes.RateLimited }).ExecuteAsync(context.HttpContext);
            };
        });

        // Rights, not roles (docs/03 §6.1): [RequireRight(area, level)] and [RequireAdmin] become policies; the request's
        // rights come from its session (SessionCache.ValidateAsync). The refusal's handler is the audit's (AddAudit).
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddSingleton<IAuthorizationPolicyProvider, Rights.RightPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, Rights.RightHandler>();

        return services;
    }

    /// <summary>After the app is built: the Microsoft scheme goes while SSO is off.</summary>
    public static WebApplication UsePgpmAuthSchemes(this WebApplication app)
    {
        MicrosoftSignIn.RemoveDisabledSchemes(app.Services);
        return app;
    }
}

/// <summary>
/// Unsafe v1 calls with a session cookie must carry the request token from the XSRF-TOKEN cookie in X-XSRF-TOKEN
/// (PNMTD). Requests without the cookie session (signed out: 401 before this runs) are not affected, nor are actions
/// marked <see cref="SkipCsrfAttribute"/> (the login).
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CsrfProtectionAttribute : Attribute, IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        var unsafeMethod = !(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
            || HttpMethods.IsOptions(http.Request.Method) || HttpMethods.IsTrace(http.Request.Method));
        if (unsafeMethod
            && http.User.Identity is { IsAuthenticated: true, AuthenticationType: CookieAuthenticationDefaults.AuthenticationScheme }
            && context.ActionDescriptor.EndpointMetadata.OfType<SkipCsrfAttribute>().FirstOrDefault() is null)
        {
            try
            {
                await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                context.Result = new ObjectResult(new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Forbidden",
                    Detail = $"The {AuthSetup.CsrfHeaderName} header is missing or does not match the {AuthSetup.CsrfCookieName} cookie.",
                    Extensions = { ["code"] = "csrf_token_invalid" },
                })
                { StatusCode = StatusCodes.Status403Forbidden, ContentTypes = { "application/problem+json" } };
                return;
            }
        }
        await next();
    }
}

/// <summary>No antiforgery check: the login steps, which run before there is a session.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SkipCsrfAttribute : Attribute;

/// <summary>Issues the antiforgery tokens for the session in <paramref name="http"/>'s User (/auth/me, the login).</summary>
public static class CsrfTokens
{
    public static void Issue(HttpContext http)
    {
        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        http.Response.Cookies.Append(AuthSetup.CsrfCookieName, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = AuthSetup.SecureCookie(http),
            Path = "/",
        });
    }
}
