using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Lib.Helper;

namespace PGPM.Api.V1;

/// <summary>The signed-in user's own account (PNMTD, docs/03 §4.5). The MCP tokens join in phase 6.</summary>
[Route("api/v1/account")]
[Authorize]
public sealed class AccountController(UserAccounts accounts, TwoFactorService twoFactor) : V1ControllerBase
{
    /// <summary>One's own groups and effective rights, with the group each level comes from.</summary>
    [HttpGet("access")]
    public async Task<AccessItem> Access([FromServices] GroupService groups, CancellationToken ct) =>
        await groups.AccessAsync(User.UserId() ?? throw ApiException.Unauthorized(), ct);

    /// <summary>Ends the user's other sessions; this one continues with a fresh cookie. 409 password_not_set for a Microsoft-only account.</summary>
    [HttpPost("password")]
    [Audit(AuditEvents.PasswordChanged)]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken ct)
    {
        var id = User.UserId() ?? throw ApiException.Unauthorized();
        await accounts.ChangePasswordAsync(id, request.CurrentPassword, request.NewPassword, AuditWho.From(HttpContext), ct);
        await RenewSessionAsync(id, ct, password: true);
        return NoContent();
    }

    /// <summary>"Sign out everywhere": every other session of this user ends; this one continues.</summary>
    [HttpPost("sessions/revoke")]
    [Audit(AuditEvents.SessionsRevoked)]
    public async Task<IActionResult> RevokeSessions(CancellationToken ct)
    {
        var id = User.UserId() ?? throw ApiException.Unauthorized();
        await accounts.RevokeSessionsAsync(id, AuditWho.From(HttpContext), ct);
        await RenewSessionAsync(id, ct);
        return NoContent();
    }

    /// <summary>Step 1 of setting up 2FA: a new secret, pending for 15 minutes (it replaces an earlier pending one). 409 totp_already_enabled.</summary>
    [HttpPost("totp/setup")]
    [Audit(AuditEvents.TotpSetupStarted)]
    public async Task<ActionResult<TotpSetup>> SetupTotp(CancellationToken ct) =>
        await twoFactor.SetupAsync(User.UserId() ?? throw ApiException.Unauthorized(), ct);

    /// <summary>
    /// Step 2: a code from the new secret turns 2FA on. Every other session of the user ends; this one continues. 400 for a
    /// wrong code, 409 totp_setup_expired: start again.
    /// </summary>
    [HttpPost("totp/confirm")]
    [Audit(AuditEvents.TotpEnabled)]
    public async Task<IActionResult> ConfirmTotp(TotpCodeRequest request, CancellationToken ct)
    {
        var id = User.UserId() ?? throw ApiException.Unauthorized();
        await twoFactor.ConfirmAsync(id, request.Code, AuditWho.From(HttpContext), ct);
        await RenewSessionAsync(id, ct, totp: true);
        return NoContent();
    }

    /// <summary>
    /// Turns 2FA off: needs the password and a current code (400 names the wrong one). Every other session ends. 409
    /// totp_required while Auth:TotpPolicy requires it, totp_not_enabled.
    /// </summary>
    [HttpPost("totp/disable")]
    [Audit(AuditEvents.TotpDisabled, AuditEvents.TotpFailed)]
    public async Task<IActionResult> DisableTotp(TotpDisableRequest request, CancellationToken ct)
    {
        var id = User.UserId() ?? throw ApiException.Unauthorized();
        await twoFactor.DisableAsync(id, request.Password, request.Code, AuditWho.From(HttpContext), ct);
        await RenewSessionAsync(id, ct, password: true, totp: true);
        return NoContent();
    }

    /// <summary>
    /// The session goes on with a fresh cookie (a new stamp). It keeps when the password and the code were entered, unless
    /// this request entered them.
    /// </summary>
    private async Task RenewSessionAsync(Guid id, CancellationToken ct, bool password = false, bool totp = false)
    {
        // Cookie sessions only: the tests' sign-in has no cookie to renew.
        if (User.Identity?.AuthenticationType == CookieAuthenticationDefaults.AuthenticationScheme)
        {
            var user = await accounts.GetAsync(id, ct);
            var now = Clock.UtcNow;
            var signedIn = password ? now : SessionRules.Time(User, SessionClaims.SignedIn) ?? now;
            var totpAt = totp ? now : SessionRules.Time(User, SessionClaims.TotpAt);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                SessionClaims.Create(user, signedIn, user.IsTotpEnabled ? totpAt : null, User.Method()),
                new AuthenticationProperties { IsPersistent = true, IssuedUtc = now });
        }
    }
}
