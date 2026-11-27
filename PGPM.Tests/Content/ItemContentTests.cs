using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Config;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Tests.Sync;
using static PGPM.Tests.Content.ContentKit;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Content;

/// <summary>
/// Ideas, leads and projects and what hangs below them (docs/02 §6–§8) through the sync: their rules, rights and access,
/// merged duplicate pairs, field values by type and section access, cascades and restore.
/// </summary>
[TestClass]
public sealed class ItemContentTests
{
    private const string Everything = "Notes:Create,Ideas:Create,Leads:Create,Projects:Create,Persons:Edit";

    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static async Task<(Guid Group, Guid A, Guid B, SyncClient ClientA, SyncClient ClientB)> TeamAsync(string rightsB = Everything)
    {
        var group = await GroupAsync(app, "content");
        var a = await UserAsync(app, "author", group);
        var b = await UserAsync(app, "colleague", group);
        var clientA = new SyncClient(app.ClientFor(a, Everything));
        var clientB = new SyncClient(app.ClientFor(b, rightsB));
        await clientA.SyncAllAsync();
        await clientB.SyncAllAsync();
        return (group, a, b, clientA, clientB);
    }

    [TestMethod]
    public async Task EveryTypeIsCreatedWithItsStatusRules()
    {
        var (_, _, _, client, _) = await TeamAsync();
        var idea = await StatusAsync(app, ItemType.Idea);
        var lead = await StatusAsync(app, ItemType.Lead);
        var allowed = await StatusAsync(app, ItemType.Project);
        var other = await StatusAsync(app, ItemType.Project);
        var archived = await StatusAsync(app, ItemType.Idea, archived: true);
        var template = await TemplateAsync(app, allowed);
        var (i, l, p, bad, wrongType, refused, old) = (Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New());
        var answer = await client.SyncAllAsync(
        [
            Insert("items", i, Item("idea", "An idea", idea)),
            Insert("items", l, Item("lead", "A lead", lead, new JsonObject { ["leadOrganisation"] = "ACME", ["leadProbability"] = 40 })),
            Insert("items", p, Item("project", "A project", allowed, new JsonObject { ["projectTemplateId"] = template.ToString() })),
            Insert("items", bad, Item("project", "Not allowed", other, new JsonObject { ["projectTemplateId"] = template.ToString() })),
            Insert("items", wrongType, Item("idea", "Lead status", lead)),
            Insert("items", refused, Item("idea", "Lead fields", idea, new JsonObject { ["leadValue"] = 10 })),
            Insert("items", old, Item("idea", "Archived", archived)),
        ]);
        CollectionAssert.AreEqual(new[] { "accepted", "accepted", "accepted", "validation_failed", "validation_failed", "validation_failed", "validation_failed" },
            Codes(answer), answer["results"]!.ToJsonString());
        Assert.IsNotNull(client.Row("items", i)!["number"]);
        Assert.IsNotNull(client.Row("items", i)!["statusChangedAt"], "the server stamps the status");
        // A project's status stays within its template's.
        var row = client.Row("items", p)!;
        var patch = await client.SyncAllAsync([Patch("items", p, row.Int("rev"), ("statusId", allowed.ToString(), other.ToString()))]);
        Assert.AreEqual("validation_failed", Codes(patch)[0]);
        // Rights per type.
        var noIdeas = new SyncClient(app.ClientFor(await UserAsync(app, "noideas"), "Notes:Create"));
        var refusedAnswer = await noIdeas.SyncAllAsync([Insert("items", Ids.New(), Item("idea", "No right", idea))]);
        Assert.AreEqual("missing_right", Codes(refusedAnswer)[0]);
    }

    [TestMethod]
    public async Task AConversionMarksItsOrigin()
    {
        var (_, _, _, client, _) = await TeamAsync();
        var idea = Ids.New();
        var project = Ids.New();
        await client.SyncAllAsync([Insert("items", idea, Item("idea", "Origin", await StatusAsync(app, ItemType.Idea)))]);
        var answer = await client.SyncAllAsync([Insert("items", project, Item("project", "Converted", await StatusAsync(app, ItemType.Project),
            new JsonObject { ["originItemId"] = idea.ToString() }))]);
        Assert.AreEqual("accepted", Codes(answer)[0]);
        Assert.AreEqual(idea.ToString(), client.Row("items", project).Str("originItemId"));
        Assert.AreEqual(project.ToString(), client.Row("items", idea).Str("convertedToItemId"));
    }

    [TestMethod]
    public async Task ChildrenFollowTheirItemsAccessAndDuplicatesMerge()
    {
        var (group, _, b, clientA, clientB) = await TeamAsync();
        var project = Ids.New();
        var batch = Ids.New();
        var status = await StatusAsync(app, ItemType.Project);
        await clientA.SyncAllAsync([Insert("items", project, Item("project", "Shared", status), batch),
            Insert("item_shares", Ids.New(), Share(project, group, "read"), batch)]);
        var person = Ids.New();
        var (gate, dod, assignment, duplicate) = (Ids.New(), Ids.New(), Ids.New(), Ids.New());
        var answer = await clientA.SyncAllAsync(
        [
            Insert("persons", person, new JsonObject { ["displayName"] = "Extern" }),
            Insert("milestones", gate, new JsonObject { ["itemId"] = project.ToString(), ["kind"] = "gate", ["title"] = "G1", ["sortOrder"] = "a0",
                ["decision"] = "go" }),
            Insert("dod_items", dod, new JsonObject { ["milestoneId"] = gate.ToString(), ["text"] = "Done?", ["state"] = "na", ["sortOrder"] = "a0" }),
            Insert("dod_items", Ids.New(), new JsonObject { ["milestoneId"] = gate.ToString(), ["text"] = "Proof", ["state"] = "na", ["note"] = "n/a: see doc",
                ["sortOrder"] = "a1" }),
            Insert("assignments", assignment, new JsonObject { ["itemId"] = project.ToString(), ["personId"] = person.ToString(), ["role"] = "Sponsor",
                ["sortOrder"] = "a0" }),
            Insert("assignments", duplicate, new JsonObject { ["itemId"] = project.ToString(), ["personId"] = person.ToString(), ["sortOrder"] = "a1" }),
            Insert("milestones", Ids.New(), new JsonObject { ["itemId"] = project.ToString(), ["kind"] = "milestone", ["title"] = "M", ["sortOrder"] = "a1",
                ["decision"] = "go" }),
        ]);
        CollectionAssert.AreEqual(new[] { "accepted", "accepted", "validation_failed", "accepted", "accepted", "accepted", "validation_failed" }, Codes(answer),
            answer["results"]!.ToJsonString());
        Assert.AreEqual(assignment.ToString(), answer.Arr("results")[5].Str("mergedInto"), "a person once per item");
        var stored = clientA.Row("milestones", gate)!;
        Assert.IsNotNull(stored["decidedAt"], "the server stamps the decision");

        // The colleague reads (read share) – everything below the project – but changes nothing.
        await clientB.SyncAllAsync();
        Assert.IsNotNull(clientB.Row("milestones", gate));
        Assert.IsNotNull(clientB.Row("assignments", assignment));
        var refused = await clientB.SyncAllAsync([Patch("milestones", gate, stored.Int("rev"), ("title", "G1", "Gate 1"))]);
        Assert.AreEqual("missing_access", Codes(refused)[0]);
        // A stranger sees none of it.
        var stranger = new SyncClient(app.ClientFor(await UserAsync(app, "stranger"), Everything));
        await stranger.SyncAllAsync();
        Assert.IsNull(stranger.Row("milestones", gate));
        Assert.IsNull(stranger.Row("assignments", assignment));
        _ = b;
    }

    [TestMethod]
    public async Task LinksNeedATargetToReadAndGoWithEitherItem()
    {
        var (_, _, _, client, _) = await TeamAsync();
        var (note, project, secret) = (Ids.New(), Ids.New(), Ids.New());
        var stranger = new SyncClient(app.ClientFor(await UserAsync(app, "other"), Everything));
        await stranger.SyncAllAsync([Insert("items", secret, Note("Not yours"))]);
        await client.SyncAllAsync([Insert("items", note, Note("Learning")), Insert("items", project, Item("project", "Target", await StatusAsync(app, ItemType.Project)))]);
        var (link, again) = (Ids.New(), Ids.New());
        var answer = await client.SyncAllAsync(
        [
            Insert("item_links", link, new JsonObject { ["fromItemId"] = note.ToString(), ["toItemId"] = project.ToString(), ["relation"] = "learning_of" }),
            Insert("item_links", again, new JsonObject { ["fromItemId"] = note.ToString(), ["toItemId"] = project.ToString(), ["relation"] = "learning_of" }),
            Insert("item_links", Ids.New(), new JsonObject { ["fromItemId"] = note.ToString(), ["toItemId"] = secret.ToString(), ["relation"] = "related" }),
            Insert("item_links", Ids.New(), new JsonObject { ["fromItemId"] = note.ToString(), ["toItemId"] = note.ToString(), ["relation"] = "related" }),
        ]);
        CollectionAssert.AreEqual(new[] { "accepted", "accepted", "parent_missing", "validation_failed" }, Codes(answer), answer["results"]!.ToJsonString());
        Assert.AreEqual(link.ToString(), answer.Arr("results")[1].Str("mergedInto"));

        // Deleting the target tombstones the incoming link in its batch; a restore brings both back.
        var target = client.Row("items", project)!;
        await client.SyncAllAsync([Delete("items", project, target.Int("rev"))]);
        Assert.IsNull(client.Row("item_links", link), "the link went with its target");
        await using (var db = app.NewDb())
        {
            var batch = await db.Items.Where(i => i.Id == project).Select(i => i.DeleteBatchId).SingleAsync();
            Assert.AreEqual(batch, await db.ItemLinks.Where(l => l.Id == link).Select(l => l.DeleteBatchId).SingleAsync());
            using var restored = await client.Http.PostEmptyAsync($"/api/v1/trash/{batch}/restore");
            Assert.AreEqual(2, (await restored.JsonAsync()).Int("restored"));
        }
        await client.SyncAllAsync();
        Assert.IsNotNull(client.Row("item_links", link));
    }

    [TestMethod]
    public async Task FieldValuesHaveTheirTypesShapeAndTheirSectionsAccess()
    {
        var (group, _, _, clientA, clientB) = await TeamAsync();
        var board = await GroupAsync(app, "board");
        var boardMember = await UserAsync(app, "boardmember", group, board);
        var clientC = new SyncClient(app.ClientFor(boardMember, Everything));
        var open = await SectionAsync(app, ["idea"]);
        var decision = await SectionAsync(app, ["idea"], (board, SectionAccess.Edit));
        var hidden = await SectionAsync(app, ["idea"], (board, SectionAccess.View));
        var rating = await FieldAsync(app, open, FieldType.Rating, new JsonObject { ["allowNa"] = true });
        var choice = await FieldAsync(app, open, FieldType.Select, JsonNode.Parse("""{"options": [{"id": "a", "label": "A"}, {"id": "b", "label": "B", "archived": true}]}""")!.AsObject());
        var number = await FieldAsync(app, open, FieldType.Number, new JsonObject { ["min"] = 0, ["max"] = 10 });
        var persons = await FieldAsync(app, open, FieldType.Person, new JsonObject { ["multiple"] = true });
        var onePerson = await FieldAsync(app, open, FieldType.Person);
        var score = await FieldAsync(app, open, FieldType.Score, JsonNode.Parse($$"""{"op": "sum", "inputs": ["{{rating.Key}}"]}""")!.AsObject());
        var verdict = await FieldAsync(app, decision, FieldType.Text);
        var secret = await FieldAsync(app, hidden, FieldType.Text);
        var project = await SectionAsync(app, ["project"]);
        var projectOnly = await FieldAsync(app, project, FieldType.Text);

        var idea = Ids.New();
        var batch = Ids.New();
        await clientA.SyncAllAsync([Insert("items", idea, Item("idea", "Assessed", await StatusAsync(app, ItemType.Idea)), batch),
            Insert("item_shares", Ids.New(), Share(idea, group, "edit"), batch)]);
        JsonObject Value(Guid field, JsonNode? value) => new() { ["itemId"] = idea.ToString(), ["fieldId"] = field.ToString(), ["value"] = value?.DeepClone() };
        var me = clientA.Rows("persons").First().Str("id")!;
        var answer = await clientA.SyncAllAsync(
        [
            Insert("field_values", Ids.New(), Value(rating.Id, 4)),
            Insert("field_values", Ids.New(), Value(choice.Id, "a")),
            Insert("field_values", Ids.New(), Value(number.Id, 12)),
            Insert("field_values", Ids.New(), Value(persons.Id, new JsonArray(me))),
            Insert("field_values", Ids.New(), Value(score.Id, 3)),
            Insert("field_values", Ids.New(), Value(verdict.Id, "go")),
            Insert("field_values", Ids.New(), Value(choice.Id, "b")),
            Insert("field_values", Ids.New(), Value(projectOnly.Id, "x")),
            Insert("field_values", Ids.New(), Value(onePerson.Id, new JsonArray(Ids.New().ToString()))),
            Insert("field_values", Ids.New(), Value(onePerson.Id, new JsonArray(me, Ids.New().ToString()))),
            Insert("field_values", Ids.New(), Value(number.Id, 5)),
            Insert("field_values", Ids.New(), Value(number.Id, 7)),
        ]);
        // The last is a second value of the same field (another device, offline): a patch from null, which conflicts with 5.
        CollectionAssert.AreEqual(new[]
        {
            "accepted", "accepted", "validation_failed", "accepted", "validation_failed", "missing_access", "validation_failed", "validation_failed",
            "validation_failed", "validation_failed", "accepted", "conflict",
        }, Codes(answer), answer["results"]!.ToJsonString());
        Assert.AreEqual(answer.Arr("results")[10].Str("id"), answer.Arr("results")[11].Str("mergedInto"));
        Assert.AreEqual(5, answer.Arr("conflicts").Single()!["theirs"]!.GetValue<int>());
        var ratingRow = clientA.Rows("field_values").Single(v => v.Str("fieldId") == rating.Id.ToString());
        var na = await clientA.SyncAllAsync([Patch("field_values", Guid.Parse(ratingRow.Str("id")!), ratingRow.Int("rev"), ("value", 4, "na")),]);
        Assert.AreEqual("accepted", Codes(na)[0]);
        var tooHigh = await clientA.SyncAllAsync([Patch("field_values", Guid.Parse(ratingRow.Str("id")!), ratingRow.Int("rev") + 1, ("value", "na", 6))]);
        Assert.AreEqual("validation_failed", Codes(tooHigh)[0]);

        // The board edits its section; the hidden section and its values never reach anyone outside the board.
        await clientC.SyncAllAsync();
        var byBoard = await clientC.SyncAllAsync([Insert("field_values", Ids.New(), Value(verdict.Id, "go")), Insert("field_values", Ids.New(), Value(secret.Id, "s"))]);
        CollectionAssert.AreEqual(new[] { "accepted", "accepted" }, Codes(byBoard));
        await clientB.SyncAllAsync();
        Assert.IsTrue(clientB.Rows("field_values").Any(v => v.Str("fieldId") == verdict.Id.ToString()), "edit rows only: everybody sees it");
        Assert.IsFalse(clientB.Rows("field_values").Any(v => v.Str("fieldId") == secret.Id.ToString()));
        Assert.IsNull(clientB.Row("sections", hidden));
        Assert.IsNull(clientB.Row("field_definitions", secret.Id));
        Assert.IsFalse(clientB.Row("sections", decision)!.Bool("canEdit"));
        Assert.IsTrue(clientC.Row("sections", decision)!.Bool("canEdit"));
        Assert.IsTrue(clientB.Row("sections", open)!.Bool("canEdit"));
        Assert.IsNotNull(clientC.Row("sections", hidden));
    }

    [TestMethod]
    public async Task DeletingAProjectTakesEverythingBelowItAndRestoreBringsItBack()
    {
        var (_, _, _, client, _) = await TeamAsync();
        var section = await SectionAsync(app, ["project"]);
        var field = await FieldAsync(app, section, FieldType.Text);
        var (project, gate, dod, value, itemSection, file) = (Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New());
        var person = client.Rows("persons").First().Str("id");
        await client.SyncAllAsync(
        [
            Insert("items", project, Item("project", "Doomed", await StatusAsync(app, ItemType.Project))),
            Insert("item_sections", itemSection, new JsonObject { ["itemId"] = project.ToString(), ["sectionId"] = section.ToString(), ["sortOrder"] = 1 }),
            Insert("field_values", value, new JsonObject { ["itemId"] = project.ToString(), ["fieldId"] = field.Id.ToString(), ["value"] = "v" }),
            Insert("milestones", gate, new JsonObject { ["itemId"] = project.ToString(), ["kind"] = "gate", ["title"] = "G", ["sortOrder"] = "a0" }),
            Insert("dod_items", dod, new JsonObject { ["milestoneId"] = gate.ToString(), ["text"] = "X", ["sortOrder"] = "a0" }),
            Insert("assignments", Ids.New(), new JsonObject { ["milestoneId"] = gate.ToString(), ["personId"] = person, ["sortOrder"] = "a0" }),
            Insert("files", file, new JsonObject
            {
                ["itemId"] = project.ToString(), ["purpose"] = "attachment", ["fileName"] = "plan.pdf", ["contentType"] = "application/pdf",
                ["sizeBytes"] = 3, ["sha256"] = new string('b', 64),
            }),
        ]);
        Assert.AreEqual(7, client.Rows("items").Count(i => i.Str("id") == project.ToString()) + client.Rows("item_sections").Count(r => r.Str("itemId") == project.ToString())
            + client.Rows("field_values").Count(r => r.Str("itemId") == project.ToString()) + client.Rows("milestones").Count(r => r.Str("itemId") == project.ToString())
            + client.Rows("dod_items").Count(r => r.Str("milestoneId") == gate.ToString()) + client.Rows("assignments").Count(r => r.Str("milestoneId") == gate.ToString())
            + client.Rows("files").Count(r => r.Str("itemId") == project.ToString()));
        await client.SyncAllAsync([Delete("items", project, client.Row("items", project).Int("rev"))]);
        Assert.IsNull(client.Row("dod_items", dod));
        Assert.IsNull(client.Row("files", file));
        await using var db = app.NewDb();
        var batch = await db.Items.Where(i => i.Id == project).Select(i => i.DeleteBatchId).SingleAsync();
        Assert.AreEqual(7, await db.Database.SqlQuery<int>($"""
            SELECT (SELECT count(*) FROM items WHERE delete_batch_id = {batch}) + (SELECT count(*) FROM item_sections WHERE delete_batch_id = {batch})
              + (SELECT count(*) FROM field_values WHERE delete_batch_id = {batch}) + (SELECT count(*) FROM milestones WHERE delete_batch_id = {batch})
              + (SELECT count(*) FROM dod_items WHERE delete_batch_id = {batch}) + (SELECT count(*) FROM assignments WHERE delete_batch_id = {batch})
              + (SELECT count(*) FROM files WHERE delete_batch_id = {batch}) AS "Value"
            """).SingleAsync());
        using var restored = await client.Http.PostEmptyAsync($"/api/v1/trash/{batch}/restore");
        Assert.AreEqual(7, (await restored.JsonAsync()).Int("restored"));
        await client.SyncAllAsync();
        Assert.IsNotNull(client.Row("dod_items", dod));
        Assert.IsNotNull(client.Row("field_values", value));
    }
}
