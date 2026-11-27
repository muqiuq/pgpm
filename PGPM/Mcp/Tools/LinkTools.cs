using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Audit;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using static PGPM.Mcp.Tools.ToolArgs;

namespace PGPM.Mcp.Tools;

/// <summary>
/// <c>link_items</c> and <c>unlink_items</c> (docs/03 §7.3, docs/01 §4.7): a typed link belongs to its source item (edit to add
/// or remove it), the target must be readable; the same link twice is one.
/// </summary>
public static class LinkTools
{
    public static readonly IReadOnlyList<string> Relations = ["contributes_to", "learning_of", "depends_on", "related"];

    public static IReadOnlyList<ToolSpec> All =>
    [
        new("link_items", "Link two items",
            "Links an item you may edit to another you may see: contributes_to (an idea to a project), learning_of (a note to a project or " +
            "idea), depends_on, or related (the default). The link belongs to the first item.",
            ToolArgs.Schema(
                Text("from", "The item the link belongs to: id or key", required: true),
                Text("to", "The item it points to: id or key", required: true),
                Choice("relation", "contributes_to, learning_of, depends_on or related (default)", Relations)),
            null, Level.None, ToolKind.Write, LinkAsync)
        {
            AuditEvents = [AuditEvents.McpItemUpdated],
            Idempotent = true,
        },
        new("unlink_items", "Unlink two items",
            "Removes the links from an item you may edit to another: of the given relation, or all of them.",
            ToolArgs.Schema(
                Text("from", "The item the link belongs to: id or key", required: true),
                Text("to", "The item it points to: id or key", required: true),
                Choice("relation", "Only this relation; all when left out", Relations)),
            null, Level.None, ToolKind.Write, UnlinkAsync)
        {
            AuditEvents = [AuditEvents.McpItemUpdated],
            Idempotent = true,
        },
    ];

    private static async Task<object> LinkAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var from = await ItemViews.FindAsync(TextOf(args, "from", 1, 64), ctx, db, ct);
        var to = await ItemViews.FindAsync(TextOf(args, "to", 1, 64), ctx, db, ct);
        if (from.Id == to.Id)
        {
            throw ToolException.Invalid("An item doesn't link to itself.");
        }
        var relation = OptionalChoice(args, "relation", Relations) ?? "related";
        var kind = Data.EnumText.Parse<LinkRelation>(relation);
        var outcome = await ctx.WriteAsync(db, async write =>
        {
            await ItemTools.RequireEditableAsync(write, from.Id, ct);
            var existing = await write.Db.ItemLinks.AsNoTracking().AnyAsync(l => l.FromItemId == from.Id && l.ToItemId == to.Id && !l.Deleted
                && l.Relation == kind, ct);
            if (existing)
            {
                return "unchanged";
            }
            await ToolWrites.InsertAsync(write, "item_links", new JsonObject
            {
                ["fromItemId"] = from.Id.ToString(), ["toItemId"] = to.Id.ToString(), ["relation"] = relation,
            }, Ids.New(), ct);
            return "linked";
        }, ct);
        return await AnswerAsync(ctx, db, from, to, $"{outcome} ({relation})", ct);
    }

    private static async Task<object> UnlinkAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var from = await ItemViews.FindAsync(TextOf(args, "from", 1, 64), ctx, db, ct);
        var to = await ItemViews.FindAsync(TextOf(args, "to", 1, 64), ctx, db, ct);
        var relation = OptionalChoice(args, "relation", Relations) is { } r ? Data.EnumText.Parse<LinkRelation>(r) : (LinkRelation?)null;
        var removed = await ctx.WriteAsync(db, async write =>
        {
            await ItemTools.RequireEditableAsync(write, from.Id, ct);
            var links = await write.Db.ItemLinks.AsNoTracking()
                .Where(l => l.FromItemId == from.Id && l.ToItemId == to.Id && !l.Deleted && (relation == null || l.Relation == relation)).ToListAsync(ct);
            foreach (var link in links)
            {
                await ToolWrites.DeleteAsync(write, "item_links", link, ct);
            }
            return links.Count;
        }, ct);
        return await AnswerAsync(ctx, db, from, to, removed == 0 ? "unchanged" : $"{removed} removed", ct);
    }

    private static async Task<object> AnswerAsync(ToolContext ctx, PgpmDbContext db, ItemEntity from, ItemEntity to, string outcome, CancellationToken ct)
    {
        var text = $"{ItemViews.KeyOf(from)} → {ItemViews.KeyOf(to)}: {outcome}";
        ctx.Summary = text;
        ctx.Target("item", from.Id, from.Title);
        ctx.Target("item", to.Id, to.Title);
        AuditLog.Target(AuditTarget.Item(from.Id, from.Title));
        AuditLog.Summary($"{ItemViews.KeyOf(from)} {from.Title}: link to {ItemViews.KeyOf(to)} {outcome}");
        return new { outcome, from = await ItemDetails.RefAsync(from.Id, ctx, db, ct), links = await ItemDetails.Links.Read(from, ctx, db, ct) };
    }
}
