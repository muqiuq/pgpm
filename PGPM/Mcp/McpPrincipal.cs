using System.Security.Claims;
using PGPM.Auth;

namespace PGPM.Mcp;

/// <summary>The MCP token's authentication scheme (docs/03 §7.2): never a default scheme, only <c>/mcp</c>'s policy names it.</summary>
public static class McpTokenDefaults
{
    public const string Scheme = "McpToken";
}

/// <summary>
/// The principal of an MCP request (docs/03 §7.2): the token's user, the token, and the capped rights as the usual
/// <c>pgpm:right</c> claims, so that every <c>User.Has(…)</c> of PGPM's services sees what the token may do. Never
/// <c>pgpm:admin</c>.
/// </summary>
public static class McpPrincipal
{
    public const string TokenId = "pgpm:mcp_token";
    public const string Kind = "pgpm:mcp_kind";

    public static ClaimsPrincipal Create(McpCaller caller)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, caller.User.Id.ToString()),
            new(ClaimTypes.Name, caller.User.Username),
            new(SessionClaims.DisplayName, caller.User.DisplayName),
            new(TokenId, caller.Token.Id.ToString()),
            new(Kind, caller.Token.Kind.ToString()),
        };
        claims.AddRange(caller.Rights.Claims().Where(c => c.Type != SessionClaims.Admin));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, McpTokenDefaults.Scheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    public static bool IsMcp(ClaimsPrincipal? principal) =>
        principal?.Identity is { IsAuthenticated: true, AuthenticationType: McpTokenDefaults.Scheme };

    public static Guid? TokenOf(ClaimsPrincipal? principal) =>
        IsMcp(principal) && Guid.TryParse(principal!.FindFirstValue(TokenId), out var id) ? id : null;

    public static McpTokenKind? KindOf(ClaimsPrincipal? principal) =>
        IsMcp(principal) && Enum.TryParse<McpTokenKind>(principal!.FindFirstValue(Kind), out var kind) ? kind : null;

    /// <summary>The audit's <c>actor_ref</c>: <c>token:{id}:rw</c> or <c>:ro</c>.</summary>
    public static string? Reference(ClaimsPrincipal? principal) =>
        TokenOf(principal) is { } id ? Reference(id, KindOf(principal) ?? McpTokenKind.Read) : null;

    public static string Reference(Guid tokenId, McpTokenKind kind) => $"token:{tokenId}:{(kind == McpTokenKind.Write ? "rw" : "ro")}";
}
