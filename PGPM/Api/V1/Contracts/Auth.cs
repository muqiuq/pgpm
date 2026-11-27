using PGPM.Auth;
using PGPM.Auth.Rights;

namespace PGPM.Api.V1.Contracts;

/// <summary><c>POST /auth/login</c>.</summary>
public sealed class LoginRequest
{
    public string? Username { get; init; }

    public string? Password { get; init; }
}

/// <summary><c>POST /auth/login/totp</c>, <c>/auth/totp/daily</c>: the code from the authenticator app (6 digits, spaces allowed).</summary>
public sealed class TotpCodeRequest
{
    public string? Code { get; init; }
}

/// <summary>
/// After a login step: <c>next</c> is <c>done</c> (signed in, <see cref="Me"/> set) or <c>totp</c> (the code is next,
/// within 5 minutes, at <c>/auth/login/totp</c>).
/// </summary>
public sealed record LoginResponse(string Next, MeResponse? Me);

/// <summary>
/// Who is signed in and what they may do (docs/03 §4.5, §6.1): the admin flag, or the level per area their groups give;
/// their groups and their person (docs/03 §6.3).
/// </summary>
public sealed record CurrentUser(
    Guid Id,
    string Username,
    string DisplayName,
    string? Email,
    bool IsAdmin,
    IReadOnlyDictionary<Area, Level> Rights,
    IReadOnlyList<Guid> GroupIds,
    Guid? PersonId,
    bool IsTotpEnabled,
    bool HasPassword,
    bool HasMicrosoftAccount);

/// <summary>
/// What the UI must do before anything else: change the password (every other call is 403 must_change_password until
/// then), set up 2FA because the policy requires it, or offer to. Always false for a Microsoft session.
/// </summary>
public sealed record SessionFlags(bool MustChangePassword, bool TotpSetupRequired, bool TotpSetupSuggested);

/// <summary>
/// The session itself. <see cref="TotpDue"/>: the day's code is due, every other call is 403 totp_due until it is entered
/// at <c>/auth/totp/daily</c>. <see cref="SignedInUntil"/>: when the sign-in is asked for again (Auth:LoginDays), null
/// when the rule is off. <see cref="AuthMethod"/>: password or Microsoft.
/// </summary>
public sealed record SessionTimes(bool TotpDue, DateTime? SignedInUntil, AuthMethod AuthMethod);

/// <summary><c>GET /auth/me</c>; the UI keeps the last one for an offline start (docs/03 §4.5).</summary>
public sealed record MeResponse(CurrentUser User, SessionFlags Flags, SessionTimes Session);

/// <summary><c>GET /auth/options</c> (GGITSV): which sign-ins this server offers the caller.</summary>
public sealed record SignInOptions(bool Microsoft, bool Local);

/// <summary><c>POST /account/password</c>: the current password and a new one (10 to 256 characters).</summary>
public sealed class ChangePasswordRequest
{
    public string? CurrentPassword { get; init; }

    public string? NewPassword { get; init; }
}

/// <summary>
/// <c>POST /account/totp/setup</c>: a new secret for the authenticator app, as a QR code (<see cref="OtpauthUri"/>) or
/// typed (<see cref="Secret"/>, Base32). It turns 2FA on only when <c>/account/totp/confirm</c> gets a code from it
/// before <see cref="ExpiresAt"/>.
/// </summary>
public sealed record TotpSetup(string Secret, string OtpauthUri, DateTime ExpiresAt);

/// <summary><c>POST /account/totp/disable</c>: the password and a current code.</summary>
public sealed class TotpDisableRequest
{
    public string? Password { get; init; }

    public string? Code { get; init; }
}
