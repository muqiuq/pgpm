using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PGPM.Audit;

namespace PGPM.Mcp;

/// <summary>
/// The <c>McpToken</c> scheme (docs/03 §7.2, PNMTD's McpTokenHandler): <c>Authorization: Bearer pgpm_…</c>, checked by
/// <see cref="McpTokenService"/>. Only <c>/mcp</c>'s policy names it, so a token never reaches PGPM's own API. A value that
/// isn't a token, or matches none, is 401 with <c>WWW-Authenticate</c>; a known token that is only off is 403 with what to
/// do, so that clients show the message instead of starting an OAuth sign-in.
/// </summary>
public sealed class McpTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The checked caller of this request, for the tools and the log.</summary>
    public const string CallerItem = "pgpm:mcp_caller";

    private const string FailureItem = "pgpm:mcp_failure";

    public static McpCaller? CallerOf(HttpContext http) => http.Items.TryGetValue(CallerItem, out var c) ? c as McpCaller : null;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            Context.Items[FailureItem] = ("missing", (McpCaller?)null);
            return AuthenticateResult.NoResult();
        }
        var tokens = Context.RequestServices.GetRequiredService<McpTokenService>();
        var (caller, why) = await tokens.AuthenticateAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (caller is null || why != "on")
        {
            Context.Items[FailureItem] = (why, caller);
            return AuthenticateResult.Fail(why);
        }
        Context.Items[CallerItem] = caller;
        await tokens.TouchAsync(caller.Token, AuditWho.IpOf(Context), McpCalls.ClientOf(Context), Context.RequestAborted);
        return AuthenticateResult.Success(new AuthenticationTicket(McpPrincipal.Create(caller), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var (why, caller) = Context.Items.TryGetValue(FailureItem, out var f) && f is ValueTuple<string, McpCaller?> failure ? failure : ("missing", null);
        var known = caller is not null;
        Response.StatusCode = known ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        var message = Describe(why);
        if (!known)
        {
            Response.Headers.WWWAuthenticate = $"Bearer error=\"invalid_token\", error_description=\"{message}\"";
        }
        await Response.WriteAsJsonAsync(new { error = why, message });
        await Context.RequestServices.GetRequiredService<McpLog>().AuthFailedAsync(AuditWho.IpOf(Context), Request.Headers.UserAgent.ToString(),
            Context.TraceIdentifier, why, caller?.Token, caller?.User.Username);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Response.WriteAsJsonAsync(new { error = "forbidden", message = "This token may not do this." });
    }

    /// <summary>What the person can do about it, in a sentence.</summary>
    public static string Describe(string why) => why switch
    {
        "missing" => "Send your PGPM MCP token as Authorization: Bearer pgpm_….",
        "unknown" => "Unknown or replaced token. Copy the current one from Account → Security in PGPM.",
        "off" or "no_value" => "This token is off. Switch it on on Account → Security in PGPM.",
        "expired" => "This token's time is over. Switch it on again on Account → Security in PGPM.",
        "blocked" => "This token was blocked after refused requests. Its user can switch it on again on Account → Security in PGPM.",
        "stamp" => "This token went off when its user's password, 2FA or sessions changed. Switch it on again on Account → Security in PGPM.",
        "inactive" => "This token's user is deactivated.",
        "totp" => "The write token needs two-factor authentication or a linked Microsoft account on its user's account.",
        "no_right" => "This token's user no longer has the right MCP: Use.",
        "mcp_off" => "PGPM's MCP server is off.",
        _ => "This token isn't accepted.",
    };
}
