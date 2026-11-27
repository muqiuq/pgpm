using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PGPM.Api.V1.Contracts;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Lib.Helper;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Mcp.Tools;

/// <summary>An MCP call's caller: the token's user and the token, where PGPM's pages are, and the request's correlation id.</summary>
public sealed record McpCallInfo(Guid UserId, string Username, Guid TokenId, McpTokenKind Kind, string BaseUrl, string CorrelationId);

/// <summary>
/// What a tool runs with (docs/03 §7.2–7.3): the request's services, the token's <see cref="Viewer"/> (its user, groups and
/// capped rights, its access capped at read or edit), the call, and what the MCP log records about it – a summary and the
/// objects it was about. Writes go through <see cref="WriteAsync{T}"/>: one <see cref="SyncWriter"/> unit with the source
/// <c>mcp</c>, so a tool's change is validated, stamped and logged as a client's and reaches every connected client live.
/// </summary>
public sealed class ToolContext(IServiceProvider services, McpCallInfo call, Viewer viewer)
{
    public IServiceProvider Services { get; } = services;

    public McpCallInfo Call { get; } = call;

    public Viewer Viewer { get; } = viewer;

    public UserRights Rights => Viewer.Rights;

    public AccessService Access(PgpmDbContext db) => new(db);

    private ToolConfig? config;

    /// <summary>The configuration as the user sees it (statuses, visible sections and fields), read once per call.</summary>
    public async Task<ToolConfig> ConfigAsync(PgpmDbContext db, CancellationToken ct) => config ??= await ToolConfig.LoadAsync(this, db, ct);

    /// <summary>The objects a call was about, for the MCP log.</summary>
    public List<(string Type, string Id, string? Name)> Targets { get; } = [];

    /// <summary>One line for the MCP log (and the audit entry of a change): "3 items", "created N-12 Kick-off".</summary>
    public string? Summary { get; set; }

    public void Target(string type, object id, string? name) => Targets.Add((type, id.ToString()!, name));

    /// <summary>A link to a page of PGPM, for the assistant to hand to the person.</summary>
    public string Link(string path) => $"{Call.BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    /// <summary>One write unit (docs/03 §11.5): the counter's lock, the mutations, the commit; rolled back on any failure.</summary>
    public async Task<T> WriteAsync<T>(PgpmDbContext db, Func<SyncWrite, Task<T>> work, CancellationToken ct)
    {
        await using var write = await new SyncWriter(db, Services).BeginAsync(Viewer, ChangeSource.Mcp, Call.CorrelationId, ct);
        using var _ = ChangeContext.Use(write.Context());
        var result = await work(write);
        await write.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// One mutation through the write unit, as a client's outbox entry: accepted (or partial) goes on; a refusal is the
    /// tool's error with the table's code and message (<c>missing_access</c>, <c>missing_right</c>, <c>validation_failed</c>,
    /// …), a conflict <c>changed_meanwhile</c>.
    /// </summary>
    public static async Task<SyncMutationResult> ApplyAsync(SyncWrite write, SyncMutationIn mutation, CancellationToken ct)
    {
        mutation.MutationId = mutation.MutationId == Guid.Empty ? Ids.New() : mutation.MutationId;
        mutation.ClientChangedAt ??= Clock.UtcNow;
        var result = await write.ApplyAsync(mutation, null, ct);
        return result.Status switch
        {
            SyncStatus.Accepted or SyncStatus.Partial => result,
            SyncStatus.Conflict => throw new ToolException("changed_meanwhile", "Someone changed it meanwhile: read it again and decide.",
                new Dictionary<string, object?> { ["fields"] = result.ConflictFields }),
            _ => throw new ToolException(result.Code ?? "rejected",
                result.Errors is { Count: > 0 } errors
                    ? $"{result.Message} {string.Join(" ", errors.Select(e => $"{e.Key}: {string.Join(" ", e.Value)}"))}"
                    : result.Message ?? "Refused."),
        };
    }
}

/// <summary>
/// A list's page (PNMTD's Paging): <c>limit</c> (default 20, at most 100) and an opaque <c>cursor</c> holding the position.
/// </summary>
public readonly record struct ToolPaging(int Limit, int Offset)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public static ToolPaging From(JsonElement args, int defaultLimit = DefaultLimit)
    {
        var limit = Math.Clamp(ToolArgs.IntOf(args, "limit", defaultLimit), 1, MaxLimit);
        var offset = 0;
        if (ToolArgs.OptionalText(args, "cursor", 64) is { Length: > 0 } cursor)
        {
            try
            {
                var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
                offset = text.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(text[2..], out var o) && o >= 0 ? o
                    : throw ToolException.Invalid("cursor isn't one of PGPM's.");
            }
            catch (FormatException)
            {
                throw ToolException.Invalid("cursor isn't one of PGPM's.");
            }
        }
        return new ToolPaging(limit, offset);
    }

    public static string Cursor(int offset) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"o:{offset}"));

    /// <summary>One more than the page is read, so that the page knows whether another follows.</summary>
    public async Task<ToolPage<T>> ReadAsync<T>(IQueryable<T> query, CancellationToken ct)
    {
        var rows = await query.Skip(Offset).Take(Limit + 1).ToListAsync(ct);
        var total = await query.CountAsync(ct);
        var more = rows.Count > Limit;
        return new ToolPage<T>([.. rows.Take(Limit)], more ? Cursor(Offset + Limit) : null, total);
    }
}

/// <summary>A page of a list.</summary>
public sealed record ToolPage<T>(IReadOnlyList<T> Items, string? NextCursor, int? Total)
{
    public ToolPage<TOut> Select<TOut>(Func<T, TOut> map) => new([.. Items.Select(map)], NextCursor, Total);
}
