using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Config;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Sync;
using static PGPM.Tests.Content.ContentKit;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Sync;

/// <summary>
/// The security review's negative cases (docs/06): a user with no share pushing to every synced table, hidden sections in the
/// pull and the History, duplicate pairs of read-only users, the Trash and the files of foreign items.
/// </summary>
[TestClass]
public sealed class SyncSecurityTests
{
    private const string AllButPersonsAndConfiguration = "Notes:Create,Ideas:Create,Leads:Create,Projects:Create,Maps:Create";
    private const string Everything = AllButPersonsAndConfiguration + ",Persons:Edit";

    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static JsonObject Row(params (string Key, JsonNode? Value)[] values)
    {
        var row = new JsonObject();
        foreach (var (key, value) in values)
        {
            row[key] = value;
        }
        return row;
    }

    /// <summary>
    /// An owner's idea, project (milestone, DoD criterion, assignment, link, section, value, file), canvas map (share, layer,
    /// element, map image), saved view and setting; then a stranger – every create right, no share, no Persons: Edit – pushes
    /// an insert, a patch and a delete to every table of <see cref="SyncTables.All"/>. Each is refused with the expected code,
    /// nothing of the owner's changes, and the stranger's pull holds none of it.
    /// </summary>
    [TestMethod]
    public async Task AStrangerIsRefusedOnEveryTableAndPullsNothingOfTheOwners()
    {
        var team = await GroupAsync(app, "secowner");
        var owner = await UserAsync(app, "secowner", team);
        var ownerClient = new SyncClient(app.ClientFor(owner, Everything));
        await ownerClient.SyncAllAsync();
        var person = Guid.Parse(ownerClient.Rows("persons").Single(p => p.Str("userId") == owner.ToString()).Str("id")!);
        var section = await SectionAsync(app, ["idea", "project"]);
        var field = await FieldAsync(app, section, FieldType.Text);
        var ideaStatus = await StatusAsync(app, ItemType.Idea);
        var projectStatus = await StatusAsync(app, ItemType.Project);
        var (idea, project, itemSection, value, milestone, dod, assignment, link) =
            (Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New());
        var (file, map, mapShare, layer, element, mapImage, view, setting, itemShare) =
            (Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New());
        var batch = Ids.New();
        var mapBatch = Ids.New();
        var created = await ownerClient.SyncAllAsync(
        [
            Insert("items", idea, Item("idea", "Owner's idea", ideaStatus), batch),
            Insert("item_shares", itemShare, Share(idea, team, "edit"), batch),
            Insert("items", project, Item("project", "Owner's project", projectStatus)),
            Insert("item_sections", itemSection, Row(("itemId", idea.ToString()), ("sectionId", section.ToString()), ("sortOrder", 10))),
            Insert("field_values", value, Row(("itemId", idea.ToString()), ("fieldId", field.Id.ToString()), ("value", "secret"))),
            Insert("milestones", milestone, Row(("itemId", project.ToString()), ("kind", "gate"), ("title", "G1"), ("sortOrder", "a0"))),
            Insert("dod_items", dod, Row(("milestoneId", milestone.ToString()), ("text", "Proof"), ("sortOrder", "a0"))),
            Insert("assignments", assignment, Row(("itemId", project.ToString()), ("personId", person.ToString()), ("sortOrder", "a0"))),
            Insert("item_links", link, Row(("fromItemId", project.ToString()), ("toItemId", idea.ToString()), ("relation", "related"))),
            Insert("files", file, Row(("itemId", idea.ToString()), ("purpose", "attachment"), ("fileName", "plan.txt"), ("contentType", "text/plain"),
                ("sizeBytes", 3), ("sha256", new string('c', 64)))),
            Insert("maps", map, Row(("kind", "canvas"), ("name", "Owner's map"), ("settings", new JsonObject()), ("isTemplate", false)), mapBatch),
            Insert("map_shares", mapShare, Row(("mapId", map.ToString()), ("groupId", team.ToString()), ("access", "edit")), mapBatch),
            Insert("map_layers", layer, Row(("mapId", map.ToString()), ("name", "Base"), ("sortOrder", "a0"), ("isVisible", true), ("isLocked", false))),
            Insert("map_elements", element, Row(("mapId", map.ToString()), ("layerId", layer.ToString()), ("kind", "item"), ("z", "a0"), ("x", 0), ("y", 0),
                ("width", 100), ("height", 50), ("style", new JsonObject()), ("itemId", idea.ToString()))),
            Insert("files", mapImage, Row(("mapId", map.ToString()), ("purpose", "map_image"), ("fileName", "photo.png"), ("contentType", "image/png"),
                ("sizeBytes", 3), ("sha256", new string('d', 64)))),
            Insert("saved_views", view, Row(("itemType", "idea"), ("name", "Mine"), ("definition", new JsonObject()), ("isShared", false), ("sortOrder", 1))),
            Insert("user_settings", setting, Row(("key", "list.ideas"), ("value", new JsonObject { ["sort"] = "title" }))),
        ]);
        Assert.IsTrue(created.Arr("results").All(r => r.Str("status") == "accepted"), created["results"]!.ToJsonString());
        await ownerClient.SyncAllAsync();
        var owned = new (string Table, Guid Id)[]
        {
            ("items", idea), ("item_shares", itemShare), ("items", project), ("item_sections", itemSection), ("field_values", value),
            ("milestones", milestone), ("dod_items", dod), ("assignments", assignment), ("item_links", link), ("files", file), ("maps", map),
            ("map_shares", mapShare), ("map_layers", layer), ("map_elements", element), ("files", mapImage), ("saved_views", view),
            ("user_settings", setting), ("persons", person),
        };
        var before = await StateAsync(owned);

        var stranger = await UserAsync(app, "stranger");
        var strangerClient = new SyncClient(app.ClientFor(stranger, AllButPersonsAndConfiguration));
        await strangerClient.SyncAllAsync();
        var (myNote, myMap, myLayer) = (Ids.New(), Ids.New(), Ids.New());
        Assert.IsTrue((await strangerClient.SyncAllAsync(
        [
            Insert("items", myNote, Note("Stranger's note")),
            Insert("maps", myMap, Row(("kind", "canvas"), ("name", "Stranger's map"), ("settings", new JsonObject()), ("isTemplate", false))),
            Insert("map_layers", myLayer, Row(("mapId", myMap.ToString()), ("name", "Base"), ("sortOrder", "a0"), ("isVisible", true), ("isLocked", false))),
        ])).Arr("results").All(r => r.Str("status") == "accepted"));

        JsonObject Rev(string table, Guid id) => Patch(table, id, ownerClient.Row(table, id)?.Int("rev") ?? 1, ("x", 0, 1));
        JsonObject PatchOf(string table, Guid id, string key, JsonNode? old, JsonNode? @new) =>
            Patch(table, id, ownerClient.Row(table, id)?.Int("rev") ?? 1, (key, old, @new));
        JsonObject DeleteOf(string table, Guid id) => Delete(table, id, ownerClient.Row(table, id)?.Int("rev") ?? 1, force: true);
        var random = Ids.New();
        var cases = new List<(JsonObject Mutation, string Expected)>
        {
            // [S, ro] tables: no client writes at all.
            (Insert("groups", Ids.New(), Row(("name", "Mine"))), "not_supported"),
            (PatchOf("groups", team, "name", null, "Renamed"), "not_patchable"),
            (DeleteOf("groups", team), "not_supported"),
            (Insert("persons", Ids.New(), Row(("displayName", "Extern"))), "missing_right"),
            (PatchOf("persons", person, "notes", null, "x"), "missing_right"),
            (DeleteOf("persons", person), "missing_right"),
        };
        foreach (var table in new[]
                 {
                     "statuses", "sections", "field_definitions", "project_templates", "template_statuses", "template_sections", "template_field_defaults",
                     "template_milestones", "assignment_roles",
                 })
        {
            var configId = table switch { "statuses" => ideaStatus, "sections" => section, "field_definitions" => field.Id, _ => random };
            cases.Add((Insert(table, Ids.New(), Row(("name", "x"))), "not_supported"));
            cases.Add((PatchOf(table, configId, "name", null, "x"), "not_patchable"));
            cases.Add((DeleteOf(table, configId), "not_supported"));
        }
        cases.AddRange(
        [
            (Insert("saved_views", Ids.New(), Row(("itemType", "idea"), ("name", "Shared"), ("definition", new JsonObject()), ("isShared", true), ("sortOrder", 1))),
                "missing_right"),
            (PatchOf("saved_views", view, "name", "Mine", "Taken"), "missing_access"),
            (DeleteOf("saved_views", view), "missing_access"),
            // A project converted from the owner's idea: the origin must be readable.
            (Insert("items", Ids.New(), Item("project", "Stolen", projectStatus, new JsonObject { ["originItemId"] = idea.ToString() })), "validation_failed"),
            (PatchOf("items", idea, "title", "Owner's idea", "Mine now"), "missing_access"),
            (DeleteOf("items", idea), "missing_access"),
            (Insert("item_shares", Ids.New(), Share(idea, team, "manage")), "missing_access"),
            (PatchOf("item_shares", itemShare, "access", "edit", "manage"), "missing_access"),
            (DeleteOf("item_shares", itemShare), "missing_access"),
            (Insert("maps", Ids.New(), Row(("kind", "canvas"), ("name", "Template"), ("settings", new JsonObject()), ("isTemplate", true))), "missing_right"),
            (PatchOf("maps", map, "name", "Owner's map", "Mine"), "missing_access"),
            (DeleteOf("maps", map), "missing_access"),
            (Insert("map_shares", Ids.New(), Row(("mapId", map.ToString()), ("groupId", team.ToString()), ("access", "manage"))), "missing_access"),
            (PatchOf("map_shares", mapShare, "access", "edit", "manage"), "missing_access"),
            (DeleteOf("map_shares", mapShare), "missing_access"),
            (Insert("files", Ids.New(), Row(("itemId", idea.ToString()), ("purpose", "attachment"), ("fileName", "x.txt"), ("contentType", "text/plain"),
                ("sizeBytes", 3), ("sha256", new string('c', 64)))), "parent_missing"),
            (Insert("files", Ids.New(), Row(("mapId", map.ToString()), ("purpose", "map_image"), ("fileName", "x.png"), ("contentType", "image/png"),
                ("sizeBytes", 3), ("sha256", new string('d', 64)))), "parent_missing"),
            (PatchOf("files", file, "fileName", "plan.txt", "x.txt"), "missing_access"),
            (DeleteOf("files", file), "missing_access"),
            (Insert("item_sections", Ids.New(), Row(("itemId", project.ToString()), ("sectionId", section.ToString()), ("sortOrder", 1))), "parent_missing"),
            (PatchOf("item_sections", itemSection, "sortOrder", 10, 20), "missing_access"),
            (DeleteOf("item_sections", itemSection), "missing_access"),
            // A duplicate pair of a row the stranger can't see: refused before any merge.
            (Insert("field_values", Ids.New(), Row(("itemId", idea.ToString()), ("fieldId", field.Id.ToString()), ("value", "mine"))), "parent_missing"),
            (PatchOf("field_values", value, "value", "secret", "mine"), "missing_access"),
            (DeleteOf("field_values", value), "missing_access"),
            (Insert("milestones", Ids.New(), Row(("itemId", project.ToString()), ("kind", "milestone"), ("title", "M"), ("sortOrder", "a1"))), "parent_missing"),
            (PatchOf("milestones", milestone, "title", "G1", "Mine"), "missing_access"),
            (DeleteOf("milestones", milestone), "missing_access"),
            (Insert("dod_items", Ids.New(), Row(("milestoneId", milestone.ToString()), ("text", "X"), ("sortOrder", "a1"))), "parent_missing"),
            (PatchOf("dod_items", dod, "state", "open", "done"), "missing_access"),
            (DeleteOf("dod_items", dod), "missing_access"),
            (Insert("assignments", Ids.New(), Row(("itemId", project.ToString()), ("personId", person.ToString()), ("sortOrder", "a1"))), "parent_missing"),
            (Insert("assignments", Ids.New(), Row(("milestoneId", milestone.ToString()), ("personId", person.ToString()), ("sortOrder", "a1"))), "parent_missing"),
            (PatchOf("assignments", assignment, "role", null, "Sponsor"), "missing_access"),
            (DeleteOf("assignments", assignment), "missing_access"),
            // A link from one's own note to the owner's idea: the target must be readable.
            (Insert("item_links", Ids.New(), Row(("fromItemId", myNote.ToString()), ("toItemId", idea.ToString()), ("relation", "related"))), "parent_missing"),
            (Insert("item_links", Ids.New(), Row(("fromItemId", project.ToString()), ("toItemId", myNote.ToString()), ("relation", "related"))), "parent_missing"),
            (PatchOf("item_links", link, "relation", "related", "depends_on"), "missing_access"),
            (DeleteOf("item_links", link), "missing_access"),
            (Insert("map_layers", Ids.New(), Row(("mapId", map.ToString()), ("name", "Mine"), ("sortOrder", "a1"), ("isVisible", true), ("isLocked", false))),
                "parent_missing"),
            (PatchOf("map_layers", layer, "name", "Base", "Mine"), "missing_access"),
            (DeleteOf("map_layers", layer), "missing_access"),
            (Insert("map_elements", Ids.New(), Row(("mapId", map.ToString()), ("layerId", layer.ToString()), ("kind", "rect"), ("z", "a1"), ("x", 0), ("y", 0),
                ("width", 10), ("height", 10), ("style", new JsonObject()))), "parent_missing"),
            // A card of the owner's idea on one's own map: the item must be readable.
            (Insert("map_elements", Ids.New(), Row(("mapId", myMap.ToString()), ("layerId", myLayer.ToString()), ("kind", "item"), ("z", "a0"), ("x", 0),
                ("y", 0), ("width", 10), ("height", 10), ("style", new JsonObject()), ("itemId", idea.ToString()))), "parent_missing"),
            (Rev("map_elements", element), "missing_access"),
            (DeleteOf("map_elements", element), "missing_access"),
            (PatchOf("user_settings", setting, "value", new JsonObject { ["sort"] = "title" }, new JsonObject()), "missing_access"),
            (DeleteOf("user_settings", setting), "missing_access"),
        ]);
        var answer = await strangerClient.SyncAllAsync(cases.Select(c => c.Mutation));
        var results = answer.Arr("results");
        var wrong = cases.Select((c, i) => (c, r: results[i]!))
            .Where(x => x.r.Str("status") != "rejected" || x.r.Str("code") != x.c.Expected)
            .Select(x => $"{x.c.Mutation.Str("op")} {x.c.Mutation.Str("table")}: {x.r.Str("status")} {x.r.Str("code")}, expected {x.c.Expected}").ToList();
        Assert.AreEqual("", string.Join("\n", wrong));
        // No refusal hands out the hidden row.
        Assert.IsTrue(results.All(r => r!["current"] is null), "a refusal of a row one can't see carries no current");
        // Every table had its insert, patch and delete.
        var missing = SyncTables.All.Select(t => t.Name).Where(name =>
            new[] { "insert", "patch", "delete" }.Any(op => !cases.Any(c => c.Mutation.Str("table") == name && c.Mutation.Str("op") == op))).ToList();
        CollectionAssert.AreEqual(new[] { "user_settings" }, missing, "every table but user_settings' insert (its own row, below) is covered");

        // A setting with the owner's key is the stranger's own row; the owner's stays as it was.
        var mine = Ids.New();
        var own = await strangerClient.SyncAllAsync([Insert("user_settings", mine, Row(("key", "list.ideas"), ("value", new JsonObject { ["sort"] = "x" })))]);
        Assert.AreEqual("accepted", own.Arr("results").Single().Str("status"));
        Assert.IsNull(own.Arr("results").Single().Str("mergedInto"));

        CollectionAssert.AreEqual(before, await StateAsync(owned), "nothing of the owner's changed");
        await strangerClient.SyncAllAsync();
        var seen = owned.Where(o => o.Table != "persons" && strangerClient.Row(o.Table, o.Id) is not null).Select(o => $"{o.Table} {o.Id}").ToList();
        Assert.AreEqual("", string.Join(", ", seen), "the stranger pulls none of the owner's rows");
        await using var db = app.NewDb();
        Assert.IsNull(await db.Items.Where(i => i.Id == idea).Select(i => i.ConvertedToItemId).SingleAsync(), "the origin isn't marked converted");
    }

    /// <summary>
    /// Server-owned columns are the server's whatever an insert sends (docs/03 §11.4): an item's owner, number and conversion, a
    /// person's user and source, a file's status and storage path – a path out of the files folder included.
    /// </summary>
    [TestMethod]
    public async Task AnInsertNeverSetsWhatTheServerOwns()
    {
        var victim = await UserAsync(app, "ownedvictim");
        var user = await UserAsync(app, "owneduser");
        var client = new SyncClient(app.ClientFor(user, Everything));
        var (note, person, file) = (Ids.New(), Ids.New(), Ids.New());
        var answer = await client.SyncAllAsync(
        [
            Insert("items", note, Row(("type", "note"), ("title", "Mine"), ("tags", new JsonArray()), ("ownerUserId", victim.ToString()), ("number", 1),
                ("convertedToItemId", Ids.New().ToString()), ("access", "manage"))),
            Insert("persons", person, Row(("displayName", "Impostor"), ("userId", victim.ToString()), ("source", "microsoft"),
                ("entraObjectId", Ids.New().ToString()))),
            Insert("files", file, Row(("itemId", note.ToString()), ("purpose", "attachment"), ("fileName", "passwd"), ("contentType", "text/plain"),
                ("sizeBytes", 3), ("sha256", new string('f', 64)), ("status", "available"), ("storagePath", "../../../etc/passwd"))),
        ]);
        Assert.IsTrue(answer.Arr("results").All(r => r.Str("status") == "accepted"), answer["results"]!.ToJsonString());
        await using var db = app.NewDb();
        var item = await db.Items.AsNoTracking().SingleAsync(i => i.Id == note);
        Assert.AreEqual(user, item.OwnerUserId);
        Assert.IsNull(item.ConvertedToItemId);
        var stored = await db.Persons.AsNoTracking().SingleAsync(p => p.Id == person);
        Assert.IsNull(stored.UserId);
        Assert.AreEqual(Persons.PersonSource.Manual, stored.Source);
        Assert.IsNull(stored.EntraObjectId);
        var row = await db.Files.AsNoTracking().SingleAsync(f => f.Id == file);
        Assert.AreEqual(Files.FileStatus.Pending, row.Status);
        Assert.IsNull(row.StoragePath);
        using var download = await client.Http.GetAsync($"/api/v1/files/{file}/passwd");
        Assert.AreEqual(HttpStatusCode.NotFound, download.StatusCode);
        // And no patch reaches them.
        var patched = await client.SyncAllAsync([Patch("items", note, 1, ("ownerUserId", user.ToString(), victim.ToString())),
            Patch("files", file, 1, ("storagePath", null, "../x"))]);
        CollectionAssert.AreEqual(new[] { "not_patchable", "not_patchable" }, Codes(patched));
    }

    /// <summary>
    /// An attachment that merely says image/png (its bytes never checked) lends its bytes to attachments only: a cover or an
    /// embedded image of the same bytes needs an upload of its own, which the image check sees (docs/06 S-07).
    /// </summary>
    [TestMethod]
    public async Task AnAttachmentNeverLendsItsBytesToAnImage()
    {
        var client = new SyncClient(app.ClientFor(await UserAsync(app, "fakeimage"), Everything));
        var bytes = "<svg onload=alert(1)>"u8.ToArray();
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        JsonObject File(Guid item, string purpose, string name) => Row(("itemId", item.ToString()), ("purpose", purpose), ("fileName", name),
            ("contentType", "image/png"), ("sizeBytes", bytes.Length), ("sha256", sha));
        var (note, attachment, copy, cover, embedded) = (Ids.New(), Ids.New(), Ids.New(), Ids.New(), Ids.New());
        await client.SyncAllAsync([Insert("items", note, Note("Fake image")), Insert("files", attachment, File(note, "attachment", "x.png"))]);
        (await client.Http.PutAsync($"/api/v1/files/{attachment}/chunks/0", new ByteArrayContent(bytes))).Dispose();
        using (var committed = await client.Http.PostJsonNodeAsync($"/api/v1/files/{attachment}/commit",
                   new JsonObject { ["sha256"] = sha, ["size"] = bytes.Length }))
        {
            Assert.AreEqual(HttpStatusCode.OK, committed.StatusCode, await committed.Content.ReadAsStringAsync());
        }
        await client.SyncAllAsync([Insert("files", copy, File(note, "attachment", "y.png")), Insert("files", cover, File(note, "cover", "c.png")),
            Insert("files", embedded, File(note, "embedded", "e.png"))]);
        Assert.AreEqual("available", client.Row("files", copy).Str("status"), "attachments share");
        Assert.AreEqual("pending", client.Row("files", cover).Str("status"));
        Assert.AreEqual("pending", client.Row("files", embedded).Str("status"));
    }

    /// <summary>Each row's rev and deleted flag, as the database has them.</summary>
    private static async Task<List<string>> StateAsync(IEnumerable<(string Table, Guid Id)> rows)
    {
        await using var db = app.NewDb();
        var state = new List<string>();
        foreach (var (table, id) in rows)
        {
            var row = await SyncTables.Get(table).FindAsync(db, id, CancellationToken.None);
            state.Add($"{table} {id}: rev {row?.Rev} deleted {row?.Deleted}");
        }
        return state;
    }

    /// <summary>
    /// A section only the board may view (docs/02 §7.3): its item sections, values, field definitions and template defaults never
    /// reach a colleague who reads the idea – not by the pull, not by the History (docs/06 S-01) – and do reach a board member.
    /// </summary>
    [TestMethod]
    public async Task AHiddenSectionNeverReachesOthersByPullOrHistory()
    {
        var team = await GroupAsync(app, "hidteam");
        var board = await GroupAsync(app, "hidboard");
        var owner = await UserAsync(app, "hidowner", team);
        var colleague = await UserAsync(app, "hidcolleague", team);
        var member = await UserAsync(app, "hidmember", team, board);
        var hidden = await SectionAsync(app, ["idea", "project"], (board, SectionAccess.View));
        var open = await SectionAsync(app, ["idea"]);
        var secret = await FieldAsync(app, hidden, FieldType.Text);
        var visible = await FieldAsync(app, open, FieldType.Text);
        var template = await TemplateAsync(app);
        await using (var db = app.NewDb())
        {
            db.TemplateFieldDefaults.Add(new TemplateFieldDefaultEntity { Id = Ids.New(), TemplateId = template, FieldId = secret.Id, Value = "\"default\"" });
            await db.SaveChangesAsync();
        }
        var ownerClient = new SyncClient(app.ClientFor(owner, Everything));
        var memberClient = new SyncClient(app.ClientFor(member, Everything));
        var colleagueClient = new SyncClient(app.ClientFor(colleague, Everything));
        var (idea, hiddenSection, hiddenValue, openValue) = (Ids.New(), Ids.New(), Ids.New(), Ids.New());
        var batch = Ids.New();
        await ownerClient.SyncAllAsync([Insert("items", idea, Item("idea", "Assessed", await StatusAsync(app, ItemType.Idea)), batch),
            Insert("item_shares", Ids.New(), Share(idea, team, "edit"), batch),
            Insert("field_values", openValue, Row(("itemId", idea.ToString()), ("fieldId", visible.Id.ToString()), ("value", "open")))]);
        var byMember = await memberClient.SyncAllAsync(
        [
            Insert("item_sections", hiddenSection, Row(("itemId", idea.ToString()), ("sectionId", hidden.ToString()), ("sortOrder", 10))),
            Insert("field_values", hiddenValue, Row(("itemId", idea.ToString()), ("fieldId", secret.Id.ToString()), ("value", "Compliance: high risk"))),
        ]);
        Assert.IsTrue(byMember.Arr("results").All(r => r.Str("status") == "accepted"), byMember["results"]!.ToJsonString());
        // The colleague may edit the idea, not see the section: a value pushed into it is refused as if the field didn't exist.
        var refused = await colleagueClient.SyncAllAsync([Insert("field_values", Ids.New(), Row(("itemId", idea.ToString()), ("fieldId", secret.Id.ToString()),
            ("value", "guess")))]);
        Assert.AreEqual("parent_missing", Codes(refused)[0]);
        await memberClient.SyncAllAsync([Patch("field_values", hiddenValue, 1, ("value", "Compliance: high risk", "Compliance: limited risk"))]);

        await colleagueClient.SyncAllAsync();
        await memberClient.SyncAllAsync();
        Assert.IsNotNull(colleagueClient.Row("items", idea));
        Assert.IsNotNull(colleagueClient.Row("field_values", openValue));
        Assert.IsNull(colleagueClient.Row("item_sections", hiddenSection));
        Assert.IsNull(colleagueClient.Row("field_values", hiddenValue));
        Assert.IsNull(colleagueClient.Row("sections", hidden));
        Assert.IsNull(colleagueClient.Row("field_definitions", secret.Id));
        Assert.IsFalse(colleagueClient.Rows("template_field_defaults").Any(d => d.Str("fieldId") == secret.Id.ToString()));
        Assert.IsNotNull(memberClient.Row("item_sections", hiddenSection));
        Assert.AreEqual("Compliance: limited risk", memberClient.Row("field_values", hiddenValue).Str("value"));
        Assert.IsTrue(memberClient.Rows("template_field_defaults").Any(d => d.Str("fieldId") == secret.Id.ToString()));

        async Task<string> HistoryAsync(SyncClient client)
        {
            using var response = await client.Http.GetAsync($"/api/v1/history?itemId={idea}&pageSize=200");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }
        var theirs = await HistoryAsync(colleagueClient);
        Assert.DoesNotContain("high risk", theirs);
        Assert.DoesNotContain("limited risk", theirs);
        Assert.DoesNotContain(hiddenSection.ToString(), theirs);
        Assert.DoesNotContain(hiddenValue.ToString(), theirs);
        StringAssert.Contains(theirs, openValue.ToString(), "the visible section's values stay in the History");
        var boards = await HistoryAsync(memberClient);
        StringAssert.Contains(boards, "limited risk");
        StringAssert.Contains(boards, hiddenSection.ToString());
        StringAssert.Contains(await HistoryAsync(new SyncClient(app.Client(TestUsers.Admin))), "limited risk");
    }

    /// <summary>
    /// A user who only reads an idea never patches it through a duplicate pair: the insert of an existing (item, field), (item,
    /// section) or (item, person) is refused before it would merge (docs/03 §11.5).
    /// </summary>
    [TestMethod]
    public async Task ADuplicatePairNeverPatchesARowTheUserMayNotEdit()
    {
        var team = await GroupAsync(app, "dupteam");
        var owner = await UserAsync(app, "dupowner", team);
        var reader = await UserAsync(app, "dupreader", team);
        var ownerClient = new SyncClient(app.ClientFor(owner, Everything));
        var readerClient = new SyncClient(app.ClientFor(reader, Everything));
        await ownerClient.SyncAllAsync();
        var person = ownerClient.Rows("persons").Single(p => p.Str("userId") == owner.ToString()).Str("id")!;
        var section = await SectionAsync(app, ["idea"]);
        var field = await FieldAsync(app, section, FieldType.Text);
        var (idea, value) = (Ids.New(), Ids.New());
        var batch = Ids.New();
        await ownerClient.SyncAllAsync(
        [
            Insert("items", idea, Item("idea", "Read only", await StatusAsync(app, ItemType.Idea)), batch),
            Insert("item_shares", Ids.New(), Share(idea, team, "read"), batch),
            Insert("item_sections", Ids.New(), Row(("itemId", idea.ToString()), ("sectionId", section.ToString()), ("sortOrder", 10))),
            Insert("field_values", value, Row(("itemId", idea.ToString()), ("fieldId", field.Id.ToString()), ("value", "owner's"))),
            Insert("assignments", Ids.New(), Row(("itemId", idea.ToString()), ("personId", person), ("role", "Owner"), ("sortOrder", "a0"))),
        ]);
        await readerClient.SyncAllAsync();
        Assert.AreEqual("read", readerClient.Row("items", idea).Str("access"));
        var answer = await readerClient.SyncAllAsync(
        [
            Insert("field_values", Ids.New(), Row(("itemId", idea.ToString()), ("fieldId", field.Id.ToString()), ("value", "reader's"))),
            Insert("item_sections", Ids.New(), Row(("itemId", idea.ToString()), ("sectionId", section.ToString()), ("sortOrder", 1))),
            Insert("assignments", Ids.New(), Row(("itemId", idea.ToString()), ("personId", person), ("role", "Reader"), ("sortOrder", "a1"))),
            Insert("item_links", Ids.New(), Row(("fromItemId", idea.ToString()), ("toItemId", idea.ToString()), ("relation", "related"))),
        ]);
        CollectionAssert.AreEqual(new[] { "missing_access", "missing_access", "missing_access", "missing_access" }, Codes(answer),
            answer["results"]!.ToJsonString());
        await using var db = app.NewDb();
        Assert.AreEqual("\"owner's\"", await db.FieldValues.Where(v => v.Id == value).Select(v => v.Value).SingleAsync());
        Assert.AreEqual(1, await db.FieldValues.Where(v => v.Id == value).Select(v => v.Rev).SingleAsync());
        Assert.AreEqual("Owner", await db.Assignments.Where(a => a.ItemId == idea).Select(a => a.Role).SingleAsync());
    }

    /// <summary>
    /// The Trash and the History of a foreign item, and its files: a stranger lists none of it, restores and purges nothing, reads
    /// no History, downloads, uploads and commits nothing (404: the file doesn't exist for them). Another user's deleted setting
    /// isn't theirs to restore either (docs/06 S-05).
    /// </summary>
    [TestMethod]
    public async Task TheTrashHistoryAndFilesOfForeignItemsAreRefused()
    {
        var owner = await UserAsync(app, "trashowner");
        var ownerClient = new SyncClient(app.ClientFor(owner, Everything));
        var strangerClient = new SyncClient(app.ClientFor(await UserAsync(app, "trashstranger"), Everything));
        var (note, map, file, setting) = (Ids.New(), Ids.New(), Ids.New(), Ids.New());
        var bytes = "abc"u8.ToArray();
        await ownerClient.SyncAllAsync(
        [
            Insert("items", note, Note("Foreign")),
            Insert("maps", map, Row(("kind", "canvas"), ("name", "Foreign map"), ("settings", new JsonObject()), ("isTemplate", false))),
            Insert("files", file, Row(("itemId", note.ToString()), ("purpose", "attachment"), ("fileName", "a.txt"), ("contentType", "text/plain"),
                ("sizeBytes", bytes.Length), ("sha256", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))))),
            Insert("user_settings", setting, Row(("key", "theme"), ("value", "dark"))),
        ]);
        using (var chunk = await ownerClient.Http.PutAsync($"/api/v1/files/{file}/chunks/0", new ByteArrayContent(bytes)))
        {
            Assert.AreEqual(HttpStatusCode.OK, chunk.StatusCode);
        }
        using (var stolen = await strangerClient.Http.PostJsonNodeAsync($"/api/v1/files/{file}/commit",
                   new JsonObject { ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), ["size"] = bytes.Length }))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, stolen.StatusCode, "another user's upload isn't committed by a stranger");
        }
        using (var restart = await strangerClient.Http.PutAsync($"/api/v1/files/{file}/chunks/0", new ByteArrayContent(bytes)))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, restart.StatusCode);
        }
        using (var committed = await ownerClient.Http.PostJsonNodeAsync($"/api/v1/files/{file}/commit",
                   new JsonObject { ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), ["size"] = bytes.Length }))
        {
            Assert.AreEqual(HttpStatusCode.OK, committed.StatusCode, await committed.Content.ReadAsStringAsync());
        }
        using (var download = await strangerClient.Http.GetAsync($"/api/v1/files/{file}/a.txt"))
        {
            Assert.AreEqual(HttpStatusCode.NotFound, download.StatusCode);
        }
        using (var history = await strangerClient.Http.GetAsync($"/api/v1/history?itemId={note}"))
        {
            Assert.AreEqual(HttpStatusCode.Forbidden, history.StatusCode);
        }

        await ownerClient.SyncAllAsync([Delete("items", note, 1), Delete("maps", map, 1), Delete("user_settings", setting, 1)]);
        await using var db = app.NewDb();
        var batches = await db.DeleteBatches.AsNoTracking().Where(b => b.RootId == note || b.RootId == map || b.RootId == setting)
            .ToDictionaryAsync(b => b.RootId, b => b.Id);
        Assert.HasCount(3, batches);
        var trash = await (await strangerClient.Http.GetAsync("/api/v1/trash?pageSize=200")).JsonAsync();
        Assert.IsFalse(trash.Arr("items").Any(e => e.Str("rootId") == note.ToString() || e.Str("rootId") == map.ToString()));
        foreach (var (root, batch) in batches)
        {
            using var restore = await strangerClient.Http.PostEmptyAsync($"/api/v1/trash/{batch}/restore");
            Assert.AreEqual(HttpStatusCode.Forbidden, restore.StatusCode, $"restore of {root}");
            using var purge = await strangerClient.Http.DeleteAsync($"/api/v1/trash/{batch}");
            Assert.AreEqual(HttpStatusCode.Forbidden, purge.StatusCode, "purging is the admins'");
        }
        Assert.IsTrue(await db.Items.AnyAsync(i => i.Id == note && i.Deleted));
        Assert.IsTrue(await db.UserSettings.AnyAsync(s => s.Id == setting && s.Deleted), "another user's setting stays deleted");
        // The owner restores their own setting.
        using var mine = await ownerClient.Http.PostEmptyAsync($"/api/v1/trash/{batches[setting]}/restore");
        Assert.AreEqual(HttpStatusCode.OK, mine.StatusCode);
    }
}
