namespace PGPM.Api.V1.Contracts;

/// <summary>What this server is: shown in the drawer's footer and the sign-in page (anonymous).</summary>
public sealed record AppInfo(string Name, string Version);
