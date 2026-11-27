using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PGPM.Auth;

/// <summary>
/// The phase-0 skeleton of PNMTD's authentication (docs/03 §4): the server protects itself from day one.
/// <list type="bullet">
/// <item>Cookie authentication only. It never redirects: 401 when signed out, 403 when a right is missing.</item>
/// <item>Deny by default: an endpoint without [AllowAnonymous] requires a signed-in user.</item>
/// <item>Data Protection keys on the keys volume, so sessions (and later the TOTP secrets) survive restarts.</item>
/// <item>Antiforgery for unsafe v1 calls with the session cookie (<see cref="CsrfProtectionAttribute"/>).</item>
/// </list>
/// Phase 1 adds the login endpoints, SessionCache, SessionRules and SessionGates, the rights policies, the login rate
/// limit and Microsoft SSO (GGITSV) on this scheme.
/// </summary>
public static class AuthSetup
{
    public const string CookieName = "pgpm.auth";

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

    public static IServiceCollection AddPgpmAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddPgpmDataProtection(configuration);

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.Path = "/";
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
            });

        services.AddAntiforgery(options =>
        {
            options.HeaderName = CsrfHeaderName;
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        // Phase 1 adds the login policy (PNMTD: 10 a minute per address).
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
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

/// <summary>Issues the antiforgery tokens for the session in <paramref name="http"/>'s User (phase 1: /auth/me, the login).</summary>
public static class CsrfTokens
{
    public static void Issue(HttpContext http)
    {
        var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
        http.Response.Cookies.Append(AuthSetup.CsrfCookieName, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = "/",
        });
    }
}
