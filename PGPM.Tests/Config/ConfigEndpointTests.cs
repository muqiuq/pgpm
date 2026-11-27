using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PGPM.Auth;
using PGPM.Data;
using PGPM.Lib.Helper;
using PGPM.Tests.Sync;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Config;

/// <summary>
/// The configuration's admin endpoints (docs/03 §3.3), on a database of their own (a section's access resets every device):
/// statuses with one default per type, sections and who may see them, fields with their scores' validation, templates with
/// everything below them, roles – and every change reaches the devices through the sync.
/// </summary>
[TestClass]
public sealed class ConfigEndpointTests
{
    private static TestApp app = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        var connection = TestDefaults.CreateDatabase("pgpm_test_config");
        using (var db = PgpmDbContext.Create(connection))
        {
            DatabaseMigrator.Migrate(db, NullLogger.Instance);
            TestUsers.Seed(db);
        }
        app = new TestApp(new Dictionary<string, string?> { ["Database:ConnectionString"] = connection });
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static HttpClient Manager() => app.ClientWithRights("Configuration:Manage");

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string url, JsonNode? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        }
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(expected, response.StatusCode, text);
        return text.Length == 0 ? [] : JsonNode.Parse(text) is JsonObject o ? o : new JsonObject { ["list"] = JsonNode.Parse(text) };
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static JsonObject Status(string type, string name, bool? isDefault = null) => new()
    {
        ["itemType"] = type, ["name"] = name, ["color"] = "#64748b", ["category"] = "open", ["isDefault"] = isDefault,
    };

    [TestMethod]
    public async Task StatusesKeepOneDefaultPerTypeAndReachTheDevices()
    {
        var m = Manager();
        var a = await SendAsync(m, HttpMethod.Post, "/api/v1/config/statuses", Status("lead", $"First {Tag()}"));
        var leadDefault = a.Bool("isDefault");
        var b = await SendAsync(m, HttpMethod.Post, "/api/v1/config/statuses", Status("lead", $"Second {Tag()}", true));
        Assert.IsTrue(b.Bool("isDefault"));
        await SendAsync(m, HttpMethod.Post, $"/api/v1/config/statuses/{b.Str("id")}/archive", expected: HttpStatusCode.Conflict);
        var update = Status("lead", a.Str("name")!, true);
        await SendAsync(m, HttpMethod.Put, $"/api/v1/config/statuses/{a.Str("id")}", update);
        var archived = await SendAsync(m, HttpMethod.Post, $"/api/v1/config/statuses/{b.Str("id")}/archive");
        Assert.IsTrue(archived.Bool("isArchived"));
        await SendAsync(m, HttpMethod.Post, "/api/v1/config/statuses", Status("lead", a.Str("name")!.ToUpperInvariant()), HttpStatusCode.Conflict);
        await SendAsync(m, HttpMethod.Post, "/api/v1/config/statuses", Status("note", "Notes have none"), HttpStatusCode.BadRequest);
        await using (var db = app.NewDb())
        {
            var leads = await db.Statuses.Where(s => s.ItemType == Items.ItemType.Lead && !s.Deleted).OrderByDescending(s => s.SortOrder).Select(s => s.Id).ToListAsync();
            var ordered = await SendAsync(m, HttpMethod.Put, "/api/v1/config/statuses/order",
                new JsonObject { ["itemType"] = "lead", ["ids"] = new JsonArray([.. leads.Select(id => (JsonNode)id.ToString())]) });
            Assert.AreEqual(leads[0].ToString(), ordered["list"]![0].Str("id"));
            Assert.AreEqual(1, await db.Statuses.CountAsync(s => s.ItemType == Items.ItemType.Lead && s.IsDefault));
        }
        var device = new SyncClient(app.Client(TestUsers.Member));
        await device.SyncAllAsync();
        Assert.IsTrue(device.Row("statuses", Guid.Parse(a.Str("id")!))!.Bool("isDefault"));
        Assert.IsTrue(device.Row("statuses", Guid.Parse(b.Str("id")!))!.Bool("isArchived"));
        _ = leadDefault;
    }

    [TestMethod]
    public async Task ASectionsAccessDecidesWhoSeesItAndResetsWhomItConcerns()
    {
        var m = Manager();
        var member = new SyncClient(app.Client(TestUsers.Member));
        var viewer = new SyncClient(app.Client(TestUsers.Viewer));
        await member.SyncAllAsync();
        await viewer.SyncAllAsync();
        var section = await SendAsync(m, HttpMethod.Post, "/api/v1/config/sections", new JsonObject
        {
            ["name"] = $"Board {Tag()}", ["appliesTo"] = new JsonArray("idea"), ["defaultFor"] = new JsonArray("idea"),
            ["access"] = new JsonArray(new JsonObject { ["groupId"] = GroupEntity.ViewersId.ToString(), ["level"] = "edit" }),
        });
        var id = Guid.Parse(section.Str("id")!);
        Assert.AreEqual("edit", section.Arr("access")[0].Str("level"));
        // Edit rows only: everybody sees it, the viewers edit it.
        await member.SyncAllAsync();
        await viewer.SyncAllAsync();
        Assert.IsFalse(member.Row("sections", id)!.Bool("canEdit"));
        Assert.IsTrue(viewer.Row("sections", id)!.Bool("canEdit"));
        // View rows: only the viewers see it any more – the others' devices reset and lose it.
        var resets = member.Resets;
        await SendAsync(m, HttpMethod.Put, $"/api/v1/config/sections/{id}", new JsonObject
        {
            ["name"] = section.Str("name"), ["appliesTo"] = new JsonArray("idea"),
            ["access"] = new JsonArray(new JsonObject { ["groupId"] = GroupEntity.ViewersId.ToString(), ["level"] = "view" }),
        });
        await member.SyncAllAsync();
        Assert.AreEqual(resets + 1, member.Resets, "an access epoch reset");
        Assert.IsNull(member.Row("sections", id));
        await viewer.SyncAllAsync();
        Assert.IsNotNull(viewer.Row("sections", id));
        await SendAsync(m, HttpMethod.Post, "/api/v1/config/sections",
            new JsonObject { ["name"] = "Bad", ["appliesTo"] = new JsonArray("lead") }, HttpStatusCode.BadRequest);
        await SendAsync(m, HttpMethod.Post, "/api/v1/config/sections",
            new JsonObject { ["name"] = "Bad", ["appliesTo"] = new JsonArray("idea"), ["defaultFor"] = new JsonArray("project") }, HttpStatusCode.BadRequest);
    }

    private static async Task<Guid> SectionAsync()
    {
        var section = await SendAsync(Manager(), HttpMethod.Post, "/api/v1/config/sections",
            new JsonObject { ["name"] = $"Fields {Tag()}", ["appliesTo"] = new JsonArray("idea", "project") });
        return Guid.Parse(section.Str("id")!);
    }

    private static JsonObject Field(Guid section, string key, string type, JsonObject? config = null, JsonNode? defaultValue = null) => new()
    {
        ["sectionId"] = section.ToString(), ["key"] = key, ["label"] = key, ["type"] = type, ["config"] = config ?? [], ["defaultValue"] = defaultValue,
    };

    [TestMethod]
    public async Task FieldsAreValidatedAndScoresNeverFormCycles()
    {
        var m = Manager();
        var section = await SectionAsync();
        var t = Tag();
        var (r1, r2, s1, s2) = ($"r1_{t}", $"r2_{t}", $"s1_{t}", $"s2_{t}");
        await SendAsync(m, HttpMethod.Post, "/api/v1/config/fields", Field(section, r1, "rating"));
        var rating2 = await SendAsync(m, HttpMethod.Post, "/api/v1/config/fields", Field(section, r2, "rating", new JsonObject { ["allowNa"] = true }));
        var score1 = await SendAsync(m, HttpMethod.Post, "/api/v1/config/fields",
            Field(section, s1, "score", JsonNode.Parse($$"""{"op": "sum", "inputs": ["{{r1}}", "{{r2}}"], "na": "rescale", "max": 10}""")!.AsObject()));
        await SendAsync(m, HttpMethod.Post, "/api/v1/config/fields", Field(section, s2, "score", JsonNode.Parse($$"""{"op": "max", "inputs": ["{{s1}}"]}""")!.AsObject()));
        // s1 using s2 (which uses s1): a cycle.
        var cycle = Field(section, s1, "score", JsonNode.Parse($$"""{"op": "sum", "inputs": ["{{r1}}", "{{r2}}", "{{s2}}"]}""")!.AsObject());
        var refused = await SendAsync(m, HttpMethod.Put, $"/api/v1/config/fields/{score1.Str("id")}", cycle, HttpStatusCode.BadRequest);
        Assert.Contains("cycle", refused["errors"]!["config"]!.ToJsonString());
        // Rescaled sums need their inputs' maxima; an input's change that breaks a score is refused too.
        await SendAsync(m, HttpMethod.Post, "/api/v1/config/fields", Field(section, $"n_{t}", "number"));
        var noMax = Field(section, $"s3_{t}", "score", JsonNode.Parse($$"""{"op": "sum", "inputs": ["n_{{t}}"], "na": "rescale", "max": 5}""")!.AsObject());
        Assert.Contains("input_max_required", (await SendAsync(m, HttpMethod.Post, "/api/v1/config/fields", noMax, HttpStatusCode.BadRequest)).ToJsonString());
        var select = await SendAsync(m, HttpMethod.Post, "/api/v1/config/fields",
            Field(section, $"sel_{t}", "select", JsonNode.Parse("""{"options": [{"id": "a", "label": "A", "points": 1}]}""")!.AsObject()));
        await SendAsync(m, HttpMethod.Post, "/api/v1/config/fields",
            Field(section, $"s4_{t}", "score", JsonNode.Parse($$"""{"op": "sum", "inputs": ["sel_{{t}}"]}""")!.AsObject()));
        var pointless = Field(section, $"sel_{t}", "select", JsonNode.Parse("""{"options": [{"id": "a", "label": "A"}]}""")!.AsObject());
        var broken = await SendAsync(m, HttpMethod.Put, $"/api/v1/config/fields/{select.Str("id")}", pointless, HttpStatusCode.BadRequest);
        Assert.Contains("options_without_points", broken.ToJsonString());
        // Key and type never change; defaults fit the type.
        await SendAsync(m, HttpMethod.Put, $"/api/v1/config/fields/{rating2.Str("id")}", Field(section, $"other_{t}", "rating"), HttpStatusCode.BadRequest);
        await SendAsync(m, HttpMethod.Post, "/api/v1/config/fields", Field(section, $"d_{t}", "rating", defaultValue: 9), HttpStatusCode.BadRequest);
        await SendAsync(m, HttpMethod.Post, "/api/v1/config/fields", Field(section, r1, "text"), HttpStatusCode.Conflict);
        // A field a score uses is neither archived nor deleted.
        await SendAsync(m, HttpMethod.Post, $"/api/v1/config/fields/{rating2.Str("id")}/archive", expected: HttpStatusCode.Conflict);
    }

    [TestMethod]
    public async Task DeletingAFieldNeedsTheCountOfItsValues()
    {
        var m = Manager();
        var section = await SectionAsync();
        var field = await SendAsync(m, HttpMethod.Post, "/api/v1/config/fields", Field(section, $"t_{Tag()}", "text"));
        var fieldId = field.Str("id")!;
        var device = new SyncClient(app.Client(TestUsers.Member));
        await device.SyncAllAsync();
        var status = Guid.Parse((await SendAsync(m, HttpMethod.Post, "/api/v1/config/statuses", Status("idea", $"S {Tag()}"))).Str("id")!);
        var (idea, value) = (Ids.New(), Ids.New());
        await device.SyncAllAsync([Insert("items", idea, new JsonObject { ["type"] = "idea", ["title"] = "Has a value", ["statusId"] = status.ToString() }),
            Insert("field_values", value, new JsonObject { ["itemId"] = idea.ToString(), ["fieldId"] = fieldId, ["value"] = "text" })]);
        Assert.IsNotNull(device.Row("field_values", value));
        var usage = await SendAsync(m, HttpMethod.Get, $"/api/v1/config/fields/{fieldId}/usage");
        Assert.AreEqual((1, 1), (usage.Int("values"), usage.Int("items")));
        var unconfirmed = await SendAsync(m, HttpMethod.Delete, $"/api/v1/config/fields/{fieldId}", expected: HttpStatusCode.Conflict);
        Assert.AreEqual(1, unconfirmed.Int("values"));
        await SendAsync(m, HttpMethod.Delete, $"/api/v1/config/fields/{fieldId}?confirm=1", expected: HttpStatusCode.NoContent);
        await device.SyncAllAsync();
        Assert.IsNull(device.Row("field_values", value), "its values went with it");
        Assert.IsNull(device.Row("field_definitions", Guid.Parse(fieldId)));
    }

    [TestMethod]
    public async Task ATemplateComesWithItsStatusesSectionsDefaultsAndMilestones()
    {
        var m = Manager();
        var go = Guid.Parse((await SendAsync(m, HttpMethod.Post, "/api/v1/config/statuses", Status("project", $"Running {Tag()}"))).Str("id")!);
        var stop = Guid.Parse((await SendAsync(m, HttpMethod.Post, "/api/v1/config/statuses", Status("project", $"Stopped {Tag()}"))).Str("id")!);
        var other = Guid.Parse((await SendAsync(m, HttpMethod.Post, "/api/v1/config/statuses", Status("project", $"Other {Tag()}"))).Str("id")!);
        var section = await SectionAsync();
        var field = await SendAsync(m, HttpMethod.Post, "/api/v1/config/fields", Field(section, $"b_{Tag()}", "bool"));
        JsonObject Template(params JsonObject[] milestones) => new()
        {
            ["name"] = "AI-ish", ["color"] = "#8b5cf6", ["icon"] = "pi-microchip-ai", ["defaultStatusId"] = go.ToString(),
            ["statusIds"] = new JsonArray(go.ToString(), stop.ToString()), ["sectionIds"] = new JsonArray(section.ToString()),
            ["fieldDefaults"] = new JsonArray(new JsonObject { ["fieldId"] = field.Str("id"), ["value"] = true }),
            ["defaultShares"] = new JsonArray(new JsonObject { ["groupId"] = GroupEntity.MembersId.ToString(), ["access"] = "edit" }),
            ["milestones"] = new JsonArray([.. milestones.Select(x => x.DeepClone())]),
        };
        var gate = new JsonObject
        {
            ["kind"] = "gate", ["title"] = "PoC decision", ["offsetDays"] = 56, ["dod"] = new JsonArray("Goals defined", "Result documented"),
            ["onGoStatusId"] = go.ToString(), ["onNoGoStatusId"] = stop.ToString(),
        };
        var kickOff = new JsonObject { ["kind"] = "milestone", ["title"] = "Kick-off", ["offsetDays"] = 0 };
        var created = await SendAsync(m, HttpMethod.Post, "/api/v1/config/templates", Template(kickOff, gate));
        Assert.AreEqual(2, created.Arr("milestones").Count);
        Assert.AreEqual("Kick-off", created.Arr("milestones")[0].Str("title"));
        Assert.IsTrue(string.CompareOrdinal(created.Arr("milestones")[0].Str("sortOrder"), created.Arr("milestones")[1].Str("sortOrder")) < 0);
        var id = created.Str("id")!;
        var device = new SyncClient(app.Client(TestUsers.Member));
        await device.SyncAllAsync();
        Assert.AreEqual(2, device.Rows("template_statuses").Count(r => r.Str("templateId") == id));
        Assert.AreEqual(1, device.Rows("template_field_defaults").Count(r => r.Str("templateId") == id));
        Assert.AreEqual(2, device.Rows("template_milestones").Count(r => r.Str("templateId") == id));
        // Without the kick-off, the gate kept (its id): the kick-off is tombstoned on the devices.
        var keptGate = created.Arr("milestones")[1]!.DeepClone().AsObject();
        var kickOffId = created.Arr("milestones")[0].Str("id")!;
        var updated = await SendAsync(m, HttpMethod.Put, $"/api/v1/config/templates/{id}", Template(keptGate));
        Assert.AreEqual(keptGate.Str("id"), updated.Arr("milestones").Single().Str("id"));
        await device.SyncAllAsync();
        Assert.IsNull(device.Row("template_milestones", Guid.Parse(kickOffId)));
        // Suggestions and the default within the allowed statuses; gates only suggest.
        var wrong = Template(gate);
        wrong["defaultStatusId"] = other.ToString();
        await SendAsync(m, HttpMethod.Put, $"/api/v1/config/templates/{id}", wrong, HttpStatusCode.BadRequest);
        var suggesting = new JsonObject { ["kind"] = "milestone", ["title"] = "M", ["onGoStatusId"] = go.ToString() };
        await SendAsync(m, HttpMethod.Put, $"/api/v1/config/templates/{id}", Template(suggesting), HttpStatusCode.BadRequest);
        var archived = await SendAsync(m, HttpMethod.Post, $"/api/v1/config/templates/{id}/archive");
        Assert.IsTrue(archived.Bool("isArchived"));
    }

    [TestMethod]
    public async Task RolesAreSuggestionsManagedOnline()
    {
        var m = Manager();
        var role = await SendAsync(m, HttpMethod.Post, "/api/v1/config/roles", new JsonObject { ["name"] = $"Sponsor {Tag()}", ["description"] = "Pays." });
        var id = role.Str("id")!;
        await SendAsync(m, HttpMethod.Post, "/api/v1/config/roles", new JsonObject { ["name"] = role.Str("name") }, HttpStatusCode.Conflict);
        var renamed = await SendAsync(m, HttpMethod.Put, $"/api/v1/config/roles/{id}", new JsonObject { ["name"] = $"Patron {Tag()}" });
        Assert.AreEqual(2, renamed.Int("rev"));
        var device = new SyncClient(app.Client(TestUsers.Viewer));
        await device.SyncAllAsync();
        Assert.IsNotNull(device.Row("assignment_roles", Guid.Parse(id)));
        await SendAsync(m, HttpMethod.Delete, $"/api/v1/config/roles/{id}", expected: HttpStatusCode.NoContent);
        await device.SyncAllAsync();
        Assert.IsNull(device.Row("assignment_roles", Guid.Parse(id)));
        // Clients don't write configuration.
        var refused = await device.SyncAllAsync([Insert("statuses", Ids.New(), Status("idea", "From a client"))]);
        Assert.AreEqual("not_supported", refused.Arr("results")[0].Str("code"));
    }
}
