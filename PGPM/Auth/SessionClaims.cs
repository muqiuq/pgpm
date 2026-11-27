using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using PGPM.Lib.Helper;

namespace PGPM.Auth;

/// <summary>How a session signed in (the <see cref="SessionClaims.AuthMethod"/> claim).</summary>
public enum AuthMethod { Password, Microsoft }

/// <summary>The claims of a session (PNMTD, docs/03 §4.2): no secret, and the stamp the session is checked against.</summary>
public static class SessionClaims
{
    public const string DisplayName = "pgpm:display_name";
    public const string SecurityStamp = "pgpm:stamp";
    public const string MustChangePassword = "pgpm:must_change_password";

    /// <summary>
    /// <c>password</c> or <c>microsoft</c>. A Microsoft session skips the password and TOTP gates: MFA is Entra's job
    /// (docs/03 §4.2, GGITSV's UserAccess.SessionStateFor). In the cookie.
    /// </summary>
    public const string AuthMethod = "pgpm:auth_method";

    /// <summary>
    /// Auth:TotpPolicy requires 2FA and the user has none yet. Never in the cookie: added on every request
    /// (<see cref="SessionCache.ValidateAsync"/>), so a policy change applies to running sessions too.
    /// </summary>
    public const string TotpSetupPending = "pgpm:totp_setup_pending";

    /// <summary>The admin flag. Like the rights never in the cookie: added on every request from the user's groups.</summary>
    public const string Admin = "pgpm:admin";

    /// <summary>One per area: "Projects:Create".</summary>
    public const string Right = "pgpm:right";

    /// <summary>One per group the user is in (the sharing checks of phase 2). Never in the cookie.</summary>
    public const string Group = "pgpm:group";

    /// <summary>When the password (or Microsoft) was last entered for this session, Unix seconds. In the cookie.</summary>
    public const string SignedIn = "pgpm:signed_in";

    /// <summary>When the 2FA code was last entered for this session, Unix seconds. In the cookie.</summary>
    public const string TotpAt = "pgpm:totp_at";

    /// <summary>The day's code is due. Never in the cookie: added on every request.</summary>
    public const string TotpDue = "pgpm:totp_due";

    /// <summary>The claims added per request, which a cookie must never carry.</summary>
    public static readonly IReadOnlySet<string> PerRequest = new HashSet<string>(StringComparer.Ordinal)
    {
        TotpSetupPending, TotpDue, Admin, Right, Group, ClaimTypes.Role,
    };

    /// <summary>
    /// A session's principal. <paramref name="signedIn"/> defaults to now; <paramref name="totpAt"/> is when the code was
    /// entered, null for a sign-in without 2FA.
    /// </summary>
    public static ClaimsPrincipal Create(UserEntity user, DateTime? signedIn = null, DateTime? totpAt = null,
        Auth.AuthMethod method = Auth.AuthMethod.Password)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(DisplayName, user.DisplayName),
            new(SecurityStamp, user.SecurityStamp),
            new(AuthMethod, method.ToString().ToLowerInvariant()),
            SessionRules.TimeClaim(SignedIn, signedIn ?? Clock.UtcNow),
        };
        if (totpAt is { } at)
        {
            claims.Add(SessionRules.TimeClaim(TotpAt, at));
        }
        if (user.MustChangePassword && method == Auth.AuthMethod.Password)
        {
            claims.Add(new Claim(MustChangePassword, "true"));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static Guid? UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>The session's method; a session without the claim (the tests' sign-in) is a password one.</summary>
    public static AuthMethod Method(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(AuthMethod) == "microsoft" ? Auth.AuthMethod.Microsoft : Auth.AuthMethod.Password;

    /// <summary>The ids of the user's groups, as the request's session cache entry gave them.</summary>
    public static IReadOnlyList<Guid> GroupIds(this ClaimsPrincipal principal) =>
        principal.FindAll(Group).Select(c => Guid.TryParse(c.Value, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
}
