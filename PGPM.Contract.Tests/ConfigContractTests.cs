using System.Net;
using System.Text.Json;
using static PGPM.Contract.Tests.ContractKit;

namespace PGPM.Contract.Tests;

/// <summary>
/// The statuses endpoints of a running PGPM (docs/03 §3.3) as the client's administration (PGPM.WebUI/src/api/config.ts,
/// StatusesView/StatusDialog) uses them: <c>StatusItem</c> answers, validation by field, the conflicts shown under a field,
/// the default rules, the order of a type – and the change reaching devices as a read-only <c>statuses</c> row of the sync.
/// The status is archived at the end (the tests may run against a deployment).
/// </summary>
[TestClass]
public sealed class ConfigContractTests
{
    [TestMethod]
    public async Task StatusesAreManagedOverRestAndArriveThroughTheSync()
    {
        using var admin = await FirstAdmin.SignInAsync();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var name = $"Contract {stamp}";

        // Validation by field (the dialog shows each under its input).
        using (var invalid = await admin.SendAsync(HttpMethod.Post, "/api/v1/config/statuses",
            new { itemType = "idea", name, color = "green", icon = "eye", category = "open", maxDays = 0 }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            var problem = await JsonAsync(invalid);
            Assert.AreEqual("validation_failed", problem.GetProperty("code").GetString());
            var errors = problem.GetProperty("errors");
            foreach (var field in new[] { "color", "icon", "maxDays" })
            {
                Assert.IsTrue(errors.TryGetProperty(field, out _), $"{field}: {errors}");
            }
        }

        var created = await admin.CallAsync(HttpMethod.Post, "/api/v1/config/statuses",
            new { itemType = "idea", name, color = "#16A34A", icon = "pi-eye", category = "open", maxDays = 5, isDefault = false });
        var id = created.GetProperty("id").GetString()!;
        Assert.AreEqual("idea", created.GetProperty("itemType").GetString());
        Assert.AreEqual(name, created.GetProperty("name").GetString());
        Assert.AreEqual("#16a34a", created.GetProperty("color").GetString(), "colours are kept lower case");
        Assert.AreEqual("pi-eye", created.GetProperty("icon").GetString(), "icons are stored as pi-xxx");
        Assert.AreEqual("open", created.GetProperty("category").GetString());
        Assert.AreEqual(5, created.GetProperty("maxDays").GetInt32());
        Assert.IsFalse(created.GetProperty("isDefault").GetBoolean());
        Assert.IsFalse(created.GetProperty("isArchived").GetBoolean());
        Assert.IsTrue(created.GetProperty("rev").GetInt32() >= 1);

        // Another status of the type with the name (ignoring case): 409 duplicate_name with a detail for the person.
        using (var duplicate = await admin.SendAsync(HttpMethod.Post, "/api/v1/config/statuses",
            new { itemType = "idea", name = name.ToUpperInvariant(), color = "#16a34a", category = "open" }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);
            var problem = await JsonAsync(duplicate);
            Assert.AreEqual("duplicate_name", problem.GetProperty("code").GetString());
            Assert.IsFalse(string.IsNullOrEmpty(problem.GetProperty("detail").GetString()));
        }

        // The order: every id of the type; it answers the type's statuses in order (sort orders 10, 20, …).
        var device = new AdminDevice(admin);
        var statuses = Rows(await device.SyncAsync(), "statuses").Where(s => s.GetProperty("itemType").GetString() == "idea"
            && !s.GetProperty("deleted").GetBoolean()).OrderBy(s => s.GetProperty("sortOrder").GetInt32()).ToList();
        var row = statuses.Single(s => s.GetProperty("id").GetString() == id);
        Assert.AreEqual("#16a34a", row.GetProperty("color").GetString(), "the row of the sync is the endpoint's");
        Assert.AreEqual(5, row.GetProperty("maxDays").GetInt32());
        var ids = statuses.Select(s => s.GetProperty("id").GetString()!).Where(s => s != id).Prepend(id).ToList();
        var ordered = await admin.CallAsync(HttpMethod.Put, "/api/v1/config/statuses/order", new { itemType = "idea", ids });
        CollectionAssert.AreEqual(ids, ordered.EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList());
        Assert.AreEqual(10, ordered[0].GetProperty("sortOrder").GetInt32());
        using (var partial = await admin.SendAsync(HttpMethod.Put, "/api/v1/config/statuses/order", new { itemType = "idea", ids = ids.Take(1) }))
        {
            Assert.AreEqual(HttpStatusCode.BadRequest, partial.StatusCode);
        }

        // The default: this one now (the old one isn't any more); it can't be archived nor stop being the default.
        var previous = statuses.Single(s => s.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetString()!;
        var update = new { itemType = "idea", name, color = "#16a34a", icon = "pi-eye", category = "open", maxDays = 5, isDefault = true };
        Assert.IsTrue((await admin.CallAsync(HttpMethod.Put, $"/api/v1/config/statuses/{id}", update)).GetProperty("isDefault").GetBoolean());
        using (var archive = await admin.SendAsync(HttpMethod.Post, $"/api/v1/config/statuses/{id}/archive"))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, archive.StatusCode);
            Assert.AreEqual("default_status", await CodeAsync(archive));
        }
        using (var undo = await admin.SendAsync(HttpMethod.Put, $"/api/v1/config/statuses/{id}", update with { isDefault = false }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, undo.StatusCode);
            Assert.AreEqual("default_required", await CodeAsync(undo));
        }
        var changes = Rows(await device.SyncAsync(), "statuses").ToList();
        Assert.IsFalse(changes.Single(s => s.GetProperty("id").GetString() == previous).GetProperty("isDefault").GetBoolean());

        // Back as it was: the old default again, this one archived (and shown so to every device).
        var old = changes.Single(s => s.GetProperty("id").GetString() == previous);
        await admin.CallAsync(HttpMethod.Put, $"/api/v1/config/statuses/{previous}", new
        {
            itemType = "idea", name = old.GetProperty("name").GetString(), color = old.GetProperty("color").GetString(),
            icon = old.GetProperty("icon").ValueKind == JsonValueKind.Null ? null : old.GetProperty("icon").GetString(),
            category = old.GetProperty("category").GetString(),
            maxDays = old.GetProperty("maxDays").ValueKind == JsonValueKind.Null ? (int?)null : old.GetProperty("maxDays").GetInt32(),
            isDefault = true,
        });
        var archived = await admin.CallAsync(HttpMethod.Post, $"/api/v1/config/statuses/{id}/archive");
        Assert.IsTrue(archived.GetProperty("isArchived").GetBoolean());
        Assert.IsTrue(Rows(await device.SyncAsync(), "statuses").Single(s => s.GetProperty("id").GetString() == id).GetProperty("isArchived").GetBoolean());

        // Read-only for clients: a push of a statuses row is refused; without Configuration: Manage the endpoints are 403.
        var refused = (await device.SyncAsync([Patch(id, 1, "name", name, "x")], table: "statuses")).GetProperty("results")[0];
        Assert.AreEqual("rejected", refused.GetProperty("status").GetString());
        var groupId = await GroupAsync(admin, $"Contract statuses {stamp}", new() { ["Ideas"] = "Create" });
        using var member = await UserAsync(admin, $"cleo-{stamp}", $"Cleo {stamp}", groupId);
        using var forbidden = await member.SendAsync(HttpMethod.Post, $"/api/v1/config/statuses/{id}/unarchive");
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    /// <summary>
    /// Sections, fields and templates as the admin pages (SectionDialog, FieldDialog, TemplateEditView) use them: a section
    /// with a group's edit access – read back through <c>GET /api/v1/config/sections/{id}</c>, since <c>section_groups</c> isn't
    /// synced –, a rating field in it, a project template with an allowed status, the section, a default and a gate with its
    /// Definition of Done; each answer's shape and the rows the devices get (<c>canEdit</c>, the template's parts). Archived at
    /// the end.
    /// </summary>
    [TestMethod]
    public async Task SectionsFieldsAndTemplatesAreManagedOverRestAndArriveThroughTheSync()
    {
        using var admin = await FirstAdmin.SignInAsync();
        var stamp = Guid.NewGuid().ToString("N")[..8];
        var groupId = await GroupAsync(admin, $"Contract board {stamp}", new() { ["Ideas"] = "Create" });

        var section = await admin.CallAsync(HttpMethod.Post, "/api/v1/config/sections", new
        {
            name = $"Contract section {stamp}", description = (string?)null, appliesTo = new[] { "idea", "project" }, defaultFor = Array.Empty<string>(),
            collapsedByDefault = true, access = new[] { new { groupId, level = "edit" } },
        });
        var sectionId = section.GetProperty("id").GetString()!;
        var read = await admin.CallAsync(HttpMethod.Get, $"/api/v1/config/sections/{sectionId}");
        var access = read.GetProperty("access").EnumerateArray().Single();
        Assert.AreEqual(groupId, access.GetProperty("groupId").GetString());
        Assert.AreEqual($"Contract board {stamp}", access.GetProperty("groupName").GetString());
        Assert.AreEqual("edit", access.GetProperty("level").GetString());
        Assert.IsTrue(read.GetProperty("collapsedByDefault").GetBoolean());
        var listed = await admin.CallAsync(HttpMethod.Get, "/api/v1/config/sections");
        Assert.IsTrue(listed.EnumerateArray().Any(s => s.GetProperty("id").GetString() == sectionId));

        var key = $"contract_{stamp}";
        var field = await admin.CallAsync(HttpMethod.Post, "/api/v1/config/fields", new
        {
            sectionId, key, label = "Contract rating", helpText = (string?)null, type = "rating",
            config = new { min = 0, max = 5, allowNa = true }, required = false, defaultValue = 3,
        });
        var fieldId = field.GetProperty("id").GetString()!;
        Assert.AreEqual("rating", field.GetProperty("type").GetString());
        Assert.AreEqual(key, field.GetProperty("key").GetString());
        Assert.AreEqual(3, field.GetProperty("defaultValue").GetInt32());
        using (var duplicate = await admin.SendAsync(HttpMethod.Post, "/api/v1/config/fields",
            new { sectionId, key, label = "Again", type = "rating", config = new { max = 5 } }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, duplicate.StatusCode);
            Assert.AreEqual("duplicate_key", await CodeAsync(duplicate));
        }

        var device = new AdminDevice(admin);
        var first = await device.SyncAsync();
        var projectStatus = Rows(first, "statuses").First(s => s.GetProperty("itemType").GetString() == "project"
            && !s.GetProperty("isArchived").GetBoolean() && !s.GetProperty("deleted").GetBoolean()).GetProperty("id").GetString()!;
        var template = await admin.CallAsync(HttpMethod.Post, "/api/v1/config/templates", new
        {
            name = $"Contract template {stamp}", description = (string?)null, color = "#2563eb", icon = "pi-bolt", defaultStatusId = projectStatus,
            statusIds = new[] { projectStatus }, sectionIds = new[] { sectionId }, fieldDefaults = new[] { new { fieldId, value = 4 } },
            defaultShares = new[] { new { groupId, access = "edit" } },
            milestones = new[]
            {
                new
                {
                    id = (string?)null, kind = "gate", title = "G0", description = (string?)null, offsetDays = (int?)14, dod = new[] { "Sponsor named" },
                    onGoStatusId = (string?)projectStatus, onNoGoStatusId = (string?)null,
                },
            },
        });
        var templateId = template.GetProperty("id").GetString()!;
        Assert.AreEqual(projectStatus, template.GetProperty("statusIds")[0].GetString());
        Assert.AreEqual(4, template.GetProperty("fieldDefaults")[0].GetProperty("value").GetInt32());
        var gate = template.GetProperty("milestones")[0];
        Assert.AreEqual("gate", gate.GetProperty("kind").GetString());
        Assert.AreEqual("Sponsor named", gate.GetProperty("dod")[0].GetString());
        Assert.IsFalse(string.IsNullOrEmpty(gate.GetProperty("sortOrder").GetString()));

        // The devices: the section – edited by its group's members only –, the field, the template and its parts.
        var otherGroup = await GroupAsync(admin, $"Contract others {stamp}", new() { ["Ideas"] = "Create" });
        using (var member = await UserAsync(admin, $"bo-{stamp}", $"Bo {stamp}", groupId))
        using (var other = await UserAsync(admin, $"oz-{stamp}", $"Oz {stamp}", otherGroup))
        {
            bool CanEdit(JsonElement answer) =>
                Rows(answer, "sections").Single(s => s.GetProperty("id").GetString() == sectionId).GetProperty("canEdit").GetBoolean();
            Assert.IsTrue(CanEdit(await new Device(member).SyncAsync()));
            Assert.IsFalse(CanEdit(await new Device(other).SyncAsync()));
        }
        var changes = await device.SyncAsync();
        Assert.AreEqual(key, Rows(first, "field_definitions").Single(f => f.GetProperty("id").GetString() == fieldId).GetProperty("key").GetString());
        Assert.IsTrue(Rows(changes, "project_templates").Any(t => t.GetProperty("id").GetString() == templateId));
        Assert.IsTrue(Rows(changes, "template_statuses").Any(t => t.GetProperty("templateId").GetString() == templateId));
        Assert.IsTrue(Rows(changes, "template_sections").Any(t => t.GetProperty("templateId").GetString() == templateId));
        Assert.IsTrue(Rows(changes, "template_field_defaults").Any(t => t.GetProperty("templateId").GetString() == templateId));
        Assert.AreEqual("G0", Rows(changes, "template_milestones").Single(t => t.GetProperty("templateId").GetString() == templateId)
            .GetProperty("title").GetString());

        // Archived again (a template first: a section a template uses keeps applying to projects).
        Assert.IsTrue((await admin.CallAsync(HttpMethod.Post, $"/api/v1/config/templates/{templateId}/archive")).GetProperty("isArchived").GetBoolean());
        Assert.IsTrue((await admin.CallAsync(HttpMethod.Post, $"/api/v1/config/fields/{fieldId}/archive")).GetProperty("isArchived").GetBoolean());
        Assert.IsTrue((await admin.CallAsync(HttpMethod.Post, $"/api/v1/config/sections/{sectionId}/archive")).GetProperty("isArchived").GetBoolean());
    }

    /// <summary>The admin's device: the sync with the admin's session.</summary>
    private sealed class AdminDevice(ApiSession admin)
    {
        private readonly Guid clientId = Guid.CreateVersion7();
        private long cursor;
        private int epoch;

        public async Task<JsonElement> SyncAsync(object[]? mutations = null, string? table = null)
        {
            var sent = (mutations ?? []).Select(m => table is null ? m : WithTable(m, table)).ToArray();
            var response = await admin.CallAsync(HttpMethod.Post, "/api/v1/sync", new
            {
                clientId, appVersion = "contract", since = cursor, accessEpoch = epoch, limit = 1000, mutations = sent,
                resolved = Array.Empty<object>(), pull = true, pending = 0, conflicts = 0,
            });
            epoch = response.GetProperty("accessEpoch").GetInt32();
            cursor = response.GetProperty("cursor").GetInt64();
            return response;
        }

        /// <summary>A mutation of <see cref="Patch"/> (an <c>items</c> patch) for another table.</summary>
        private static Dictionary<string, object?> WithTable(object mutation, string table)
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(mutation))!;
            fields["table"] = table;
            return fields;
        }
    }
}
