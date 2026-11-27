using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Config;
using PGPM.Files;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Tests.Content;
using PGPM.Tests.Sync;
using static PGPM.Tests.Mcp.McpKit;
using static PGPM.Tests.Mcp.McpToolKit;

namespace PGPM.Tests.Mcp;

/// <summary>
/// <c>convert_to_project</c> (docs/03 §7.3): what the browser's conversion carries over (docs/01 §8, PGPM.WebUI/src/local/
/// items.ts) – shares, cover and attachments sharing the stored bytes, the sections and values that apply to projects and that
/// the user may edit, the template, persons, links, the source's new status and its link to the project.
/// </summary>
[TestClass]
public sealed class McpConvertToolTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => app = new TestApp();

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    [TestMethod]
    public async Task AConversionCarriesWhatTheBrowsersCarries()
    {
        var (team, former, board) = (await SyncKit.GroupAsync(app, "mcp-conv"), await SyncKit.GroupAsync(app, "mcp-conv-old"), await SyncKit.GroupAsync(app, "mcp-conv-board"));
        var (user, read, write) = await WriterAsync(app, "converter", team, former);
        var colleague = await MemberAsync(app, "conv-colleague", team, former);
        var ideaOpen = await ContentKit.StatusAsync(app, ItemType.Idea);
        Guid selected;
        await using (var db = app.NewDb())
        {
            var status = new StatusEntity { Id = Ids.New(), ItemType = ItemType.Idea, Name = "Selected", Category = StatusCategory.Closed };
            db.Statuses.Add(status);
            await db.SaveChangesAsync();
            selected = status.Id;
        }
        var running = await ContentKit.StatusAsync(app, ItemType.Project, StatusCategory.Active);
        var both = await ContentKit.SectionAsync(app, ["idea", "project"]);
        var (_, value) = await ContentKit.FieldAsync(app, both, FieldType.Number);
        var (_, rating) = await ContentKit.FieldAsync(app, both, FieldType.Rating, new JsonObject { ["max"] = 5 });
        var ideasOnly = await ContentKit.SectionAsync(app, ["idea"]);
        var (_, pitch) = await ContentKit.FieldAsync(app, ideasOnly, FieldType.Text);
        var locked = await ContentKit.SectionAsync(app, ["idea", "project"], (board, SectionAccess.Edit));
        var (lockedId, verdict) = await ContentKit.FieldAsync(app, locked, FieldType.Text);
        var projectSection = await ContentKit.SectionAsync(app, ["project"]);
        var template = await ContentKit.TemplateAsync(app, running);
        await using (var db = app.NewDb())
        {
            db.TemplateSections.Add(new TemplateSectionEntity { Id = Ids.New(), TemplateId = template, SectionId = projectSection, SortOrder = 10 });
            db.TemplateMilestones.Add(new TemplateMilestoneEntity
            {
                Id = Ids.New(), TemplateId = template, Kind = MilestoneKind.Gate, Title = "G1 MVP", OffsetDays = 14, Dod = """["MVP live"]""", SortOrder = "a0",
            });
            await db.SaveChangesAsync();
        }

        var idea = await ItemAsync(app, user, "idea", "Ticket triage", ideaOpen, "manage", team);
        await using var client = await ClientAsync(app, write);
        await OkAsync(client, "update_item", new
        {
            id = idea.ToString(), description = "Sort the tickets by urgency", color = "#336699",
            fields = new Dictionary<string, object> { [value] = 42, [rating] = 4, [pitch] = "Saves hours" },
        });
        await AdminSyncAsync(app, Value(idea, lockedId, "Approved"));
        var attachment = await UploadAsync(app, user, idea, "attachment", "notes.txt", "text/plain", Encoding.UTF8.GetBytes("The pilot's notes"));
        var cover = await UploadAsync(app, user, idea, "cover", "cover.png", "image/png", FileTests.Png(2, 2));
        await using (var db = app.NewDb())
        {
            var rev = await db.Items.Where(i => i.Id == idea).Select(i => i.Rev).SingleAsync();
            await SyncAsync(app, user, SyncKit.Patch("items", idea, rev, ("imageFileId", null, cover.ToString())));
        }
        await OkAsync(client, "assign_person", new { item = idea.ToString(), person = "me", role = "Owner" });
        var learning = await ItemAsync(app, colleague, "note", "A learning", null, "read", team);
        var gone = await ItemAsync(app, colleague, "note", "Shared no longer", null, "read", former);
        await OkAsync(client, "link_items", new { from = idea.ToString(), to = learning.ToString(), relation = "learning_of" });
        await OkAsync(client, "link_items", new { from = idea.ToString(), to = gone.ToString() });
        await using (var db = app.NewDb())
        {
            await db.UserGroups.Where(m => m.UserId == user && m.GroupId == former).ExecuteDeleteAsync();
        }
        app.Services.GetRequiredService<SessionCache>().Forget(user);

        await using (var reader = await ClientAsync(app, read))
        {
            Assert.AreEqual("not_available", (await ErrorAsync(reader, "convert_to_project", new { id = idea.ToString() })).Code());
        }
        var project = await OkAsync(client, "convert_to_project", new { id = idea.ToString(), template = template.ToString(), startDate = "2027-05-01" });
        var id = Guid.Parse(project.S("id")!);
        Assert.AreEqual(("project", "Ticket triage", "#336699", running.ToString()),
            (project.S("type"), project.S("title"), project.S("color"), project.GetProperty("status").S("id")));
        Assert.AreEqual(idea.ToString(), project.GetProperty("origin").S("id"));
        var copied = project.GetProperty("copied");
        Assert.AreEqual((1, 1, 2, "Selected"), (copied.GetProperty("shares").GetInt32(), copied.GetProperty("links").GetInt32(), copied.GetProperty("files").GetInt32(),
            copied.S("sourceStatus")), "the link to a note the user no longer reads stays behind");
        Assert.AreEqual("2027-05-15", project.Each("milestones").Single().S("dueDate"));
        Assert.AreEqual(42, FieldOf(project, value)!.Value.GetProperty("value").GetInt32());
        Assert.AreEqual(4, FieldOf(project, rating)!.Value.GetProperty("value").GetInt32());
        Assert.IsNull(FieldOf(project, pitch), "an ideas-only section doesn't come along");
        Assert.IsTrue(FieldOf(project, verdict) is null || FieldOf(project, verdict)!.Value.Missing("value"), "nor a value the user may not edit");
        CollectionAssert.IsSubsetOf(new[] { both.ToString(), projectSection.ToString() }, project.Each("sections").Select(s => s.S("id")).ToList());
        Assert.AreEqual("Owner", project.Each("persons").Single().S("role"));
        var links = project.Each("links").Where(l => l.S("direction") == "out").Select(l => (l.S("relation"), l.GetProperty("item").S("id"))).ToList();
        CollectionAssert.AreEquivalent(new[] { ("learning_of", (string?)learning.ToString()), ("related", (string?)idea.ToString()) }, links);
        var share = project.Each("shares").Single();
        Assert.AreEqual((team.ToString(), "edit"), (share.S("groupId"), share.S("access")), "the source's shares, at most edit");

        await using (var db = app.NewDb())
        {
            var source = await db.Items.SingleAsync(i => i.Id == idea);
            Assert.AreEqual((id, selected), (source.ConvertedToItemId!.Value, source.StatusId!.Value));
            var originals = await db.Files.Where(f => f.ItemId == idea).ToDictionaryAsync(f => f.Id);
            var copies = await db.Files.Where(f => f.ItemId == id).ToListAsync();
            Assert.AreEqual(2, copies.Count);
            Assert.IsTrue(copies.All(c => c.Status == FileStatus.Available && originals.Values.Any(o => o.StoragePath == c.StoragePath && o.Sha256 == c.Sha256)),
                "the copies share the stored bytes");
            var projectRow = await db.Items.SingleAsync(i => i.Id == id);
            Assert.AreEqual(FilePurpose.Cover, copies.Single(c => c.Id == projectRow.ImageFileId).Purpose, "the cover's copy is the project's cover");
            Assert.IsFalse(await db.FieldValues.AnyAsync(v => v.ItemId == id && v.FieldId == lockedId));
            Assert.IsTrue(await db.AuditLog.AnyAsync(l => l.Event == AuditEvents.McpItemConverted && l.SubjectId == id && l.Actor == AuditActor.Mcp));
            var rows = copies.Select(c => c.Id).Append(id).ToList();
            Assert.AreEqual(1, await db.SyncMutations.Where(m => rows.Contains(m.RowId)).Select(m => m.BatchId).Distinct().CountAsync(), "one batch");
        }
        var again = await ErrorAsync(client, "convert_to_project", new { id = idea.ToString() });
        Assert.AreEqual(("already_converted", id.ToString()), (again.Code(), again.GetProperty("project").S("id")));

        // Converting needs Projects: Create besides edit on the source.
        var ideasOnlyRights = await GroupWithRightsAsync(app, "mcp-no-projects", (Area.Ideas, Level.Create), (Area.Mcp, Level.Use));
        var limited = await SyncKit.UserAsync(app, "no-projects", ideasOnlyRights, team);
        await SecondFactorAsync(app, limited);
        await using var limitedClient = await ClientAsync(app, (await TurnOnAsync(app, limited, "write")).Value!);
        Assert.IsFalse((await limitedClient.ListToolsAsync()).Any(t => t.Name == "convert_to_project"));
        var other = await ItemAsync(app, user, "idea", "Another", ideaOpen, "edit", team);
        Assert.AreEqual("not_available", (await ErrorAsync(limitedClient, "convert_to_project", new { id = other.ToString() })).Code());
    }
}
