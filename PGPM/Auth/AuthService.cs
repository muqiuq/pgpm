using System.Net;
using Microsoft.EntityFrameworkCore;
using PGPM.Audit;
using PGPM.Data;
using PGPM.Lib.Helper;
using PGPM.Lib.Security;

namespace PGPM.Auth;

public enum LoginStatus { Success, InvalidCredentials, LockedOut, TotpRequired, InvalidCode, LoginExpired, NetworkNotAllowed }

public sealed record LoginOutcome(LoginStatus Status, UserEntity? User = null, TimeSpan? RetryAfter = null);

/// <summary>
/// Auth:TotpPolicy (docs/03 §4.4), for local sessions only. Optional: the UI suggests 2FA after a sign-in without it. The
/// Required policies answer every call but setting it up with 403 totp_setup_required until it is on.
/// </summary>
public enum TotpPolicy { Optional, RequiredForAdmins, Required }

/// <summary>
/// The local sign-in (PNMTD, docs/03 §4.1–§4.4). The reasons for a failure go to the audit log only; the client always
/// hears invalid_credentials.
/// <list type="bullet">
/// <item>An unknown name (or an account without a password) is verified against a dummy hash, so it takes as long as a
///   wrong password.</item>
/// <item>With 2FA the password is step 1 and the code step 2 (<see cref="LoginTotpAsync"/>).</item>
/// <item>Lockout: 5 failures in a row, wrong passwords and wrong codes alike, lock the account for 5 minutes, then 10, 20,
///   40 and at most 60. Only a complete sign-in clears the count. An admin's unlock, a password or 2FA reset clear it too.</item>
/// <item>A hash with fewer iterations than today's is replaced at the next success.</item>
/// <item>Auth:LocalLoginNetworks (GGITSV): from other addresses, 403 local_login_not_allowed before anything is checked.</item>
/// </list>
/// </summary>
public sealed class AuthService(PgpmDbContext db, UserAccounts accounts, TotpSecrets secrets, LocalLoginRule localLogin)
{
    public const int FailuresBeforeLockout = 5;

    public static TimeSpan LockoutFor(int previousLockouts) =>
        TimeSpan.FromMinutes(Math.Min(60, 5 << Math.Min(previousLockouts, 4)));

    public async Task<LoginOutcome> LoginAsync(string? username, string? password, AuditWho who, IPAddress? address, CancellationToken ct = default)
    {
        var typed = (username ?? "").Trim();
        typed = typed.Length > 128 ? typed[..128] : typed;
        if (!localLogin.IsAllowed(address))
        {
            accounts.Log(AuditEvents.LoginRefused, null, who,
                localLogin.Mode == LocalLoginMode.None ? "the local login is off (Auth:LocalLoginNetworks=none)" : "not from this address (Auth:LocalLoginNetworks)",
                typed);
            await accounts.SaveAsync(ct);
            return new LoginOutcome(LoginStatus.NetworkNotAllowed);
        }
        var normalized = UserAccounts.Normalize(typed);
        var user = normalized.Length == 0 ? null : await db.Users.SingleOrDefaultAsync(u => u.NormalizedUsername == normalized, ct);
        var now = Clock.UtcNow;

        if (user?.LockoutEnd is { } lockedUntil && lockedUntil > now)
        {
            accounts.Log(AuditEvents.LoginFailed, user, who, "locked out");
            await accounts.SaveAsync(ct);
            return new LoginOutcome(LoginStatus.LockedOut, RetryAfter: lockedUntil - now);
        }

        var passwordValid = PasswordHasher.Verify(password, user?.PasswordHash ?? PasswordHasher.DummyHash) && user?.PasswordHash is not null;
        var failure = user is null ? "unknown user"
            : user.PasswordHash is null ? "no password (signs in with Microsoft)"
            : !passwordValid ? "wrong password"
            : !user.IsActive ? "inactive"
            : user.TemporaryPasswordExpiresAt is { } expires && expires < now ? "temporary password expired"
            : null;
        if (failure is not null)
        {
            accounts.Log(AuditEvents.LoginFailed, user, who, failure, typed);
            if (user?.PasswordHash is not null && !passwordValid)
            {
                CountFailure(user, who, now);
            }
            await accounts.SaveAsync(ct);
            return new LoginOutcome(LoginStatus.InvalidCredentials);
        }

        var detail = (string?)null;
        if (PasswordHasher.NeedsRehash(user!.PasswordHash))
        {
            user.PasswordHash = PasswordHasher.Hash(password!);
            detail = "password hash upgraded";
        }
        if (user.IsTotpEnabled)
        {
            // The audit log gets the outcome of step 2.
            await accounts.SaveAsync(ct);
            return new LoginOutcome(LoginStatus.TotpRequired, user);
        }
        UserAccounts.Unlock(user);
        user.LastLoginAt = now;
        accounts.Log(AuditEvents.LoginSucceeded, user, who, detail ?? "password");
        await accounts.SaveAsync(ct);
        return new LoginOutcome(LoginStatus.Success, user);
    }

    /// <summary>
    /// Step 2: the code, for the user and stamp of the pgpm.2fa ticket. A wrong or reused code counts towards the lockout
    /// like a wrong password. LoginExpired: the user changed in between (a new stamp, deactivated, 2FA reset), so the
    /// sign-in starts again with the password.
    /// </summary>
    public async Task<LoginOutcome> LoginTotpAsync(Guid userId, string stamp, string? code, AuditWho who, IPAddress? address, CancellationToken ct = default)
    {
        if (!localLogin.IsAllowed(address))
        {
            return new LoginOutcome(LoginStatus.NetworkNotAllowed);
        }
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive || !user.IsTotpEnabled || user.SecurityStamp != stamp)
        {
            return new LoginOutcome(LoginStatus.LoginExpired);
        }
        if (await CheckCodeAsync(user, code, who, null, ct) is { } refused)
        {
            return refused;
        }
        user.LastLoginAt = Clock.UtcNow;
        accounts.Log(AuditEvents.LoginSucceeded, user, who, "password and 2FA");
        await accounts.SaveAsync(ct);
        return new LoginOutcome(LoginStatus.Success, user);
    }

    /// <summary>
    /// The day's code, for a session that is signed in: the checks and the lockout of step 2. LoginExpired: the user has no
    /// 2FA any more, or is gone.
    /// </summary>
    public async Task<LoginOutcome> ConfirmDailyTotpAsync(Guid userId, string? code, AuditWho who, CancellationToken ct = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive || !user.IsTotpEnabled)
        {
            return new LoginOutcome(LoginStatus.LoginExpired);
        }
        if (await CheckCodeAsync(user, code, who, "the day's code", ct) is { } refused)
        {
            return refused;
        }
        accounts.Log(AuditEvents.TotpDailyConfirmed, user, who, null);
        await accounts.SaveAsync(ct);
        return new LoginOutcome(LoginStatus.Success, user);
    }

    /// <summary>Null when the code is right (then it is used up, and the count of failures cleared); otherwise why not, saved.</summary>
    private async Task<LoginOutcome?> CheckCodeAsync(UserEntity user, string? code, AuditWho who, string? what, CancellationToken ct)
    {
        var now = Clock.UtcNow;
        var suffix = what is null ? "" : $" ({what})";
        if (user.LockoutEnd is { } lockedUntil && lockedUntil > now)
        {
            accounts.Log(AuditEvents.TotpFailed, user, who, "locked out" + suffix);
            await accounts.SaveAsync(ct);
            return new LoginOutcome(LoginStatus.LockedOut, RetryAfter: lockedUntil - now);
        }
        var secret = secrets.Unprotect(user.TotpSecretProtected);
        long step = 0;
        var check = secret is null ? TotpCheck.Invalid : TotpService.Check(secret, code, now, user.TotpLastUsedStep, out step);
        if (check != TotpCheck.Valid)
        {
            accounts.Log(AuditEvents.TotpFailed, user, who,
                (secret is null ? "secret unreadable (Data Protection keys lost?)" : check == TotpCheck.Reused ? "code already used" : "wrong code") + suffix);
            CountFailure(user, who, now);
            await accounts.SaveAsync(ct);
            return new LoginOutcome(LoginStatus.InvalidCode);
        }
        user.TotpLastUsedStep = step;
        UserAccounts.Unlock(user);
        return null;
    }

    private void CountFailure(UserEntity user, AuditWho who, DateTime now)
    {
        user.FailedLoginCount++;
        if (user.FailedLoginCount >= FailuresBeforeLockout)
        {
            var duration = LockoutFor(user.LockoutCount);
            user.LockoutEnd = now + duration;
            user.LockoutCount++;
            user.FailedLoginCount = 0;
            accounts.Log(AuditEvents.LockedOut, user, who, $"{duration.TotalMinutes:0} minutes");
        }
    }

    /// <summary>The flags the UI acts on after a sign-in and on every reload. A Microsoft session has none of them.</summary>
    public static (bool MustChangePassword, bool TotpSetupRequired, bool TotpSetupSuggested) Flags(UserEntity user, TotpPolicy policy, AuthMethod method)
    {
        if (method == AuthMethod.Microsoft)
        {
            return (false, false, false);
        }
        var required = !user.IsTotpEnabled && TotpRequired(policy, user.IsAdmin);
        return (user.MustChangePassword, required, !user.IsTotpEnabled && !required);
    }

    /// <summary>RequiredForAdmins means the admin flag; groups don't change it.</summary>
    public static bool TotpRequired(TotpPolicy policy, bool isAdmin) =>
        policy == TotpPolicy.Required || (policy == TotpPolicy.RequiredForAdmins && isAdmin);

    public static TotpPolicy Policy(IConfiguration configuration) =>
        Enum.TryParse<TotpPolicy>(configuration["Auth:TotpPolicy"], ignoreCase: true, out var policy) ? policy : TotpPolicy.Optional;
}
