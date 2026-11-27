using System.Security.Claims;

namespace PGPM.Auth.Microsoft;

/// <summary>The account of a validated Entra ID token (GGITSV's ExternalIdentity), and Graph's <c>/me</c> when it answered.</summary>
public sealed record ExternalIdentity(Guid ObjectId, Guid TenantId, string? PreferredUserName, string? DisplayName, string? Email,
    IReadOnlyList<string> Roles)
{
    public MicrosoftDirectoryProfile? Directory { get; init; }
}

/// <summary>What Graph's <c>/me</c> says about the account.</summary>
public sealed record MicrosoftDirectoryProfile(string? UserPrincipalName, string? Mail);

/// <summary>
/// Reads the account from the claims of an Entra ID token (v2.0, GGITSV). The claims keep their JWT names
/// (<c>MapInboundClaims = false</c>): <c>oid</c>, <c>tid</c>, <c>preferred_username</c>, <c>name</c>, <c>email</c>,
/// <c>roles</c> (one claim per app role).
/// </summary>
public static class MicrosoftClaims
{
    public const string ObjectId = "oid";
    public const string TenantId = "tid";
    public const string PreferredUserName = "preferred_username";
    public const string Name = "name";
    public const string Email = "email";
    public const string Roles = "roles";

    /// <summary>The account of a validated token; null when the token lacks a valid object id or tenant.</summary>
    public static ExternalIdentity? ToExternalIdentity(ClaimsPrincipal? principal)
    {
        if (!Guid.TryParse(principal?.FindFirstValue(ObjectId), out var objectId)
            || !Guid.TryParse(principal.FindFirstValue(TenantId), out var tenantId)
            || objectId == Guid.Empty
            || tenantId == Guid.Empty)
        {
            return null;
        }
        return new ExternalIdentity(
            objectId,
            tenantId,
            principal.FindFirstValue(PreferredUserName),
            principal.FindFirstValue(Name),
            principal.FindFirstValue(Email),
            principal.FindAll(Roles).Select(c => c.Value).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList());
    }
}
