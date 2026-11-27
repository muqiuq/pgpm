using System.Text.Json.Nodes;
using PGPM.Config;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Sync;
using PGPM.Tests.Sync;

namespace PGPM.Tests.Content;

/// <summary>
/// Configuration rows for the content tests, written straight into the run's database (names unique per call, never a
/// default status: other classes use the same database at the same time), and the sync rows the client inserts.
/// </summary>
internal static class ContentKit
{
    public static string Tag() => Guid.NewGuid().ToString("N")[..10];

    public static async Task<Guid> StatusAsync(TestApp app, ItemType type, StatusCategory category = StatusCategory.Open, bool archived = false)
    {
        await using var db = app.NewDb();
        var status = new StatusEntity { Id = Ids.New(), ItemType = type, Name = $"{type} {Tag()}", Category = category, IsArchived = archived };
        db.Statuses.Add(status);
        await db.SaveChangesAsync();
        return status.Id;
    }

    public static async Task<Guid> SectionAsync(TestApp app, string[] appliesTo, params (Guid Group, SectionAccess Level)[] access)
    {
        await using var db = app.NewDb();
        var section = new SectionEntity { Id = Ids.New(), Name = $"Section {Tag()}", AppliesTo = [.. appliesTo] };
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        db.SectionGroups.AddRange(access.Select(a => new SectionGroupEntity { SectionId = section.Id, GroupId = a.Group, Access = a.Level }));
        await db.SaveChangesAsync();
        return section.Id;
    }

    public static async Task<(Guid Id, string Key)> FieldAsync(TestApp app, Guid sectionId, FieldType type, JsonObject? config = null, bool required = false)
    {
        await using var db = app.NewDb();
        var field = new FieldDefinitionEntity
        {
            Id = Ids.New(), SectionId = sectionId, Key = $"f_{Tag()}", Label = $"{type}", Type = type, Config = (config ?? []).ToJsonString(),
            Required = required,
        };
        db.FieldDefinitions.Add(field);
        await db.SaveChangesAsync();
        return (field.Id, field.Key);
    }

    public static async Task<Guid> TemplateAsync(TestApp app, params Guid[] allowedStatuses)
    {
        await using var db = app.NewDb();
        var template = new ProjectTemplateEntity { Id = Ids.New(), Name = $"Template {Tag()}" };
        db.ProjectTemplates.Add(template);
        await db.SaveChangesAsync();
        db.TemplateStatuses.AddRange(allowedStatuses.Select(s => new TemplateStatusEntity { Id = Ids.New(), TemplateId = template.Id, StatusId = s }));
        await db.SaveChangesAsync();
        return template.Id;
    }

    public static JsonObject Item(string type, string title, Guid? status, JsonObject? more = null)
    {
        var row = new JsonObject { ["type"] = type, ["title"] = title, ["statusId"] = status?.ToString(), ["tags"] = new JsonArray() };
        foreach (var (key, value) in more ?? [])
        {
            row[key] = value?.DeepClone();
        }
        return row;
    }

    /// <summary>The results' status or code, in order.</summary>
    public static List<string?> Codes(JsonObject answer) => SyncKit.Arr(answer, "results").Select(r => SyncKit.Str(r, "code") ?? SyncKit.Str(r, "status")).ToList();
}
