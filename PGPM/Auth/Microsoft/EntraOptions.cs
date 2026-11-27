namespace PGPM.Auth.Microsoft;

/// <summary>
/// Microsoft SSO's settings (docs/03 §5, §14): <c>Entra:Enabled</c>, <c>TenantId</c>, <c>ClientId</c>,
/// <c>ClientSecret</c> (one app registration, single tenant), <c>AutoProvision</c> (a user at the first sign-in, default
/// on) and <c>DefaultGroupId</c> (the group of a user who has none after the app roles). The redirect URI is built from
/// <c>PublicUrl</c>.
/// </summary>
public sealed class EntraOptions
{
    public const string Section = "Entra";

    public bool Enabled { get; set; }

    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public bool AutoProvision { get; set; } = true;

    public Guid? DefaultGroupId { get; set; }

    /// <summary>A switched-on SSO without its registration cannot work: the start stops with what is missing.</summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }
        var missing = new[] { (nameof(TenantId), TenantId), (nameof(ClientId), ClientId), (nameof(ClientSecret), ClientSecret) }
            .Where(s => string.IsNullOrWhiteSpace(s.Item2)).Select(s => $"Entra:{s.Item1}").ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Entra:Enabled is on, but {string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} not set.");
        }
        if (!Guid.TryParse(TenantId, out _))
        {
            throw new InvalidOperationException("Entra:TenantId must be the tenant's id (a GUID): PGPM is single-tenant.");
        }
    }

    public Guid? Tenant => Guid.TryParse(TenantId, out var t) ? t : null;
}
