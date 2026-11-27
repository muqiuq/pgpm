using System.Text.Json.Nodes;
using PGPM.Auth.Rights;
using PGPM.Items;
using PGPM.Mcp;
using PGPM.Mcp.Tools;

namespace PGPM.Api.V1.Contracts;

/// <summary>
/// One of the user's two MCP tokens (docs/03 §7.1). <see cref="State"/>: <c>on</c>, <c>off</c>, <c>no_value</c>,
/// <c>expired</c>, <c>blocked</c>, <c>stamp</c>, <c>inactive</c>, <c>totp</c>, <c>no_right</c>, <c>mcp_off</c>.
/// <see cref="Until"/>: when it goes off by itself. <see cref="EndedWhy"/>: <c>off</c>, <c>expired</c>, <c>regenerated</c>,
/// <c>stamp</c>, <c>strikes</c>, <c>admin</c>, <c>user_inactive</c>, <c>totp_off</c>. Never the value: that is shown once.
/// </summary>
public sealed record McpTokenItem(
    McpTokenKind Kind,
    bool On,
    string State,
    DateTime? Until,
    bool HasValue,
    string? Hint,
    DateTime? IssuedAt,
    DateTime? LastUsedAt,
    string? LastUsedFrom,
    string? LastClient,
    DateTime? EndedAt,
    string? EndedWhy,
    string? EndedNote,
    DateTime? BlockedAt,
    int Strikes,
    IReadOnlyDictionary<Area, Level> Rights,
    ItemAccess Access);

/// <summary><c>GET /account/mcp</c>: the settings that apply, the address to connect to, and both tokens.</summary>
public sealed record McpAccountItem(
    bool Enabled,
    string Url,
    int WriteHours,
    int ReadDaysAfterOnline,
    bool WriteRequiresTotp,
    bool HasSecondFactor,
    bool CanUse,
    McpTokenItem Read,
    McpTokenItem Write);

/// <summary>After switching a token on or regenerating it: the token, and its value if one was made now. Shown once.</summary>
public sealed record McpTokenIssued(McpTokenItem Token, string? Value);

/// <summary>A tool as Administration → MCP → Tools shows it, from the catalog itself.</summary>
public sealed record McpToolDoc(
    string Name,
    string Title,
    string Description,
    ToolKind Kind,
    string Right,
    bool ReadToken,
    bool Destructive,
    JsonObject Parameters,
    bool Available);

/// <summary>The most a token gets per area: the write token's ceiling, the read token's cap.</summary>
public sealed record McpCeilingRow(Area Area, string Label, Level? Write, Level? Read);

/// <summary><c>GET /mcp/catalog</c>: the rules and the tools, with this installation's values.</summary>
public sealed record McpCatalog(
    bool Enabled,
    string Url,
    string Instructions,
    int WriteHours,
    int ReadDaysAfterOnline,
    bool WriteRequiresTotp,
    int CallsPerMinute,
    int WritesPerMinute,
    int MaxResultChars,
    int StrikesToBlock,
    int LogEntries,
    IReadOnlyList<McpCeilingRow> Ceiling,
    IReadOnlyList<McpToolDoc> Tools);

/// <summary>An entry of the MCP log, as the list shows it. <see cref="Outcome"/>: <c>ok</c>, <c>error</c>, <c>denied</c>, <c>rate_limited</c>, <c>unauthenticated</c>.</summary>
public sealed record McpLogItem(
    long Id,
    DateTime At,
    Guid? TokenId,
    McpTokenKind? Kind,
    Guid? UserId,
    string Username,
    string? Ip,
    string? Client,
    string Method,
    string? Tool,
    string? ToolKind,
    string Outcome,
    string? ErrorCode,
    string Summary,
    bool Strike,
    int DurationMs);

/// <summary>One entry with everything: arguments, result, and what changed – the audit entries and History rows of the same call.</summary>
public sealed record McpLogDetail(
    McpLogItem Item,
    string CorrelationId,
    string? UserAgent,
    string? Arguments,
    string? Result,
    bool Truncated,
    string? Error,
    string? Targets,
    IReadOnlyList<AuditEntryItem> Audit,
    IReadOnlyList<HistoryEntryItem> History);

/// <summary><c>GET /mcp/log</c>: newest first. Filters: a user, a token, the kind, a tool, the outcome, a time range, text in the summary.</summary>
public sealed class McpLogQuery : PageQuery
{
    public Guid? UserId { get; set; }

    public Guid? TokenId { get; set; }

    public McpTokenKind? Kind { get; set; }

    public string? Tool { get; set; }

    public string? Outcome { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }
}

/// <summary>A user's token on Administration → MCP → Tokens: never the value.</summary>
public sealed record McpAdminTokenItem(Guid Id, Guid UserId, string Username, string DisplayName, McpTokenItem Token);

/// <summary><c>POST /mcp/tokens/{id}/off</c>: why, shown to the token's user and in the audit (3 to 300 characters).</summary>
public sealed record McpTurnOffRequest(string? Reason);

/// <summary><c>POST /mcp/tokens/revoke-all</c>: why, for the audit and the users.</summary>
public sealed record McpRevokeAllRequest(string? Reason);

public sealed record McpRevokeAllResult(int Revoked);

/// <summary>A count by name: a tool, a user, an outcome.</summary>
public sealed record McpCount(string Name, int Count);

/// <summary>A day's calls: how many, and how many failed or were refused.</summary>
public sealed record McpDay(DateOnly Date, int Calls, int Failed);

/// <summary><c>GET /mcp/status</c>: the tokens that are on, blocked or have a value, and the calls of the last day and week.</summary>
public sealed record McpStatus(
    bool Enabled,
    int ReadOn,
    int WriteOn,
    int Blocked,
    int WithValue,
    int Calls24h,
    int Calls7d,
    int Strikes24h,
    DateTime? LastCallAt,
    IReadOnlyList<McpCount> Outcomes24h,
    IReadOnlyList<McpCount> Tools7d,
    IReadOnlyList<McpCount> Users7d,
    IReadOnlyList<McpDay> Days);
