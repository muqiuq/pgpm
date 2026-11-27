using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Audit;
using PGPM.Auth.Rights;
using PGPM.Config;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using static PGPM.Mcp.Tools.ToolArgs;

namespace PGPM.Mcp.Tools;

/// <summary>
/// <c>assign_person</c> and <c>unassign_person</c> (docs/03 §7.3): a person on an item or on a milestone of a project, with
/// a role, once each (assigning again changes the role), as the browser's assignment lists do (PGPM.WebUI/src/local/items.ts
/// <c>assignPerson</c>, milestones.ts <c>assignToMilestone</c>: a gate's persons decide – role <i>Review board</i> unless
/// another is given). Needs edit on the item.
/// </summary>
public static class PersonTools
{
    public const string GateRole = "Review board";

    private static readonly Field[] TargetArgs =
    [
        Text("item", ItemTools.ItemArg + "; or give milestone"),
        Id("milestone", "A milestone or gate of a project (get_item lists them), instead of item", required: false),
        Text("person", "The person: an id (search_persons), an e-mail, or \"me\"", required: true),
    ];

    public static IReadOnlyList<ToolSpec> All =>
    [
        new("assign_person", "Assign a person",
            "Assigns a person to an item or to a milestone or gate, with a role (Owner, Contributor, …; a gate's persons decide, role Review board " +
            "when none is given). A person is assigned once: assigning again changes the role.",
            ToolArgs.Schema([.. TargetArgs, Text("role", "The role, 1 to 100 characters")]),
            null, Level.None, ToolKind.Write, AssignAsync)
        {
            AuditEvents = [AuditEvents.McpItemUpdated],
            Idempotent = true,
        },
        new("unassign_person", "Unassign a person",
            "Removes a person's assignment from an item or from a milestone or gate.",
            ToolArgs.Schema(TargetArgs), null, Level.None, ToolKind.Write, UnassignAsync)
        {
            AuditEvents = [AuditEvents.McpItemUpdated],
            Idempotent = true,
        },
    ];

    /// <summary>The item, or the milestone and its project, the arguments name (readable).</summary>
    private static async Task<(ItemEntity Item, MilestoneEntity? Milestone)> TargetAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        if (OptionalId(args, "milestone") is { } milestoneId)
        {
            if (Has(args, "item"))
            {
                throw ToolException.Invalid("Give item or milestone, not both.");
            }
            return await MilestoneTools.FindAsync(ctx, db, milestoneId, ct);
        }
        return (await ItemViews.FindAsync(OptionalText(args, "item", 64) ?? throw ToolException.Invalid("Give item or milestone."), ctx, db, ct), null);
    }

    private static async Task<object> AssignAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var (item, milestone) = await TargetAsync(args, ctx, db, ct);
        var personId = await ToolConfig.PersonIdAsync(db, ctx, TextOf(args, "person", 1, 200), ct);
        var given = OptionalText(args, "role", 100);
        var role = given is { Length: > 0 } ? given : given is null && milestone?.Kind == MilestoneKind.Gate ? GateRole : null;
        var outcome = await ctx.WriteAsync(db, async write =>
        {
            await ItemTools.RequireEditableAsync(write, item.Id, ct);
            var list = await write.Db.Assignments.AsNoTracking()
                .Where(a => !a.Deleted && (milestone == null ? a.ItemId == item.Id : a.MilestoneId == milestone.Id)).ToListAsync(ct);
            if (list.FirstOrDefault(a => a.PersonId == personId) is { } existing)
            {
                if (role is null || existing.Role == role)
                {
                    return "unchanged";
                }
                await ToolWrites.PatchAsync(write, "assignments", existing, new Dictionary<string, JsonNode?> { ["role"] = role }, ct);
                return "updated";
            }
            await ToolWrites.InsertAsync(write, "assignments", new JsonObject
            {
                ["itemId"] = milestone is null ? item.Id.ToString() : null,
                ["milestoneId"] = milestone?.Id.ToString(),
                ["personId"] = personId.ToString(),
                ["role"] = role,
                ["sortOrder"] = ItemDetails.NextKey(list.Select(a => a.SortOrder)),
            }, Ids.New(), ct);
            return "added";
        }, ct);
        return await AnswerAsync(ctx, db, item, milestone, personId, outcome, ct);
    }

    private static async Task<object> UnassignAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var (item, milestone) = await TargetAsync(args, ctx, db, ct);
        var personId = await ToolConfig.PersonIdAsync(db, ctx, TextOf(args, "person", 1, 200), ct);
        var outcome = await ctx.WriteAsync(db, async write =>
        {
            await ItemTools.RequireEditableAsync(write, item.Id, ct);
            var existing = await write.Db.Assignments.AsNoTracking().FirstOrDefaultAsync(a => !a.Deleted && a.PersonId == personId
                && (milestone == null ? a.ItemId == item.Id : a.MilestoneId == milestone.Id), ct);
            if (existing is null)
            {
                return "unchanged";
            }
            await ToolWrites.DeleteAsync(write, "assignments", existing, ct);
            return "removed";
        }, ct);
        return await AnswerAsync(ctx, db, item, milestone, personId, outcome, ct);
    }

    private static async Task<object> AnswerAsync(ToolContext ctx, PgpmDbContext db, ItemEntity item, MilestoneEntity? milestone, Guid personId, string outcome,
        CancellationToken ct)
    {
        var person = (await ToolConfig.PersonNamesAsync(db, [personId], ct)).GetValueOrDefault(personId);
        var key = ItemViews.KeyOf(item);
        var on = milestone is null ? $"{key} {item.Title}" : $"{milestone.Title} of {key}";
        ctx.Summary = $"{person}: {outcome} on {on}";
        ctx.Target("item", item.Id, item.Title);
        AuditLog.Target(AuditTarget.Item(item.Id, item.Title));
        AuditLog.Summary($"{key} {item.Title}: {person} {outcome}{(milestone is null ? "" : $" on {milestone.Title}")}");
        return new
        {
            outcome,
            person = new { id = personId, name = person },
            item = await ItemDetails.RefAsync(item.Id, ctx, db, ct),
            milestone = milestone is null ? null : new { id = milestone.Id, title = milestone.Title },
            persons = milestone is null
                ? await ItemDetails.Persons.Read(item, ctx, db, ct)
                : (await ItemDetails.MilestonesAsync(db, ctx, item.Id, milestone.Id, ct)).FirstOrDefault()?["persons"]?.DeepClone(),
        };
    }
}
