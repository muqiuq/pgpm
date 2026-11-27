using System.Globalization;
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
/// The milestone tools (docs/03 §7.3, docs/01 §11): <c>add_milestone</c>, <c>update_milestone</c>, <c>set_dod_item</c> and
/// <c>record_gate_decision</c> – a project's milestones and gates, their Definition-of-Done criteria (done / n/a with a note)
/// and a gate's decision with the status change it suggests (PGPM.WebUI/src/domain/milestones.ts <c>suggestedStatus</c>).
/// Need edit on the project; the server stamps who decided and when.
/// </summary>
public static class MilestoneTools
{
    public static readonly IReadOnlyList<string> Statuses = ["planned", "in_progress", "done", "cancelled"];
    public static readonly IReadOnlyList<string> Decisions = ["pending", "go", "no_go", "iterate", "pivot", "park"];
    public static readonly IReadOnlyList<string> DodStates = ["open", "done", "na"];

    public static IReadOnlyList<ToolSpec> All =>
    [
        new("add_milestone", "Add a milestone or gate",
            "Adds a milestone or a gate (a decision point with go / no-go / iterate / pivot / park) after a project's last one, with its " +
            "Definition-of-Done criteria; a gate may suggest a project status on go and on no-go.",
            ToolArgs.Schema(
                Text("project", "The project: id or key (P-3)", required: true),
                Choice("kind", "milestone (default) or gate", ["milestone", "gate"]),
                Text("title", "1 to 300 characters", required: true),
                Text("description", "Markdown"),
                Text("dueDate", "yyyy-MM-dd"),
                Texts("dod", "The Definition-of-Done criteria, in order"),
                Text("onGoStatus", "Gates: the project status suggested on go (id or name)"),
                Text("onNoGoStatus", "Gates: the project status suggested on no-go (id or name)")),
            null, Level.None, ToolKind.Write, AddAsync)
        {
            AuditEvents = [AuditEvents.McpItemUpdated],
        },
        new("update_milestone", "Update a milestone or gate",
            "Changes the given fields of a milestone or gate: title, description, due date, status (planned, in_progress, done, cancelled), " +
            "colour, a gate's status suggestions. Setting done doesn't check the DoD: dodOpen in the answer says what is still open. " +
            "With expectedRev a change meanwhile is refused.",
            ToolArgs.Schema(
                Id("id", "The milestone's id (get_item)"),
                Text("title", "1 to 300 characters"),
                Text("description", "Markdown; an empty text removes it"),
                Text("dueDate", "yyyy-MM-dd; an empty text removes it"),
                Choice("status", "planned, in_progress, done or cancelled", Statuses),
                Text("color", "#rrggbb; \"none\" for the project's"),
                Text("onGoStatus", "Gates: the project status suggested on go (id or name; empty: none)"),
                Text("onNoGoStatus", "Gates: the project status suggested on no-go (id or name; empty: none)"),
                Int("expectedRev", "The rev you read (get_item)")),
            null, Level.None, ToolKind.Write, UpdateAsync)
        {
            AuditEvents = [AuditEvents.McpItemUpdated],
            Idempotent = true,
        },
        new("set_dod_item", "Set a DoD criterion",
            "Marks a Definition-of-Done criterion open, done or n/a (n/a needs a note: the evidence why), with a note (a link or a text). Give " +
            "the criterion's id (get_item), or a milestone and the criterion's text – a text the milestone doesn't have yet is added as a new " +
            "criterion.",
            ToolArgs.Schema(
                Id("id", "The criterion's id (get_item)", required: false),
                Id("milestone", "With text: the milestone or gate", required: false),
                Text("text", "With milestone: the criterion (found by its text, else added); with id: a new text"),
                Choice("state", "open, done or na", DodStates),
                Text("note", "The evidence: a link or a text; an empty text removes it")),
            null, Level.None, ToolKind.Write, SetDodAsync)
        {
            AuditEvents = [AuditEvents.McpItemUpdated],
            Idempotent = true,
        },
        new("record_gate_decision", "Record a gate decision",
            "Records a gate's decision – go, no_go, iterate, pivot, park (or pending again) – with a note. A gate suggests a project status on " +
            "go and on no-go: applyStatus: true moves the project there (if its template allows it); status sets another one. The answer " +
            "names the suggestion.",
            ToolArgs.Schema(
                Id("id", "The gate's id (get_item)"),
                Choice("decision", "go, no_go, iterate, pivot, park or pending", Decisions, required: true),
                Text("note", "Why: at most 4,000 characters; an empty text removes it"),
                Bool("applyStatus", "Move the project to the status the decision suggests"),
                Text("status", "Move the project to this status instead (id or name)")),
            null, Level.None, ToolKind.Write, DecideAsync)
        {
            AuditEvents = [AuditEvents.McpItemUpdated],
        },
    ];

    /// <summary>A live milestone of a project the token reads, with the project.</summary>
    public static async Task<(ItemEntity Item, MilestoneEntity Milestone)> FindAsync(ToolContext ctx, PgpmDbContext db, Guid id, CancellationToken ct)
    {
        var milestone = await db.Milestones.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id && !m.Deleted, ct);
        var item = milestone is null ? null
            : await ctx.Access(db).Readable(db.Items.AsNoTracking(), ctx.Viewer).FirstOrDefaultAsync(i => i.Id == milestone.ItemId && !i.Deleted, ct);
        return item is null ? throw ToolException.NotFound("milestone") : (item, milestone!);
    }

    private static Guid? StatusArg(JsonElement args, string name, ToolConfig config) =>
        OptionalText(args, name, 200) is { Length: > 0 } value ? config.ResolveStatus(ItemType.Project, value).Id : null;

    private static async Task<object> AddAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var project = await ItemViews.FindAsync(TextOf(args, "project", 1, 64), ctx, db, ct);
        if (project.Type != ItemType.Project)
        {
            throw ToolException.Invalid("Milestones belong to projects.");
        }
        var config = await ctx.ConfigAsync(db, ct);
        var gate = OptionalChoice(args, "kind", ["milestone", "gate"]) == "gate";
        var title = TextOf(args, "title", 1, 300);
        var onGo = StatusArg(args, "onGoStatus", config);
        var onNoGo = StatusArg(args, "onNoGoStatus", config);
        if (!gate && (onGo is not null || onNoGo is not null))
        {
            throw ToolException.Invalid("Only gates suggest statuses.");
        }
        var due = OptionalDate(args, "dueDate");
        var dod = TextsOf(args, "dod", 100).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        var id = await ctx.WriteAsync(db, async write =>
        {
            await ItemTools.RequireEditableAsync(write, project.Id, ct);
            var keys = await write.Db.Milestones.AsNoTracking().Where(m => m.ItemId == project.Id && !m.Deleted).Select(m => m.SortOrder).ToListAsync(ct);
            var batch = Ids.New();
            var milestone = await ToolWrites.InsertAsync(write, "milestones", new JsonObject
            {
                ["itemId"] = project.Id.ToString(),
                ["kind"] = gate ? "gate" : "milestone",
                ["title"] = title,
                ["description"] = OptionalText(args, "description", FieldValueRules.MaxText, trim: false) is { Length: > 0 } d ? d : null,
                ["dueDate"] = due?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["status"] = "planned",
                ["decision"] = gate ? "pending" : null,
                ["onGoStatusId"] = onGo?.ToString(),
                ["onNoGoStatusId"] = onNoGo?.ToString(),
                ["sortOrder"] = ItemDetails.NextKey(keys),
            }, batch, ct);
            await ItemSetup.AddDodAsync(write, milestone, dod, batch, ct);
            return milestone;
        }, ct);
        return await AnswerAsync(ctx, db, project, id, $"added {(gate ? "the gate" : "the milestone")} {title}", ct);
    }

    private static async Task<object> UpdateAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var (project, found) = await FindAsync(ctx, db, IdOf(args), ct);
        var config = await ctx.ConfigAsync(db, ct);
        var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (Has(args, "title"))
        {
            values["title"] = TextOf(args, "title", 1, 300);
        }
        if (args.TryGetProperty("description", out _))
        {
            values["description"] = OptionalText(args, "description", FieldValueRules.MaxText, trim: false) is { Length: > 0 } d ? d : null;
        }
        if (args.TryGetProperty("dueDate", out _))
        {
            values["dueDate"] = OptionalText(args, "dueDate", 32) is { Length: > 0 }
                ? OptionalDate(args, "dueDate")!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        }
        if (OptionalChoice(args, "status", Statuses) is { } status)
        {
            values["status"] = status;
        }
        if (args.TryGetProperty("color", out _))
        {
            values["color"] = ItemKinds.Color.Parse(args, "color");
        }
        foreach (var (arg, key) in new[] { ("onGoStatus", "onGoStatusId"), ("onNoGoStatus", "onNoGoStatusId") })
        {
            if (args.TryGetProperty(arg, out _))
            {
                if (found.Kind != MilestoneKind.Gate)
                {
                    throw ToolException.Invalid("Only gates suggest statuses.");
                }
                values[key] = StatusArg(args, arg, config)?.ToString();
            }
        }
        if (values.Count == 0)
        {
            throw ToolException.Invalid("Nothing to change: give title, description, dueDate, status, color, onGoStatus or onNoGoStatus.");
        }
        var expected = OptionalInt(args, "expectedRev");
        var changed = await ctx.WriteAsync(db, async write =>
        {
            await ItemTools.RequireEditableAsync(write, project.Id, ct);
            var milestone = await write.Db.Milestones.AsNoTracking().SingleAsync(m => m.Id == found.Id, ct);
            if (milestone.Deleted)
            {
                throw ToolException.NotFound("milestone");
            }
            if (expected is { } rev && rev != milestone.Rev)
            {
                throw new ToolException("changed_meanwhile", $"{milestone.Title} changed since rev {rev}: it is at rev {milestone.Rev} now. Read it again.");
            }
            return await ToolWrites.PatchAsync(write, "milestones", milestone, values, ct);
        }, ct);
        return await AnswerAsync(ctx, db, project, found.Id,
            $"{found.Title}: {(changed.Count == 0 ? "nothing changed" : string.Join(", ", changed))}", ct);
    }

    private static async Task<object> SetDodAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var state = OptionalChoice(args, "state", DodStates);
        var text = OptionalText(args, "text", 500);
        var hasNote = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("note", out _);
        var note = OptionalText(args, "note", 2000, trim: false) is { } n && n.Trim().Length > 0 ? n : null;
        ItemEntity project;
        MilestoneEntity milestone;
        Guid? dodId = OptionalId(args, "id");
        if (dodId is { } id)
        {
            var dod = await db.DodItems.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && !d.Deleted, ct) ?? throw ToolException.NotFound("criterion");
            (project, milestone) = await FindAsync(ctx, db, dod.MilestoneId, ct);
        }
        else if (OptionalId(args, "milestone") is { } milestoneId && text is { Length: > 0 })
        {
            (project, milestone) = await FindAsync(ctx, db, milestoneId, ct);
        }
        else
        {
            throw ToolException.Invalid("Give the criterion's id, or milestone and text.");
        }
        if (state is null && !hasNote && (dodId is null || text is null))
        {
            throw ToolException.Invalid("Nothing to change: give state, note or text.");
        }
        var outcome = await ctx.WriteAsync(db, async write =>
        {
            await ItemTools.RequireEditableAsync(write, project.Id, ct);
            var criteria = await write.Db.DodItems.AsNoTracking().Where(d => d.MilestoneId == milestone.Id && !d.Deleted).ToListAsync(ct);
            var row = dodId is { } given ? criteria.FirstOrDefault(d => d.Id == given) ?? throw ToolException.NotFound("criterion")
                : criteria.FirstOrDefault(d => string.Equals(d.Text.Trim(), text!.Trim(), StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                await ToolWrites.InsertAsync(write, "dod_items", new JsonObject
                {
                    ["milestoneId"] = milestone.Id.ToString(), ["text"] = text, ["state"] = state ?? "open", ["note"] = note,
                    ["sortOrder"] = ItemDetails.NextKey(criteria.Select(d => d.SortOrder)),
                }, Ids.New(), ct);
                return $"added \"{text}\"";
            }
            var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            if (state is not null)
            {
                values["state"] = state;
            }
            if (hasNote)
            {
                values["note"] = note;
            }
            if (dodId is not null && text is { Length: > 0 })
            {
                values["text"] = text;
            }
            var changed = await ToolWrites.PatchAsync(write, "dod_items", row, values, ct);
            return $"\"{row.Text}\": {(changed.Count == 0 ? "nothing changed" : string.Join(", ", changed))}";
        }, ct);
        return await AnswerAsync(ctx, db, project, milestone.Id, $"{milestone.Title}, {outcome}", ct);
    }

    private static async Task<object> DecideAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var (project, gate) = await FindAsync(ctx, db, IdOf(args), ct);
        if (gate.Kind != MilestoneKind.Gate)
        {
            throw ToolException.Invalid($"{gate.Title} is a milestone, not a gate: only gates are decided.");
        }
        var config = await ctx.ConfigAsync(db, ct);
        var decision = OptionalChoice(args, "decision", Decisions) ?? throw ToolException.Invalid("decision is one of " + string.Join(", ", Decisions) + ".");
        var hasNote = args.TryGetProperty("note", out _);
        var note = OptionalText(args, "note", 4000) is { Length: > 0 } n ? n : null;
        var explicitStatus = StatusArg(args, "status", config);
        var apply = OptionalBool(args, "applyStatus") == true;
        var allowed = await config.AllowedAsync(db, project.ProjectTemplateId, ct);
        // The client's suggestedStatus: go → on-go, no-go → on-no-go; a known, live, allowed project status other than the project's.
        var suggestedId = decision == "go" ? gate.OnGoStatusId : decision == "no_go" ? gate.OnNoGoStatusId : null;
        var suggested = suggestedId is { } sid && sid != project.StatusId ? allowed.FirstOrDefault(s => s.Id == sid) : null;
        if (explicitStatus is { } chosen && allowed.All(s => s.Id != chosen))
        {
            throw ToolException.Invalid("The project's template doesn't allow this status.");
        }
        // applyStatus without a suggestion changes nothing: statusChanged says so.
        var target = explicitStatus ?? (apply ? suggested?.Id : null);
        var statusChanged = await ctx.WriteAsync(db, async write =>
        {
            var item = await ItemTools.RequireEditableAsync(write, project.Id, ct);
            var row = await write.Db.Milestones.AsNoTracking().SingleAsync(m => m.Id == gate.Id, ct);
            if (row.Deleted)
            {
                throw ToolException.NotFound("milestone");
            }
            var values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal) { ["decision"] = decision };
            if (hasNote)
            {
                values["decisionNote"] = note;
            }
            await ToolWrites.PatchAsync(write, "milestones", row, values, ct);
            return target is { } status && status != item.StatusId
                && (await ToolWrites.PatchAsync(write, "items", item, new Dictionary<string, JsonNode?> { ["statusId"] = status.ToString() }, ct)).Count > 0;
        }, ct);
        var detail = await AnswerAsync(ctx, db, project, gate.Id,
            $"{gate.Title}: {decision}" + (statusChanged ? $", status {config.Status(target)?.Name}" : ""), ct);
        detail["suggestedStatus"] = suggested is null ? null : new JsonObject { ["id"] = suggested.Id.ToString(), ["name"] = suggested.Name };
        detail["statusChanged"] = statusChanged;
        return detail;
    }

    private static async Task<JsonObject> AnswerAsync(ToolContext ctx, PgpmDbContext db, ItemEntity project, Guid milestoneId, string what, CancellationToken ct)
    {
        var key = ItemViews.KeyOf(project);
        ctx.Summary = $"{key} {project.Title}: {what}";
        ctx.Target("item", project.Id, project.Title);
        AuditLog.Target(AuditTarget.Item(project.Id, project.Title));
        AuditLog.Summary($"{key} {project.Title}: {what}");
        var milestone = (await ItemDetails.MilestonesAsync(db, ctx, project.Id, milestoneId, ct)).FirstOrDefault()?.AsObject().DeepClone().AsObject()
            ?? new JsonObject { ["id"] = milestoneId.ToString() };
        var open = milestone["dod"] is JsonArray dod ? dod.Count(d => d?["state"]?.GetValue<string>() == "open") : 0;
        var project1 = await db.Items.AsNoTracking().SingleAsync(i => i.Id == project.Id, ct);
        var config = await ctx.ConfigAsync(db, ct);
        return new JsonObject
        {
            ["project"] = await ItemDetails.RefAsync(project.Id, ctx, db, ct),
            ["projectStatus"] = config.Status(project1.StatusId)?.Name,
            ["milestone"] = milestone,
            ["dodOpen"] = open,
        };
    }
}
