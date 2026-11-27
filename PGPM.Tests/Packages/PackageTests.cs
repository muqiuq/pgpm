using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PGPM.Auth;
using PGPM.Config;
using PGPM.Data;
using PGPM.Items;
using PGPM.Maps;
using PGPM.Packages;
using PGPM.Sync;
using PGPM.Tests.Sync;
using static PGPM.Tests.Sync.SyncKit;

namespace PGPM.Tests.Packages;

/// <summary>
/// The configuration packages' rules in C# (docs/03 §17): the built-in ones pass them, and the broken variants of
/// <c>tools/check-packages.py</c>'s self-test are refused with the same messages.
/// </summary>
[TestClass]
public sealed class PackageCheckTests
{
    private static Dictionary<string, (string File, JsonObject Data)> BuiltIn() =>
        BuiltInPackages.Names.ToDictionary(n => n, n => ($"{n}.pgpm.json", BuiltInPackages.Load(n)));

    private static List<string> Check(Dictionary<string, (string File, JsonObject Data)> packages)
    {
        var problems = packages.Values.SelectMany(p => BuiltInPackages.Schema.Errors(p.Data).Select(e => $"{p.File}: {e.Path}: {e.Message}")).ToList();
        return problems.Count > 0 ? problems : new PackageChecker(packages, oneDefaultAcrossAll: true).Run();
    }

    [TestMethod]
    public void TheBuiltInPackagesPassTheSchemaAndTheRules()
    {
        CollectionAssert.AreEqual(new[] { "core", "innovation-ai" }, BuiltInPackages.Names.ToList());
        Assert.AreEqual(0, BuiltInPackages.Schema.Problems.Count, string.Join("\n", BuiltInPackages.Schema.Problems));
        var problems = Check(BuiltIn());
        Assert.AreEqual("", string.Join("\n", problems));
    }

    private static JsonObject F(Dictionary<string, (string File, JsonObject Data)> p, string key) => p["innovation-ai"].Data["fields"]![key]!.AsObject();

    private static JsonObject Ai(Dictionary<string, (string File, JsonObject Data)> p) => p["innovation-ai"].Data;

    private static JsonObject Core(Dictionary<string, (string File, JsonObject Data)> p) => p["core"].Data;

    /// <summary>tools/check-packages.py's self_test, case by case.</summary>
    [TestMethod]
    public void TheCheckerRefusesTheBrokenVariantsOfTheScriptsSelfTest()
    {
        var cases = new List<(string Name, Action<Dictionary<string, (string, JsonObject)>> Mutate, string Expected)>
        {
            ("score cycle", p =>
            {
                F(p, "field:feasibility_index")["config"]!["inputs"]!.AsArray().Add("impact_score");
                F(p, "field:impact_score")["config"]!["inputs"]!.AsArray().Add("feasibility_index");
                F(p, "field:impact_score")["config"]!["max"] = 85;
            }, "score cycle"),
            ("score input not numeric", p => F(p, "field:impact_score")["config"]!["inputs"]![0] = "problem", "has no numeric value"),
            ("score input unknown", p => F(p, "field:feasibility_index")["config"]!["inputs"]!.AsArray().Add("nope"), "unknown field 'field:nope'"),
            ("select input without points", p => F(p, "field:time_to_proof_of_value")["config"]!["options"]![0]!.AsObject().Remove("points"),
                "without points"),
            ("rescale max not derivable", p =>
            {
                F(p, "field:effective_gain_pct")["config"]!["na"] = "rescale";
                F(p, "field:effective_gain_pct")["config"]!["op"] = "sum";
                F(p, "field:expected_gain_pct")["config"]!.AsObject().Remove("max");
            }, "can't be derived"),
            ("rescale max wrong", p => F(p, "field:feasibility_index")["config"]!["max"] = 60, "differs from the sum"),
            ("two default project statuses", p => Ai(p)["statuses"]!["status:project.closed"]!["isDefault"] = true, "exactly one default status"),
            ("no default lead status", p => Core(p)["statuses"]!["status:lead.new"]!.AsObject().Remove("isDefault"),
                "item type 'lead' needs exactly one default status"),
            ("template default status not allowed", p => Ai(p)["templates"]!["template:ai_use_case"]!["defaultStatus"] = "status:project.draft",
                "not among the template's allowed statuses"),
            ("gate suggestion not allowed", p => Ai(p)["templates"]!["template:ai_use_case"]!["milestones"]![2]!["onGoStatus"] = "status:project.done",
                "not among the template's allowed statuses"),
            ("gate suggestion idea status", p => Ai(p)["templates"]!["template:ai_use_case"]!["milestones"]![2]!["onNoGoStatus"] = "status:idea.rejected",
                "not of projects"),
            ("defaultFor beyond appliesTo", p => Ai(p)["sections"]!["section:charter"]!["defaultFor"] = new JsonArray("idea"), "not in appliesTo"),
            ("dangling section reference", p => Ai(p)["templates"]!["template:ai_use_case"]!["sections"]!.AsArray().Add("section:nope"),
                "unknown section 'section:nope'"),
            ("reference to a package not required", p => Ai(p)["requires"] = new JsonArray(), "does not require"),
            ("unknown required package", p => Ai(p)["requires"] = new JsonArray("core", "nope"), "unknown package 'nope'"),
            ("element on a missing layer", p => Ai(p)["mapTemplates"]!["maptemplate:crazy_8s"]!["elements"]![0]!["layer"] = "nope", "unknown layer 'nope'"),
            ("key defined twice", p => Core(p)["statuses"]!["status:project.closed"] = Ai(p)["statuses"]!["status:project.closed"]!.DeepClone(),
                "also defined by package"),
            ("bad field key", p => Ai(p)["fields"]!["field:Bad"] = F(p, "field:problem").DeepClone(), "does not match"),
            ("missing required property", p => Core(p)["statuses"]!["status:idea.new"]!.AsObject().Remove("category"), "missing required property 'category'"),
            ("bad enum", p => Core(p)["statuses"]!["status:idea.new"]!["category"] = "paused", "is not one of"),
            ("bad color", p => Core(p)["statuses"]!["status:idea.new"]!["color"] = "#ABCDEF", "does not match"),
            ("unknown icon", p => Core(p)["statuses"]!["status:idea.new"]!["icon"] = "pi-unicorn", "not a PrimeIcons"),
            ("milestone with status suggestion", p => Ai(p)["templates"]!["template:ai_use_case"]!["milestones"]![1]!["onGoStatus"] = "status:project.mvp_pilot",
                "must be null"),
            ("view status of another type", p => Ai(p)["savedViews"]!["view:review_board_agenda"]!["definition"]!["filters"]!["statuses"] =
                new JsonArray("status:project.prototype"), "the view lists ideas"),
            ("view column unknown", p => Ai(p)["savedViews"]!["view:running_experiments"]!["definition"]!["columns"]!.AsArray().Add("field:rb_decision"),
                "does not apply to projects"),
            ("view in on unknown option", p => Ai(p)["savedViews"]!["view:ai_system_inventory"]!["definition"]!["filters"]!["fields"]!.AsArray()
                .Add(new JsonObject { ["field"] = "field:eu_ai_act_class", ["op"] = "in", ["values"] = new JsonArray("huge") }), "not an option"),
            ("matrix axis not numeric", p => Ai(p)["matrixPresets"]!["matrixpreset:prioritization"]!["matrix"]!["x"]!["field"] = "problem",
                "axes need number, rating or score"),
            ("field default of a foreign section", p => Ai(p)["templates"]!["template:ai_ambition"]!["fieldDefaults"]!["field:is_ai_system"] = true,
                "not one of the template's sections"),
            ("field default not an option", p => Ai(p)["templates"]!["template:ai_use_case"]!["fieldDefaults"]!["field:make_or_buy"] = "steal",
                "not an option of the field"),
        };
        var failures = new List<string>();
        foreach (var (name, mutate, expected) in cases)
        {
            var variant = BuiltIn();
            mutate(variant!);
            var problems = Check(variant);
            if (!problems.Any(p => p.Contains(expected, StringComparison.Ordinal)))
            {
                failures.Add($"{name}: expected a problem containing '{expected}', got: {string.Join("; ", problems)}");
            }
        }
        Assert.AreEqual("", string.Join("\n", failures));
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => BuiltInPackages.Parse("""{"statuses": {"a": {}, "a": {}}}"""), "duplicate keys");
    }

    /// <summary>The icon list is check-packages.py's (which follows PGPM.WebUI's pinned PrimeIcons).</summary>
    [TestMethod]
    public void ThePrimeIconsAreTheScripts()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "check-packages.py")))
        {
            dir = dir.Parent;
        }
        Assert.IsNotNull(dir, "the repository");
        var script = File.ReadAllText(Path.Combine(dir.FullName, "tools", "check-packages.py"));
        var list = Regex.Match(script, "PRIMEICONS = set\\(\"\"\"(.*?)\"\"\"", RegexOptions.Singleline).Groups[1].Value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        Assert.IsTrue(list.SetEquals(PrimeIcons.Names), "PGPM/Packages/PrimeIcons.cs differs from tools/check-packages.py");
        var version = Regex.Match(script, "PRIMEICONS_VERSION = \"([^\"]+)\"").Groups[1].Value;
        Assert.AreEqual(PrimeIcons.Version, version);
    }
}

/// <summary>
/// The importer on a database of its own (docs/03 §17): the first start imports <c>core</c> and <c>innovation-ai</c>, a second
/// import changes nothing, a dry run tells local edits apart (kept unless overwritten), groups need an admin, the export is a
/// valid package.
/// </summary>
[TestClass]
public sealed class PackageImportTests
{
    private static TestApp app = null!;
    private static string connection = null!;

    [ClassInitialize]
    public static void Start(TestContext _)
    {
        connection = TestDefaults.CreateDatabase("pgpm_test_packages");
        using (var db = PgpmDbContext.Create(connection))
        {
            DatabaseMigrator.Migrate(db, NullLogger.Instance);
            TestUsers.Seed(db);
        }
        app = new TestApp(new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = connection, ["Packages:SeedOnFirstStart"] = "core,innovation-ai",
        });
        app.Client().Dispose();
    }

    [ClassCleanup]
    public static void Stop() => app.Dispose();

    private static HttpClient Admin() => app.Client(TestUsers.Admin);

    private static async Task<JsonObject> PostAsync(HttpClient client, string url, JsonNode body) =>
        await (await client.PostJsonNodeAsync(url, body)).JsonAsync();

    [TestMethod]
    public async Task TheFirstStartImportsBothPackages()
    {
        await using var db = app.NewDb();
        var core = BuiltInPackages.Load("core");
        var ai = BuiltInPackages.Load("innovation-ai");
        int Count(string collection) => (core[collection] as JsonObject)?.Count + (ai[collection] as JsonObject)?.Count ?? (core[collection] as JsonObject)?.Count
            ?? (ai[collection] as JsonObject)?.Count ?? 0;
        Assert.AreEqual(Count("statuses"), await db.Statuses.CountAsync(s => !s.Deleted));
        Assert.AreEqual(Count("sections"), await db.Sections.CountAsync(s => !s.Deleted));
        Assert.AreEqual(Count("fields"), await db.FieldDefinitions.CountAsync(f => !f.Deleted));
        Assert.AreEqual(Count("templates"), await db.ProjectTemplates.CountAsync(t => !t.Deleted));
        Assert.IsTrue(await db.AssignmentRoles.CountAsync(r => !r.Deleted) >= Count("roles"), "the packages' roles");
        Assert.AreEqual(Count("savedViews"), await db.SavedViews.CountAsync(v => !v.Deleted && v.IsShared));
        Assert.AreEqual(Count("mapTemplates") + Count("matrixPresets"), await db.Maps.CountAsync(m => m.IsTemplate && !m.Deleted));
        Assert.AreEqual(1, await db.Maps.CountAsync(m => m.IsTemplate && m.Kind == MapKind.Matrix));
        // One default status per type; the seeded groups taken over (by their fixed ids), with core's description.
        foreach (var type in new[] { ItemType.Idea, ItemType.Lead, ItemType.Project })
        {
            Assert.AreEqual(1, await db.Statuses.CountAsync(s => s.ItemType == type && s.IsDefault));
        }
        var members = await db.Groups.Include(g => g.Rights).SingleAsync(g => g.Id == GroupEntity.MembersId);
        Assert.AreEqual(core["groups"]!["group:members"]!["description"]!.GetValue<string>(), members.Description);
        Assert.AreEqual(6, members.Rights.Count);
        Assert.AreEqual(5, await db.Groups.CountAsync(), "Members, Viewers, Innovation team, Review board, Compliance");
        Assert.IsTrue(await db.ConfigKeys.AnyAsync(k => k.Key == "group:members" && k.Id == GroupEntity.MembersId && k.Package == "core"));
        // The template AI use case: its statuses, sections, defaults, gates with their checklists (as fractional orders).
        var template = await db.ProjectTemplates.SingleAsync(t => t.Name == "AI use case");
        var milestones = await db.TemplateMilestones.Where(m => m.TemplateId == template.Id).ToListAsync();
        Assert.AreEqual(7, milestones.Count);
        Assert.IsTrue(milestones.All(m => Lib.Helper.FractionalKey.IsValid(m.SortOrder)));
        Assert.AreEqual(7, await db.TemplateStatuses.CountAsync(s => s.TemplateId == template.Id));
        Assert.AreEqual(2, await db.TemplateFieldDefaults.CountAsync(d => d.TemplateId == template.Id));
        // The review board's section: only that group edits it.
        var board = await db.Sections.SingleAsync(s => s.Name == "Review board decision");
        Assert.AreEqual(SectionAccess.Edit, (await db.SectionGroups.SingleAsync(g => g.SectionId == board.Id)).Access);
        // The feasibility index evaluates as the playbook's example (docs/03 §16): 12 ratings and the time to proof of value.
        var fields = (await db.FieldDefinitions.AsNoTracking().ToListAsync()).Select(ConfigService.Spec).ToList();
        var feasibility = fields.Single(f => f.Key == "feasibility_index");
        Assert.AreEqual(0, ScoreConfigValidator.Validate(feasibility, fields).Count);
        Assert.AreEqual(1, await db.KeyValues.CountAsync(k => k.Key == ConfigPackageSeeder.Marker));
    }

    [TestMethod]
    public async Task ASecondImportChangesNothing()
    {
        foreach (var name in BuiltInPackages.Names)
        {
            var diff = await PostAsync(Admin(), "/api/v1/config/packages/dry-run", new JsonObject { ["builtIn"] = name });
            var changed = diff.Arr("entries").Where(e => e.Str("action") != "unchanged").Select(e => $"{e.Str("key")} {e.Str("action")} {e!["changed"]?.ToJsonString()}");
            Assert.AreEqual("", string.Join("\n", changed), $"{name}: a dry run after the first start");
        }
        long head;
        await using (var db = app.NewDb())
        {
            head = await SyncCounter.HeadAsync(db, CancellationToken.None);
        }
        var applied = await PostAsync(Admin(), "/api/v1/config/packages/apply", new JsonObject { ["builtIn"] = "innovation-ai" });
        Assert.AreEqual((0, 0), (applied.Int("created"), applied.Int("updated")));
        await using (var db = app.NewDb())
        {
            Assert.AreEqual(head, await SyncCounter.HeadAsync(db, CancellationToken.None), "no synced row was written");
        }
        var list = await (await Admin().GetAsync("/api/v1/config/packages")).Content.ReadAsStringAsync();
        Assert.IsTrue(JsonNode.Parse(list)!.AsArray().All(p => p!["imported"] is JsonObject), list);
    }

    [TestMethod]
    public async Task ALocalEditIsAConflictKeptUnlessOverwritten()
    {
        Guid id;
        await using (var db = app.NewDb())
        {
            id = await db.ConfigKeys.Where(k => k.Key == "status:lead.proposal").Select(k => k.Id).SingleAsync();
        }
        var admin = Admin();
        using (var put = await admin.PutJsonAsync($"/api/v1/config/statuses/{id}",
            JsonNode.Parse("""{"name": "Offer", "color": "#6366f1", "icon": "pi-file-edit", "category": "active"}""")!))
        {
            Assert.AreEqual(HttpStatusCode.OK, put.StatusCode, await put.Content.ReadAsStringAsync());
        }
        var diff = await PostAsync(admin, "/api/v1/config/packages/dry-run", new JsonObject { ["builtIn"] = "core" });
        var entry = diff.Arr("entries").Single(e => e.Str("key") == "status:lead.proposal")!;
        Assert.AreEqual("conflict", entry.Str("action"));
        Assert.AreEqual("name", entry.Arr("changed").Single()!.GetValue<string>());
        Assert.AreEqual(1, diff["counts"].Int("conflict"));

        var kept = await PostAsync(admin, "/api/v1/config/packages/apply", new JsonObject { ["builtIn"] = "core" });
        Assert.AreEqual(1, kept.Int("skipped"));
        await using (var db = app.NewDb())
        {
            Assert.AreEqual("Offer", await db.Statuses.Where(s => s.Id == id).Select(s => s.Name).SingleAsync(), "the local edit wins");
        }
        var overwritten = await PostAsync(admin, "/api/v1/config/packages/apply",
            new JsonObject { ["builtIn"] = "core", ["overwrite"] = new JsonArray("status:lead.proposal") });
        Assert.AreEqual((1, 0), (overwritten.Int("updated"), overwritten.Int("skipped")));
        await using (var db = app.NewDb())
        {
            Assert.AreEqual("Proposal", await db.Statuses.Where(s => s.Id == id).Select(s => s.Name).SingleAsync());
        }
        var again = await PostAsync(admin, "/api/v1/config/packages/dry-run", new JsonObject { ["builtIn"] = "core" });
        Assert.AreEqual(0, again["counts"].Int("conflict") + again["counts"].Int("update") + again["counts"].Int("create"));
    }

    [TestMethod]
    public async Task APackageUpdatesWhatWasNotEditedHereAndNeedsAnAdminForGroups()
    {
        var package = BuiltInPackages.Load("core");
        package["name"] = "core";
        package["version"] = "1.1.0";
        package["roles"]!["role:contact"]!["description"] = "Contact person (updated).";
        package["roles"]!["role:sponsor_core"] = new JsonObject { ["name"] = "Core sponsor", ["sortOrder"] = 40 };
        var manager = app.ClientWithRights("Configuration:Manage");
        var diff = await PostAsync(manager, "/api/v1/config/packages/dry-run", new JsonObject { ["package"] = package.DeepClone() });
        Assert.AreEqual("update", diff.Arr("entries").Single(e => e.Str("key") == "role:contact")!.Str("action"));
        Assert.AreEqual("create", diff.Arr("entries").Single(e => e.Str("key") == "role:sponsor_core")!.Str("action"));
        Assert.IsFalse(diff.Bool("needsAdmin"));
        var applied = await PostAsync(manager, "/api/v1/config/packages/apply", new JsonObject { ["package"] = package.DeepClone() });
        Assert.AreEqual((1, 1), (applied.Int("created"), applied.Int("updated")));

        // A group's change is the admins'.
        package["groups"]!["group:viewers"]!["description"] = "Read only.";
        using var refused = await manager.PostJsonNodeAsync("/api/v1/config/packages/apply", new JsonObject { ["package"] = package.DeepClone() });
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.AreEqual("admin_required", await refused.CodeAsync());
        var byAdmin = await PostAsync(Admin(), "/api/v1/config/packages/apply", new JsonObject { ["package"] = package.DeepClone() });
        Assert.AreEqual(1, byAdmin.Int("updated"));
        // Back to the built-in version, for the other tests.
        await PostAsync(Admin(), "/api/v1/config/packages/apply",
            new JsonObject { ["builtIn"] = "core", ["overwrite"] = new JsonArray("role:contact", "group:viewers") });
    }

    [TestMethod]
    public async Task AnInvalidPackageIsRefusedWithItsProblems()
    {
        var package = BuiltInPackages.Load("innovation-ai");
        package["fields"]!["field:feasibility_index"]!["config"]!["inputs"]!.AsArray().Add("nope");
        using var response = await Admin().PostJsonNodeAsync("/api/v1/config/packages/dry-run", new JsonObject { ["package"] = package });
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ProblemAsync();
        Assert.AreEqual("invalid_package", problem.GetProperty("code").GetString());
        Assert.Contains("unknown field 'field:nope'", problem.GetProperty("problems").ToString());
        using var unknown = await Admin().PostJsonNodeAsync("/api/v1/config/packages/dry-run",
            new JsonObject { ["package"] = new JsonObject { ["format"] = "pgpm-config-package", ["formatVersion"] = 1, ["name"] = "x-y", ["title"] = "X",
                ["version"] = "1.0.0", ["requires"] = new JsonArray("other") } });
        Assert.Contains("is not imported yet", await unknown.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task TheExportIsAValidPackageOfTheConfiguration()
    {
        var export = (await (await Admin().GetAsync("/api/v1/config/packages/export")).JsonAsync());
        Assert.AreEqual(0, BuiltInPackages.Schema.Errors(export).Count, string.Join("\n", BuiltInPackages.Schema.Errors(export)));
        var problems = new PackageChecker(new() { ["export"] = ("export.pgpm.json", export) }, oneDefaultAcrossAll: true).Run();
        Assert.AreEqual("", string.Join("\n", problems));
        Assert.IsNotNull(export["fields"]?["field:feasibility_index"]);
        Assert.IsNotNull(export["templates"]?["template:ai_use_case"]?["milestones"]);
        Assert.IsNotNull(export["matrixPresets"]?["matrixpreset:prioritization"]);
        // What a package brought, as that package: core's keys only.
        var core = (await (await Admin().GetAsync("/api/v1/config/packages/export?package=core")).JsonAsync());
        Assert.IsNull(core["fields"]);
        Assert.IsNotNull(core["statuses"]?["status:idea.new"]);
    }
}
