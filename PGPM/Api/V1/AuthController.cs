using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Microsoft;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Lib.Helper;

namespace PGPM.Api.V1;

/// <summary>Sign-in and sign-out (PNMTD, docs/03 §4.5), with GGITSV's Microsoft SSO start (docs/03 §5.1).</summary>
[Route("api/v1/auth")]
[Authorize]
public sealed class AuthController(AuthService auth, UserAccounts accounts, TotpSecrets secrets, PgpmDbContext db,
    IConfiguration configuration, IOptionsMonitor<AuthOptions> authOptions) : V1ControllerBase
{
    /// <summary>
    /// Step 1. 200 <c>{ next: "done" }</c> with the session cookie, or <c>{ next: "totp" }</c> with the pgpm.2fa cookie
    /// when the account has 2FA; 401 invalid_credentials (also for an unknown user, an inactive one, one without a password
    /// or an expired temporary password); 429 locked_out with Retry-After; 403 local_login_not_allowed
    /// (Auth:LocalLoginNetworks).
    /// </summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [SkipCsrf]
    [EnableRateLimiting(AuthSetup.LoginRateLimit)]
    [NotAudited("logged by AuthService: auth.login_succeeded, auth.login_failed, auth.locked_out")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var outcome = await auth.LoginAsync(request.Username, request.Password, AuditWho.From(HttpContext), HttpContext.Connection.RemoteIpAddress, ct);
        switch (outcome.Status)
        {
            case LoginStatus.Success:
                return await SignInAsync(outcome.User!, withTotp: false, ct);
            case LoginStatus.TotpRequired:
                Response.Cookies.Append(AuthSetup.TwoFactorCookieName, secrets.IssueTicket(outcome.User!), TwoFactorCookie(TwoFactorService.LoginStepLifetime));
                return new LoginResponse("totp", null);
            default:
                throw Refused(outcome);
        }
    }

    /// <summary>
    /// Step 2, within 5 minutes of step 1 (the pgpm.2fa cookie). 200 <c>{ next: "done" }</c> with the session cookie; 401
    /// invalid_code (wrong, or used already), which counts towards the lockout; 401 login_expired (no step 1, too late, or
    /// the account changed): sign in again from the password.
    /// </summary>
    [HttpPost("login/totp")]
    [AllowAnonymous]
    [SkipCsrf]
    [EnableRateLimiting(AuthSetup.LoginRateLimit)]
    [NotAudited("logged by AuthService: auth.login_succeeded, auth.totp_failed, auth.locked_out")]
    public async Task<ActionResult<LoginResponse>> LoginTotp(TotpCodeRequest request, CancellationToken ct)
    {
        var outcome = secrets.ReadTicket(Request.Cookies[AuthSetup.TwoFactorCookieName]) is { } ticket
            ? await auth.LoginTotpAsync(ticket.UserId, ticket.Stamp, request.Code, AuditWho.From(HttpContext), HttpContext.Connection.RemoteIpAddress, ct)
            : new LoginOutcome(LoginStatus.LoginExpired);
        if (outcome.Status is LoginStatus.InvalidCode)
        {
            throw Refused(outcome);
        }
        Response.Cookies.Delete(AuthSetup.TwoFactorCookieName, TwoFactorCookie(maxAge: null));
        return outcome.Status == LoginStatus.Success ? await SignInAsync(outcome.User!, withTotp: true, ct) : throw Refused(outcome);
    }

    /// <summary>
    /// The day's 2FA code, while <c>/auth/me</c> says <c>totpDue</c>. 204, and the session goes on with the code entered now;
    /// 401 invalid_code, which counts towards the lockout; 429 locked_out; 409 totp_not_enabled when the account has no 2FA.
    /// </summary>
    [HttpPost("totp/daily")]
    [EnableRateLimiting(AuthSetup.LoginRateLimit)]
    [NotAudited("logged by AuthService: auth.totp_daily_confirmed, auth.totp_failed, auth.locked_out")]
    public async Task<IActionResult> ConfirmDailyTotp(TotpCodeRequest request, CancellationToken ct)
    {
        var id = User.UserId() ?? throw ApiException.Unauthorized();
        var outcome = await auth.ConfirmDailyTotpAsync(id, request.Code, AuditWho.From(HttpContext), ct);
        if (outcome.Status == LoginStatus.LoginExpired)
        {
            throw ApiException.Conflict("totp_not_enabled", "This account has no two-factor authentication.");
        }
        if (outcome.Status != LoginStatus.Success)
        {
            throw Refused(outcome);
        }
        if (User.Identity?.AuthenticationType == CookieAuthenticationDefaults.AuthenticationScheme)
        {
            var principal = SessionClaims.Create(outcome.User!, SessionRules.Time(User, SessionClaims.SignedIn), Clock.UtcNow, User.Method());
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
                new AuthenticationProperties { IsPersistent = true, IssuedUtc = Clock.UtcNow });
        }
        return NoContent();
    }

    private ApiException Refused(LoginOutcome outcome)
    {
        switch (outcome.Status)
        {
            case LoginStatus.LockedOut:
                Response.Headers.RetryAfter = ((int)Math.Ceiling(outcome.RetryAfter!.Value.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return new ApiException(StatusCodes.Status429TooManyRequests, "locked_out", "Too many failed sign-ins. Try again later.");
            case LoginStatus.InvalidCode:
                return new ApiException(StatusCodes.Status401Unauthorized, "invalid_code", "Wrong code, or one that was used already.");
            case LoginStatus.LoginExpired:
                return new ApiException(StatusCodes.Status401Unauthorized, SessionRules.LoginExpiredCode, "The sign-in has expired. Enter your password again.");
            case LoginStatus.NetworkNotAllowed:
                return new ApiException(StatusCodes.Status403Forbidden, "local_login_not_allowed",
                    "Signing in with a password is not allowed from here. Sign in with Microsoft.");
            default:
                return new ApiException(StatusCodes.Status401Unauthorized, "invalid_credentials", "Wrong username or password.");
        }
    }

    /// <summary>The ticket itself expires after 5 minutes too; Max-Age only spares sending a dead one.</summary>
    private CookieOptions TwoFactorCookie(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = Request.IsHttps,
        Path = AuthSetup.TwoFactorCookiePath,
        MaxAge = maxAge,
    };

    /// <summary>Ends this PGPM session only: a Microsoft session at Entra stays (no single sign-out, docs/03 §5.3).</summary>
    [HttpPost("logout")]
    [Audit(AuditEvents.Logout)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        if (User.UserId() is { } id && await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct) is { } user)
        {
            accounts.Log(AuditEvents.Logout, user, AuditWho.From(HttpContext), null);
            await accounts.SaveAsync(ct);
        }
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Response.Cookies.Delete(AuthSetup.CsrfCookieName, new CookieOptions { Path = "/" });
        return NoContent();
    }

    /// <summary>
    /// Anonymous: 401 when signed out. Signed in: the user with their rights, groups and person, the flags and the session,
    /// and a fresh XSRF-TOKEN cookie.
    /// </summary>
    [HttpGet("me")]
    [AllowAnonymous]
    public async Task<ActionResult<MeResponse>> Me(CancellationToken ct)
    {
        var user = User.UserId() is { } id ? await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == id, ct) : null;
        if (user is null)
        {
            throw ApiException.Unauthorized();
        }
        CsrfTokens.Issue(HttpContext);
        // The test sign-in carries rights but no groups: those come from the database then.
        var groupIds = User.HasClaim(c => c.Type == SessionClaims.Group) ? User.GroupIds()
            : await db.UserGroups.AsNoTracking().Where(m => m.UserId == user.Id).Select(m => m.GroupId).ToListAsync(ct);
        return await MeAsync(user, User.Rights(), groupIds, User, ct);
    }

    /// <summary>Which sign-ins there are for this caller (GGITSV): Microsoft when SSO is on, the password when allowed from here.</summary>
    [HttpGet("options")]
    [AllowAnonymous]
    public SignInOptions Options([FromServices] IOptions<EntraOptions> entra, [FromServices] LocalLoginRule localLogin) =>
        new(entra.Value.Enabled, localLogin.IsAllowed(HttpContext.Connection.RemoteIpAddress));

    /// <summary>
    /// Starts the Microsoft sign-in (docs/03 §5.1): a redirect to Entra; afterwards to <paramref name="returnTo"/> (a local
    /// path only, else <c>/</c>) or to <c>/sign-in?ssoError=…</c>. 404 microsoft_sign_in_disabled when SSO is off.
    /// </summary>
    [HttpGet("sso/microsoft")]
    [AllowAnonymous]
    public async Task<IActionResult> MicrosoftSignInStart([FromQuery] string? returnTo, [FromServices] IAuthenticationSchemeProvider schemes)
    {
        if (await schemes.GetSchemeAsync(MicrosoftSignIn.Scheme) is null)
        {
            throw new ApiException(StatusCodes.Status404NotFound, "microsoft_sign_in_disabled", "Microsoft sign-in is not configured.");
        }
        return Challenge(new AuthenticationProperties { RedirectUri = ReturnPaths.Normalize(returnTo) }, MicrosoftSignIn.Scheme);
    }

    /// <summary>A complete sign-in: the password now, and the code now when <paramref name="withTotp"/>.</summary>
    private async Task<LoginResponse> SignInAsync(UserEntity user, bool withTotp, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var principal = SessionClaims.Create(user, now, withTotp ? now : null);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = true, IssuedUtc = now });
        // The request tokens are bound to the user: issued for the new session, not the anonymous one.
        HttpContext.User = principal;
        CsrfTokens.Issue(HttpContext);
        var entry = await SessionCache.LoadAsync(db, user.Id, ct);
        return new LoginResponse("done", await MeAsync(user, entry?.Rights ?? UserRights.None, entry?.GroupIds ?? [], principal, ct));
    }

    private async Task<MeResponse> MeAsync(UserEntity user, UserRights rights, IReadOnlyList<Guid> groupIds, System.Security.Claims.ClaimsPrincipal session,
        CancellationToken ct)
    {
        var personId = await db.Persons.AsNoTracking().Where(p => p.UserId == user.Id).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        var method = session.Method();
        var (mustChange, totpRequired, totpSuggested) = AuthService.Flags(user, AuthService.Policy(configuration), method);
        var until = SessionRules.Time(session, SessionClaims.SignedIn) is { } signedIn ? SessionRules.LoginEnds(signedIn, authOptions.CurrentValue) : null;
        return new MeResponse(
            new CurrentUser(user.Id, user.Username, user.DisplayName, user.Email, rights.IsAdmin, rights.Levels, groupIds, personId,
                user.IsTotpEnabled, user.PasswordHash is not null, user.HasMicrosoftAccount),
            new SessionFlags(mustChange, totpRequired, totpSuggested),
            new SessionTimes(session.HasClaim(SessionClaims.TotpDue, "true"), until, method));
    }
}
