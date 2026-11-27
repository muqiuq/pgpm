namespace PGPM.Mcp;

/// <summary>The two tokens of a user (docs/02 §3 <c>mcp_tokens.kind</c>): <c>read</c> and <c>write</c>.</summary>
public enum McpTokenKind { Read, Write }

/// <summary>
/// Why a token went off (docs/02 §3 <c>mcp_tokens.ended_why</c>). Text with a CHECK, not an enum: the lower-case enum
/// converter would write <c>userinactive</c>.
/// </summary>
public static class McpTokenEnds
{
    public const string Off = "off";
    public const string Expired = "expired";
    public const string Regenerated = "regenerated";
    public const string Stamp = "stamp";
    public const string Strikes = "strikes";
    public const string Admin = "admin";
    public const string UserInactive = "user_inactive";
    public const string TotpOff = "totp_off";

    public static readonly IReadOnlyList<string> All = [Off, Expired, Regenerated, Stamp, Strikes, Admin, UserInactive, TotpOff];
}

/// <summary>How an MCP request ended (docs/02 §3 <c>mcp_log.outcome</c>), plus <c>unauthenticated</c> for a refused token.</summary>
public static class McpOutcomes
{
    public const string Ok = "ok";
    public const string Error = "error";
    public const string Denied = "denied";
    public const string RateLimited = "rate_limited";
    public const string Unauthenticated = "unauthenticated";

    public static readonly IReadOnlyList<string> All = [Ok, Error, Denied, RateLimited, Unauthenticated];
}

/// <summary>
/// One of a user's two MCP tokens (docs/02 §3 <c>mcp_tokens</c>, PNMTD's McpTokenEntity): at most one row per kind, made
/// when the user first opens the MCP card. Only the SHA-256 of the value is kept; <see cref="Hash"/> is null until a value
/// is made and after <i>Revoke all</i>.
/// </summary>
public sealed class McpTokenEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public McpTokenKind Kind { get; set; }

    /// <summary>SHA-256 lower-hex of the value; unique where set.</summary>
    public string? Hash { get; set; }

    /// <summary>The value's last 4 characters.</summary>
    public string? Hint { get; set; }

    public DateTime? IssuedAt { get; set; }

    public DateTime? OnSince { get; set; }

    /// <summary>The write token's end, set when switched on (<c>on_since + Mcp:WriteHours</c>).</summary>
    public DateTime? OnUntil { get; set; }

    /// <summary>The user's security stamp when switched on: another one turns the token off.</summary>
    public string? OnStamp { get; set; }

    /// <summary>The delete window (reserved: no delete tools in v1).</summary>
    public DateTime? DeleteUntil { get; set; }

    public int DeleteCount { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public string? LastUsedFrom { get; set; }

    public string? LastClient { get; set; }

    /// <summary>Refused calls; <c>Mcp:StrikesToBlock</c> (3) within 24 hours block the token.</summary>
    public List<DateTime> Strikes { get; set; } = [];

    public DateTime? BlockedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    /// <summary>One of <see cref="McpTokenEnds"/>.</summary>
    public string? EndedWhy { get; set; }

    /// <summary>The admin's reason, which the user reads.</summary>
    public string? EndedNote { get; set; }
}

/// <summary>
/// One MCP request (docs/02 §3 <c>mcp_log</c>, PNMTD's McpLogEntity): a tool call, a tool list, or a refused token. The
/// arguments redacted, the result gzipped, the newest <c>Mcp:LogEntries</c> (10,000) kept.
/// </summary>
public sealed class McpLogEntity
{
    public long Id { get; set; }

    public DateTime At { get; set; }

    public Guid? TokenId { get; set; }

    public McpTokenKind? Kind { get; set; }

    public Guid? UserId { get; set; }

    /// <summary>The user's name then (an entry outlives its user).</summary>
    public string Username { get; set; } = "";

    public string? Ip { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>The MCP client's name and version, as it said.</summary>
    public string? Client { get; set; }

    /// <summary><c>tools/call</c>, <c>tools/list</c> or <c>auth</c>.</summary>
    public string Method { get; set; } = "";

    public string? Tool { get; set; }

    /// <summary>The tool's <see cref="Tools.ToolKind"/>, lower case.</summary>
    public string? ToolKind { get; set; }

    /// <summary>One of <see cref="McpOutcomes"/>.</summary>
    public string Outcome { get; set; } = McpOutcomes.Ok;

    public string? ErrorCode { get; set; }

    public string? Error { get; set; }

    /// <summary>One line: "3 items", "created N-12 Kick-off notes".</summary>
    public string Summary { get; set; } = "";

    /// <summary>jsonb: the arguments, redacted.</summary>
    public string? Arguments { get; set; }

    /// <summary>The result's text, gzipped.</summary>
    public byte[]? Result { get; set; }

    public bool Truncated { get; set; }

    /// <summary>The call counted as a strike.</summary>
    public bool Strike { get; set; }

    /// <summary>jsonb: the objects it was about, <c>[{type, id, name}]</c>.</summary>
    public string? Targets { get; set; }

    /// <summary>The request's trace id: the audit entries and change-log rows of the same call carry it.</summary>
    public string CorrelationId { get; set; } = "";

    public int DurationMs { get; set; }
}
