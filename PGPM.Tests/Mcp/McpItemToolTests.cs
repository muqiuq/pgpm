using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Audit;
using PGPM.Config;
using PGPM.Items;
using PGPM.Mcp;
using PGPM.Sync;
using PGPM.Tests.Content;
using PGPM.Tests.Sync;
using static PGPM.Tests.Mcp.McpKit;
using static PGPM.Tests.Mcp.McpToolKit;

namespace PGPM.Tests.Mcp;

/// <summary>
/// The item tools beyond notes (docs/03 §7.3): ideas, leads and projects with their statuses and fields; configured field
/// values as the user sees them – hidden sections never read nor written, sections they may not edit refused, scores computed
/// –; persons, links, milestones, DoD criteria and gate decisions. Every write goes through the SyncWriter (source mcp).
/// </summary>
[TestClass]
public sealed class McpItemToolTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static JsonObject Rating(int max = 5, bool na = true) => new() { ["min"] = 0, ["max"] = max, ["allowNa"] = na };

    [TestMethod]
    public async Task IdeasLeadsAndProjectsHaveTheirStatusesAndFields()
    {
        var group = await SyncKit.GroupAsync(app, "mcp-kinds");
        var (user, read, write) = await WriterAsync(app, "kinds", group);
        var ideaOpen = await ContentKit.StatusAsync(app, ItemType.Idea);
        var leadNew = await ContentKit.StatusAsync(app, ItemType.Lead);
        var leadWon = await ContentKit.StatusAsync(app, ItemType.Lead, StatusCategory.Closed);
        await using var db = app.NewDb();
        var leadWonName = await db.Statuses.Where(s => s.Id == leadWon).Select(s => s.Name).SingleAsync();
        await using var client = await ClientAsync(app, write);

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "create_idea", "create_lead", "create_project", "assign_person", "unassign_person", "link_items", "unlink_items",
            "add_milestone", "update_milestone", "set_dod_item", "record_gate_decision", "list_maps", "get_map", "add_item_to_map", "convert_to_project" }, names);

        var lead = await OkAsync(client, "create_lead", new
        {
            title = "Website relaunch", status = leadNew.ToString(), organisation = "ACME", value = 125000.5, currency = "chf", probability = 40,
            decisionDate = "2027-03-31", nextAction = "Send the offer", nextActionDue = "2027-01-15", source = "Fair",
            share_with = new[] { new { group = group.ToString() } },
        });
        Assert.AreEqual(("lead", "ACME", "CHF", 40), (lead.S("type"), lead.S("organisation"), lead.S("currency"), lead.GetProperty("probability").GetInt32()));
        Assert.AreEqual(125000.5m, lead.GetProperty("value").GetDecimal());
        Assert.AreEqual(leadNew.ToString(), lead.GetProperty("status").S("id"));
        StringAssert.Matches(lead.S("key"), new System.Text.RegularExpressions.Regex("^L-[0-9]+$"));
        var leadId = Guid.Parse(lead.S("id")!);
        var row = await db.Items.AsNoTracking().SingleAsync(i => i.Id == leadId);
        Assert.AreEqual((ItemType.Lead, new DateOnly(2027, 1, 15), "Fair"), (row.Type, row.LeadNextActionDue!.Value, row.LeadSource));

        var updated = await OkAsync(client, "update_item", new { id = lead.S("key"), status = leadWonName.ToUpperInvariant(), probability = 100, organisation = "" });
        CollectionAssert.AreEquivalent(new[] { "status", "leadProbability", "leadOrganisation" },
            updated.Each("changed").Select(c => c.GetString()).ToList());
        Assert.AreEqual(leadWon.ToString(), updated.GetProperty("status").S("id"));
        Assert.AreEqual("invalid_argument", (await ErrorAsync(client, "update_item", new { id = lead.S("id"), probability = 120 })).Code());
        Assert.AreEqual("invalid_argument", (await ErrorAsync(client, "update_item", new { id = lead.S("id"), status = "No such status" })).Code());
        Assert.AreEqual("invalid_argument", (await ErrorAsync(client, "update_item", new { id = lead.S("id"), pinned = true })).Code(), "leads aren't pinned");

        var idea = await OkAsync(client, "create_idea", new { title = "AI triage", status = ideaOpen.ToString(), description = "Sort the tickets" });
        Assert.AreEqual(("idea", ideaOpen.ToString()), (idea.S("type"), idea.GetProperty("status").S("id")));
        Assert.AreEqual(0, idea.GetProperty("persons").GetArrayLength());
        Assert.AreEqual(JsonValueKind.Array, idea.GetProperty("sections").ValueKind, "ideas have sections");

        await using var reader = await ClientAsync(app, read);
        var found = await OkAsync(reader, "search_items", new { type = "lead", status = leadWonName });
        Assert.AreEqual(leadId.ToString(), found.Each("items").Single().S("id"));
        var config = await OkAsync(reader, "list_config", new { include = new[] { "statuses", "itemTypes" } });
        Assert.IsTrue(config.GetProperty("statuses").GetProperty("lead").EnumerateArray().Any(s => s.S("id") == leadWon.ToString()));
        Assert.IsFalse(config.TryGetProperty("sections", out _), "only what include names");
        Assert.AreEqual(4, config.GetProperty("itemTypes").GetArrayLength());
        Assert.AreEqual("invalid_argument", (await ErrorAsync(reader, "list_config", new { include = new[] { "secrets" } })).Code());

        Assert.IsTrue(await db.ChangeLog.AnyAsync(c => c.ItemId == leadId && c.Source == ChangeSource.Mcp && c.Action == "update" && c.UserId == user));
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.Event == AuditEvents.McpItemCreated && l.SubjectId == leadId));
    }

    [TestMethod]
    public async Task FieldValuesFollowSectionVisibilityAndEditRights()
    {
        var mine = await SyncKit.GroupAsync(app, "mcp-fields");
        var board = await SyncKit.GroupAsync(app, "mcp-board");
        var (user, read, write) = await WriterAsync(app, "assessor", mine);
        var status = await ContentKit.StatusAsync(app, ItemType.Idea);
        var open = await ContentKit.SectionAsync(app, ["idea", "project"]);
        var (_, rating) = await ContentKit.FieldAsync(app, open, FieldType.Rating, Rating());
        var (_, speed) = await ContentKit.FieldAsync(app, open, FieldType.Select, new JsonObject
        {
            ["options"] = new JsonArray(new JsonObject { ["id"] = "lt3", ["label"] = "< 3 months", ["points"] = 5 },
                new JsonObject { ["id"] = "gt6", ["label"] = "> 6 months", ["points"] = 1 }),
        });
        var (_, effort) = await ContentKit.FieldAsync(app, open, FieldType.Number, new JsonObject { ["min"] = 0, ["max"] = 1000 });
        var hidden = await ContentKit.SectionAsync(app, ["idea"], (board, SectionAccess.View));
        var (secretId, secret) = await ContentKit.FieldAsync(app, hidden, FieldType.Rating, Rating());
        var (_, score) = await ContentKit.FieldAsync(app, open, FieldType.Score, new JsonObject
        {
            ["op"] = "sum", ["inputs"] = new JsonArray(rating, speed), ["max"] = 10, ["na"] = "rescale",
        });
        var (_, leaky) = await ContentKit.FieldAsync(app, open, FieldType.Score, new JsonObject
        {
            ["op"] = "sum", ["inputs"] = new JsonArray(rating, secret), ["max"] = 10,
        });
        var decision = await ContentKit.SectionAsync(app, ["idea"], (board, SectionAccess.Edit));
        var (decisionId, verdict) = await ContentKit.FieldAsync(app, decision, FieldType.Text);
        var label = $"Time to value {ContentKit.Tag()}";
        await using (var config0 = app.NewDb())
        {
            await config0.FieldDefinitions.Where(f => f.Key == speed).ExecuteUpdateAsync(f => f.SetProperty(x => x.Label, label));
        }
        var idea = await ItemAsync(app, user, "idea", "Assessed idea", status);
        var other = await ItemAsync(app, user, "idea", "Not assessed", status);
        await AdminSyncAsync(app, Value(idea, secretId, 5), Value(idea, decisionId, "Go ahead"));
        await using var client = await ClientAsync(app, write);

        var set = await OkAsync(client, "update_item", new
        {
            id = idea.ToString(), fields = new Dictionary<string, object> { [rating] = 4, [label.ToUpperInvariant()] = "< 3 MONTHS", [effort] = 12.5 },
        });
        CollectionAssert.AreEquivalent(new[] { rating, speed, effort }, set.Each("changed").Select(c => c.GetString()).ToList());
        Assert.AreEqual(4, FieldOf(set, rating)!.Value.GetProperty("value").GetInt32());
        Assert.AreEqual(("lt3", "< 3 months"), (FieldOf(set, speed)!.Value.S("value"), FieldOf(set, speed)!.Value.S("text")));
        var computed = FieldOf(set, score)!.Value;
        Assert.AreEqual((9m, 10m), (computed.GetProperty("value").GetDecimal(), computed.GetProperty("max").GetDecimal()), "4 + the option's 5 points");
        Assert.IsTrue(FieldOf(set, leaky)!.Value.Missing("value"), "an input the user can't see: no score");
        Assert.IsNull(FieldOf(set, secret), "a hidden section's value never comes back");
        Assert.IsFalse(set.GetRawText().Contains(secret, StringComparison.Ordinal));
        Assert.AreEqual("Go ahead", FieldOf(set, verdict)!.Value.S("value"), "visible, but not editable");
        var sections = set.Each("sections").ToList();
        Assert.IsTrue(sections.Single(s => s.S("id") == open.ToString()).GetProperty("canEdit").GetBoolean());
        Assert.IsTrue(sections.Single(s => s.S("id") == open.ToString()).GetProperty("onItem").GetBoolean(), "the section came with its values");
        Assert.IsFalse(sections.Single(s => s.S("id") == decision.ToString()).GetProperty("canEdit").GetBoolean());

        Assert.AreEqual("not_found", (await ErrorAsync(client, "update_item", new { id = idea.ToString(), fields = new Dictionary<string, object> { [secret] = 1 } })).Code());
        Assert.AreEqual("missing_access", (await ErrorAsync(client, "update_item",
            new { id = idea.ToString(), fields = new Dictionary<string, object> { [verdict] = "No" } })).Code());
        Assert.AreEqual("invalid_argument", (await ErrorAsync(client, "update_item",
            new { id = idea.ToString(), fields = new Dictionary<string, object> { [score] = 3 } })).Code());
        Assert.AreEqual("invalid_argument", (await ErrorAsync(client, "update_item",
            new { id = idea.ToString(), fields = new Dictionary<string, object> { [rating] = 9 } })).Code());
        Assert.AreEqual("invalid_argument", (await ErrorAsync(client, "update_item",
            new { id = idea.ToString(), fields = new Dictionary<string, object> { [speed] = "tomorrow" } })).Code());
        var na = await OkAsync(client, "update_item", new { id = idea.ToString(), fields = new Dictionary<string, object?> { [rating] = "n/a", [effort] = null } });
        Assert.AreEqual("na", FieldOf(na, rating)!.Value.S("value"));
        Assert.IsTrue(FieldOf(na, effort)!.Value.Missing("value"), "null removes a value");
        await OkAsync(client, "update_item", new { id = idea.ToString(), fields = new Dictionary<string, object> { [rating] = 5 } });

        await using var db = app.NewDb();
        Assert.AreEqual("\"Go ahead\"", (await db.FieldValues.SingleAsync(v => v.ItemId == idea && v.FieldId == decisionId)).Value, "refused writes changed nothing");
        Assert.IsTrue(await db.ItemSections.AnyAsync(s => s.ItemId == idea && s.SectionId == open && !s.Deleted));
        Assert.IsTrue(await db.ChangeLog.AnyAsync(c => c.ItemId == idea && c.Entity == "field_values" && c.Source == ChangeSource.Mcp));

        await using var reader = await ClientAsync(app, read);
        var ranged = await OkAsync(reader, "search_items", new { type = "idea", fields = new[] { new { field = score, min = 8 } } });
        Assert.AreEqual(idea.ToString(), ranged.Each("items").Single().S("id"));
        Assert.AreEqual(10m, ranged.Each("items").Single().GetProperty("values").GetProperty(score).GetDecimal());
        var notHigh = await OkAsync(reader, "search_items", new { type = "idea", fields = new[] { new { field = score, max = 2 } } });
        CollectionAssert.DoesNotContain(notHigh.Each("items").Select(i => i.S("id")).ToList(), other.ToString(), "no value is no number");
        Assert.AreEqual("not_found", (await ErrorAsync(reader, "search_items", new { fields = new[] { new { field = secret, min = 1 } } })).Code(),
            "a hidden field can't be searched by");

        var config = await OkAsync(reader, "list_config", new { include = new[] { "sections" } });
        var ids = config.Each("sections").Select(s => s.S("id")).ToList();
        CollectionAssert.Contains(ids, open.ToString());
        CollectionAssert.DoesNotContain(ids, hidden.ToString(), "hidden sections are not listed");
        var scoreDef = config.Each("sections").Single(s => s.S("id") == open.ToString()).Each("fields").Single(f => f.S("key") == score);
        Assert.AreEqual("sum", scoreDef.GetProperty("config").S("op"));
        Assert.IsFalse(config.Each("sections").Any(s => s.GetProperty("canEdit").GetBoolean()), "a read token edits nothing");
    }

    [TestMethod]
    public async Task CreateProjectAppliesTheTemplateAsTheBrowserDoes()
    {
        var (mine, theirs, board) = (await SyncKit.GroupAsync(app, "mcp-tpl"), await SyncKit.GroupAsync(app, "mcp-tpl2"), await SyncKit.GroupAsync(app, "mcp-tpl3"));
        var (user, _, write) = await WriterAsync(app, "projector", mine);
        var (planned, running, foreign) = (await ContentKit.StatusAsync(app, ItemType.Project), await ContentKit.StatusAsync(app, ItemType.Project, StatusCategory.Active),
            await ContentKit.StatusAsync(app, ItemType.Project));
        var section = await ContentKit.SectionAsync(app, ["project"]);
        var (budgetId, budget) = await ContentKit.FieldAsync(app, section, FieldType.Number);
        var (_, goal) = await ContentKit.FieldAsync(app, section, FieldType.Text);
        var hidden = await ContentKit.SectionAsync(app, ["project"], (board, SectionAccess.View));
        var template = await ContentKit.TemplateAsync(app, planned, running);
        await using (var db = app.NewDb())
        {
            await db.FieldDefinitions.Where(f => f.Key == goal).ExecuteUpdateAsync(f => f.SetProperty(x => x.DefaultValue, "\"Ship it\""));
            var row = await db.ProjectTemplates.SingleAsync(t => t.Id == template);
            row.DefaultStatusId = running;
            row.DefaultShares = new JsonArray(new JsonObject { ["groupId"] = mine.ToString(), ["access"] = "edit" },
                new JsonObject { ["groupId"] = theirs.ToString(), ["access"] = "manage" }).ToJsonString();
            db.TemplateSections.AddRange(new TemplateSectionEntity { Id = Lib.Helper.Ids.New(), TemplateId = template, SectionId = section, SortOrder = 10 },
                new TemplateSectionEntity { Id = Lib.Helper.Ids.New(), TemplateId = template, SectionId = hidden, SortOrder = 20 });
            db.TemplateFieldDefaults.Add(new TemplateFieldDefaultEntity { Id = Lib.Helper.Ids.New(), TemplateId = template, FieldId = budgetId, Value = "7" });
            db.TemplateMilestones.AddRange(
                new TemplateMilestoneEntity { Id = Lib.Helper.Ids.New(), TemplateId = template, Kind = MilestoneKind.Milestone, Title = "Kick-off", OffsetDays = 10,
                    Dod = """["Team named", " ", "Budget set"]""", SortOrder = "a0" },
                new TemplateMilestoneEntity { Id = Lib.Helper.Ids.New(), TemplateId = template, Kind = MilestoneKind.Gate, Title = "G1", OffsetDays = 30, Dod = "[]",
                    OnGoStatusId = planned, OnNoGoStatusId = running, SortOrder = "a1" });
            await db.SaveChangesAsync();
        }
        var templateName = await NameAsync(template);
        await using var client = await ClientAsync(app, write);

        var project = await OkAsync(client, "create_project", new { title = "From the template", template = templateName.ToLowerInvariant(), startDate = "2027-01-10" });
        var id = Guid.Parse(project.S("id")!);
        Assert.AreEqual((running.ToString(), template.ToString()), (project.GetProperty("status").S("id"), project.GetProperty("template").S("id")),
            "the template's default status");
        Assert.AreEqual(7, FieldOf(project, budget)!.Value.GetProperty("value").GetInt32(), "the template's default");
        Assert.AreEqual("Ship it", FieldOf(project, goal)!.Value.S("value"), "the field's own default");
        CollectionAssert.AreEqual(new[] { section.ToString() }, project.Each("sections").Select(s => s.S("id")).ToList(), "not the hidden section");
        var milestones = project.Each("milestones").ToList();
        Assert.AreEqual(("Kick-off", "2027-01-20", "0/2"), (milestones[0].S("title"), milestones[0].S("dueDate"), milestones[0].S("dodProgress")));
        Assert.AreEqual(("gate", "2027-02-09", "pending"), (milestones[1].S("kind"), milestones[1].S("dueDate"), milestones[1].S("decision")));
        Assert.AreEqual(await NameAsync(planned, "statuses"), milestones[1].S("onGoStatus"));
        var shares = project.Each("shares").ToDictionary(s => s.S("groupId")!, s => s.S("access"));
        Assert.AreEqual(("edit", "edit"), (shares[mine.ToString()], shares[theirs.ToString()]), "the template's shares, at most edit");

        await using (var db = app.NewDb())
        {
            var item = await db.Items.SingleAsync(i => i.Id == id);
            Assert.AreEqual((new DateOnly(2027, 1, 10), template, user), (item.ProjectStartDate!.Value, item.ProjectTemplateId!.Value, item.OwnerUserId));
            var keys = await db.Milestones.Where(m => m.ItemId == id).OrderBy(m => m.SortOrder).Select(m => m.SortOrder).ToListAsync();
            CollectionAssert.AreEqual(new[] { "a0", "a1" }, keys);
            Assert.IsFalse(await db.ItemSections.AnyAsync(s => s.ItemId == id && s.SectionId == hidden));
            Assert.AreEqual(1, await db.SyncMutations.Where(m => m.RowId == id && m.Op == "insert").Select(m => m.BatchId).Distinct().CountAsync());
        }

        var refused = await ErrorAsync(client, "create_project", new { title = "Wrong status", template = template.ToString(), status = foreign.ToString() });
        Assert.AreEqual("validation_failed", refused.Code(), refused.GetRawText());
        var shared = await OkAsync(client, "create_project", new
        {
            title = "Own shares", template = template.ToString(), share_with = new[] { new { group = mine.ToString(), access = "read" } },
            fields = new Dictionary<string, object> { [budget] = 99 },
        });
        Assert.AreEqual(1, shared.GetProperty("shares").GetArrayLength(), "share_with replaces the template's shares");
        Assert.AreEqual(99, FieldOf(shared, budget)!.Value.GetProperty("value").GetInt32(), "a given value before the template's");
        Assert.AreEqual(ItemTools_Today(), shared.GetProperty("startDate").GetString(), "a template's project starts today by default");
        var blank = await OkAsync(client, "create_project", new { title = "Blank", status = planned.ToString() });
        Assert.AreEqual(0, blank.GetProperty("milestones").GetArrayLength());
    }

    private static string ItemTools_Today() => PGPM.Mcp.Tools.ItemTools.Today().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> NameAsync(Guid id, string table = "project_templates")
    {
        await using var db = app.NewDb();
        return table == "statuses" ? await db.Statuses.Where(s => s.Id == id).Select(s => s.Name).SingleAsync()
            : await db.ProjectTemplates.Where(t => t.Id == id).Select(t => t.Name).SingleAsync();
    }

    [TestMethod]
    public async Task MilestonesGatesAndTheirCriteria()
    {
        var group = await SyncKit.GroupAsync(app, "mcp-gates");
        var (user, read, write) = await WriterAsync(app, "gatekeeper", group);
        var (planned, approved, stopped) = (await ContentKit.StatusAsync(app, ItemType.Project), await ContentKit.StatusAsync(app, ItemType.Project, StatusCategory.Active),
            await ContentKit.StatusAsync(app, ItemType.Project, StatusCategory.Closed));
        var approvedName = await NameAsync(approved, "statuses");
        await using var client = await ClientAsync(app, write);
        var project = await OkAsync(client, "create_project", new { title = "Gated", status = planned.ToString() });
        var key = project.S("key")!;

        var added = await OkAsync(client, "add_milestone", new
        {
            project = key, kind = "gate", title = "G0 Proof of concept", dueDate = "2027-02-01", dod = new[] { "Data access", "Success criteria" },
            onGoStatus = approvedName, onNoGoStatus = stopped.ToString(),
        });
        var gate = added.GetProperty("milestone");
        var gateId = gate.S("id")!;
        Assert.AreEqual(("gate", "pending", "0/2", approvedName), (gate.S("kind"), gate.S("decision"), gate.S("dodProgress"), gate.S("onGoStatus")));
        var second = await OkAsync(client, "add_milestone", new { project = project.S("id"), title = "Launch" });
        Assert.AreEqual("milestone", second.GetProperty("milestone").S("kind"));
        Assert.AreEqual("invalid_argument", (await ErrorAsync(client, "add_milestone", new { project = key, title = "x", onGoStatus = approvedName })).Code(),
            "only gates suggest statuses");

        var first = gate.Each("dod").First().S("id");
        var done = await OkAsync(client, "set_dod_item", new { id = first, state = "done", note = "https://example.org/evidence" });
        Assert.AreEqual(("1/2", 1), (done.GetProperty("milestone").S("dodProgress"), done.GetProperty("dodOpen").GetInt32()));
        Assert.AreEqual("validation_failed", (await ErrorAsync(client, "set_dod_item", new { milestone = gateId, text = "Legal check", state = "na" })).Code(),
            "n/a needs a note");
        var na = await OkAsync(client, "set_dod_item", new { milestone = gateId, text = "Legal check", state = "na", note = "No personal data" });
        Assert.AreEqual("2/3", na.GetProperty("milestone").S("dodProgress"), "a new criterion");
        var byText = await OkAsync(client, "set_dod_item", new { milestone = gateId, text = "success CRITERIA", state = "done" });
        Assert.AreEqual(0, byText.GetProperty("dodOpen").GetInt32(), "found by its text");

        var moved = await OkAsync(client, "update_milestone", new { id = second.GetProperty("milestone").S("id"), status = "done", dueDate = "" });
        Assert.AreEqual(("done", true), (moved.GetProperty("milestone").S("status"), moved.GetProperty("milestone").Missing("dueDate")));

        var decided = await OkAsync(client, "record_gate_decision", new { id = gateId, decision = "go", note = "Convincing PoC", applyStatus = true });
        Assert.AreEqual((true, approvedName), (decided.GetProperty("statusChanged").GetBoolean(), decided.S("projectStatus")));
        Assert.AreEqual(("go", "Convincing PoC"), (decided.GetProperty("milestone").S("decision"), decided.GetProperty("milestone").S("decisionNote")));
        Assert.IsNotNull(decided.GetProperty("milestone").S("decidedBy"), "the server stamps who decided");
        var again = await OkAsync(client, "record_gate_decision", new { id = gateId, decision = "no_go" });
        Assert.AreEqual((false, stopped.ToString()), (again.GetProperty("statusChanged").GetBoolean(), again.GetProperty("suggestedStatus").S("id")),
            "suggested, not applied");
        Assert.AreEqual("invalid_argument", (await ErrorAsync(client, "record_gate_decision",
            new { id = second.GetProperty("milestone").S("id"), decision = "go" })).Code(), "milestones aren't decided");

        await using (var db = app.NewDb())
        {
            var row = await db.Milestones.SingleAsync(m => m.Id == Guid.Parse(gateId));
            Assert.AreEqual((GateDecision.NoGo, user), (row.Decision!.Value, row.DecidedBy!.Value));
            Assert.AreEqual(approved, (await db.Items.SingleAsync(i => i.Id == Guid.Parse(project.S("id")!))).StatusId);
            Assert.IsTrue(await db.ChangeLog.AnyAsync(c => c.Entity == "milestones" && c.Source == ChangeSource.Mcp && c.EntityId == row.Id));
        }

        // Read access is not enough: someone else's project, shared read.
        var other = await MemberAsync(app, "gate-owner");
        var theirs = await ItemAsync(app, other, "project", "Theirs", planned, "read", group);
        var milestone = Lib.Helper.Ids.New();
        await SyncAsync(app, other, SyncKit.Insert("milestones", milestone, new JsonObject
        {
            ["itemId"] = theirs.ToString(), ["kind"] = "gate", ["title"] = "Their gate", ["status"] = "planned", ["decision"] = "pending", ["sortOrder"] = "a0",
        }));
        Assert.AreEqual("missing_access", (await ErrorAsync(client, "record_gate_decision", new { id = milestone.ToString(), decision = "go" })).Code());
        Assert.AreEqual("missing_access", (await ErrorAsync(client, "add_milestone", new { project = theirs.ToString(), title = "Mine" })).Code());
        await using var reader = await ClientAsync(app, read);
        Assert.AreEqual("not_available", (await ErrorAsync(reader, "record_gate_decision", new { id = gateId, decision = "go" })).Code());
        var seen = await OkAsync(reader, "get_item", new { id = theirs.ToString() });
        Assert.AreEqual("Their gate", seen.Each("milestones").Single().S("title"));
    }

    [TestMethod]
    public async Task PersonsAndLinks()
    {
        var group = await SyncKit.GroupAsync(app, "mcp-links");
        var (user, _, write) = await WriterAsync(app, "linker", group);
        var colleague = await MemberAsync(app, "colleague", group);
        var stranger = await MemberAsync(app, "stranger");
        var status = await ContentKit.StatusAsync(app, ItemType.Idea);
        var idea = await ItemAsync(app, user, "idea", "Linked idea", status);
        var note = await ItemAsync(app, colleague, "note", "A learning", null, "read", group);
        var secret = await ItemAsync(app, stranger, "note", "Secret", null);
        await using var db = app.NewDb();
        var colleaguePerson = await db.Persons.Where(p => p.UserId == colleague).Select(p => p.Id).SingleAsync();
        await using var client = await ClientAsync(app, write);

        var assigned = await OkAsync(client, "assign_person", new { item = idea.ToString(), person = "me", role = "Owner" });
        Assert.AreEqual("added", assigned.S("outcome"));
        await OkAsync(client, "assign_person", new { item = idea.ToString(), person = colleaguePerson.ToString() });
        var changed = await OkAsync(client, "assign_person", new { item = idea.ToString(), person = "me", role = "Sponsor" });
        Assert.AreEqual("updated", changed.S("outcome"));
        Assert.AreEqual(2, changed.GetProperty("persons").GetArrayLength());
        Assert.AreEqual("unchanged", (await OkAsync(client, "assign_person", new { item = idea.ToString(), person = "me" })).S("outcome"));
        Assert.AreEqual("removed", (await OkAsync(client, "unassign_person", new { item = idea.ToString(), person = colleaguePerson.ToString() })).S("outcome"));
        Assert.AreEqual("not_found", (await ErrorAsync(client, "assign_person", new { item = idea.ToString(), person = Guid.NewGuid().ToString() })).Code());

        var project = await OkAsync(client, "create_project", new { title = "Target", status = (await ContentKit.StatusAsync(app, ItemType.Project)).ToString() });
        var gate = await OkAsync(client, "add_milestone", new { project = project.S("id"), kind = "gate", title = "Review" });
        var decider = await OkAsync(client, "assign_person", new { milestone = gate.GetProperty("milestone").S("id"), person = "me" });
        Assert.AreEqual("Review board", decider.GetProperty("persons").EnumerateArray().Single().S("role"), "a gate's persons decide");

        var linked = await OkAsync(client, "link_items", new { from = idea.ToString(), to = project.S("key"), relation = "contributes_to" });
        Assert.AreEqual("linked (contributes_to)", linked.S("outcome"));
        Assert.AreEqual("unchanged (contributes_to)", (await OkAsync(client, "link_items", new { from = idea.ToString(), to = project.S("id"), relation = "contributes_to" })).S("outcome"));
        Assert.AreEqual("missing_access", (await ErrorAsync(client, "link_items", new { from = note.ToString(), to = idea.ToString() })).Code(), "read on the note");
        Assert.AreEqual("not_found", (await ErrorAsync(client, "link_items", new { from = idea.ToString(), to = secret.ToString() })).Code());
        await OkAsync(client, "link_items", new { from = idea.ToString(), to = note.ToString() });

        var detail = await OkAsync(client, "get_item", new { id = project.S("id") });
        var incoming = detail.Each("links").Single();
        Assert.AreEqual(("in", "contributes_to", idea.ToString()), (incoming.S("direction"), incoming.S("relation"), incoming.GetProperty("item").S("id")));
        var unlinked = await OkAsync(client, "unlink_items", new { from = idea.ToString(), to = project.S("id") });
        Assert.AreEqual("1 removed", unlinked.S("outcome"));
        Assert.AreEqual(1, unlinked.GetProperty("links").GetArrayLength(), "the note's link stays");
        Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.Event == AuditEvents.McpItemUpdated && l.SubjectId == idea && l.Actor == AuditActor.Mcp));
    }
}
