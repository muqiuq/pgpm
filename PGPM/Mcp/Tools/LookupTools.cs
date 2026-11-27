using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Auth.Rights;
using PGPM.Config;
using PGPM.Data;
using PGPM.Persons;
using static PGPM.Mcp.Tools.ToolArgs;

namespace PGPM.Mcp.Tools;

/// <summary>
/// <c>search_persons</c> and <c>list_config</c> (docs/03 §7.3): the directory every user reads, and what PGPM is configured
/// with as the user sees it (sections and fields of hidden sections never). Both need no right.
/// </summary>
public static class LookupTools
{
    public static IReadOnlyList<ToolSpec> All =>
    [
        new("search_persons", "Search persons",
            "The people directory (Microsoft Entra accounts and manual persons), by name, e-mail, job title, department or company. Active persons " +
            "only unless includeInactive. userId is set for a person who is a PGPM user (search_items' owner).",
            ToolArgs.Schema([
                Text("text", "Words in the name, e-mail, job title, department or company"),
                Choice("userType", "Members or guests of the tenant", ["Member", "Guest"]),
                Choice("source", "From Microsoft or entered by hand", ["microsoft", "manual"]),
                Bool("includeInactive", "Also disabled or removed accounts"),
                .. PageArgs,
            ]),
            null, Level.None, ToolKind.Read, SearchPersonsAsync),
        new("list_config", "List the configuration",
            "What PGPM is configured with: the groups items are shared with; the item types with their fields and whether this token may " +
            "create them; the statuses per type; the sections you may see with their fields (keys, types, options, rating scales, score " +
            "definitions) and whether you may edit them; the project templates with their statuses, sections, milestones and gates; the " +
            "assignment roles. include narrows it to some of these.",
            ToolArgs.Schema(Texts("include", "Only these parts", ConfigParts.All.Select(p => p.Key))), null, Level.None, ToolKind.Read, ListConfigAsync),
    ];

    private static async Task<object> SearchPersonsAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var paging = ToolPaging.From(args);
        var persons = db.Persons.AsNoTracking().Where(p => !p.Deleted);
        if (OptionalBool(args, "includeInactive") != true)
        {
            persons = persons.Where(p => p.IsActive);
        }
        if (OptionalChoice(args, "userType", ["Member", "Guest"]) is { } userType)
        {
            persons = persons.Where(p => p.UserType == userType);
        }
        if (OptionalChoice(args, "source", ["microsoft", "manual"]) is { } source)
        {
            var wanted = source == "microsoft" ? PersonSource.Microsoft : PersonSource.Manual;
            persons = persons.Where(p => p.Source == wanted);
        }
        if (SearchText.Pattern(OptionalText(args, "text", 200)) is { } pattern)
        {
            persons = persons.Where(p => EF.Functions.ILike(p.DisplayName, pattern) || (p.Email != null && EF.Functions.ILike(p.Email, pattern))
                || (p.UserPrincipalName != null && EF.Functions.ILike(p.UserPrincipalName, pattern))
                || (p.JobTitle != null && EF.Functions.ILike(p.JobTitle, pattern)) || (p.Department != null && EF.Functions.ILike(p.Department, pattern))
                || (p.CompanyName != null && EF.Functions.ILike(p.CompanyName, pattern)));
        }
        var page = await paging.ReadAsync(persons.OrderBy(p => p.DisplayName).ThenBy(p => p.Id), ct);
        ctx.Summary = $"{page.Total} persons";
        return new
        {
            items = page.Items.Select(p => new
            {
                id = p.Id, displayName = p.DisplayName, email = p.Email, jobTitle = p.JobTitle, department = p.Department, companyName = p.CompanyName,
                userType = p.UserType, source = p.Source.ToString().ToLowerInvariant(), active = p.IsActive, userId = p.UserId,
            }),
            nextCursor = page.NextCursor,
            total = page.Total,
        };
    }

    private static async Task<object> ListConfigAsync(JsonElement args, ToolContext ctx, PgpmDbContext db, CancellationToken ct)
    {
        var include = TextsOf(args, "include", 20, 64);
        var unknown = include.Where(i => ConfigParts.All.All(p => p.Key != i)).ToList();
        if (unknown.Count > 0)
        {
            throw ToolException.Invalid($"include takes {string.Join(", ", ConfigParts.All.Select(p => p.Key))}.");
        }
        var parts = ConfigParts.All.Where(p => include.Count == 0 || include.Contains(p.Key)).ToList();
        var json = new JsonObject();
        foreach (var part in parts)
        {
            json[part.Key] = await part.Read(ctx, db, ct);
        }
        ctx.Summary = string.Join(", ", parts.Select(p => p.Key));
        return json;
    }
}

/// <summary>A part of <c>list_config</c>'s answer.</summary>
public sealed record ConfigPart(string Key, Func<ToolContext, PgpmDbContext, CancellationToken, Task<JsonNode?>> Read);

/// <summary>
/// What <c>list_config</c> returns: the groups, the item types, the statuses per type, the sections the user may view with
/// their fields (score definitions included; hidden sections never), the project templates and the assignment roles.
/// </summary>
public static class ConfigParts
{
    public static readonly ConfigPart Groups = new("groups", async (_, db, ct) =>
    {
        var groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).Select(g => new { g.Id, g.Name, g.Description }).ToListAsync(ct);
        return new JsonArray([.. groups.Select(g => (JsonNode)new JsonObject
        {
            ["id"] = g.Id.ToString(), ["name"] = g.Name, ["description"] = g.Description,
        })]);
    });

    public static readonly ConfigPart ItemTypes = new("itemTypes", (ctx, _, _) => Task.FromResult<JsonNode?>(new JsonArray([.. ItemKinds.All.Select(k =>
        (JsonNode)new JsonObject
        {
            ["type"] = k.Name,
            ["keyPrefix"] = Items.ItemEntity.Prefix(k.Type),
            ["description"] = k.Description,
            ["fields"] = new JsonArray([.. ItemKinds.Common.Concat(k.Fields).Select(f => (JsonNode)f.Arg)]),
            ["hasStatus"] = k.HasStatus,
            ["hasSections"] = ItemKinds.HasSections(k.Type),
            ["canCreate"] = k.Creatable && ctx.Call.Kind == McpTokenKind.Write && ctx.Rights.Has(k.Area, Level.Create),
        })])));

    public static readonly ConfigPart Statuses = new("statuses", async (ctx, db, ct) =>
    {
        var config = await ctx.ConfigAsync(db, ct);
        return new JsonObject(ItemKinds.All.Where(k => k.HasStatus).Select(k => KeyValuePair.Create(k.Name, (JsonNode?)new JsonArray([..
            config.Statuses.Where(s => s.ItemType == k.Type).Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Id.ToString(), ["name"] = s.Name, ["category"] = ToolConfig.EnumText(s.Category), ["isDefault"] = s.IsDefault,
                ["maxDays"] = s.MaxDays, ["archived"] = s.IsArchived,
            })]))));
    });

    public static readonly ConfigPart Sections = new("sections", async (ctx, db, ct) =>
    {
        var config = await ctx.ConfigAsync(db, ct);
        return new JsonArray([.. config.SectionList.Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Id.ToString(),
            ["name"] = s.Name,
            ["description"] = s.Description,
            ["appliesTo"] = new JsonArray([.. s.AppliesTo.Select(a => (JsonNode)a)]),
            ["defaultFor"] = new JsonArray([.. s.DefaultFor.Select(a => (JsonNode)a)]),
            ["canEdit"] = ctx.Call.Kind == McpTokenKind.Write && config.EditableSections.Contains(s.Id),
            ["archived"] = s.IsArchived,
            ["fields"] = new JsonArray([.. config.Fields.Where(f => f.SectionId == s.Id).Select(f =>
            {
                var field = new JsonObject
                {
                    ["id"] = f.Id.ToString(), ["key"] = f.Key, ["label"] = f.Label, ["type"] = ToolConfig.EnumText(f.Type), ["helpText"] = f.HelpText,
                    ["required"] = f.Required, ["archived"] = f.IsArchived, ["config"] = JsonNode.Parse(f.Config),
                };
                if (f.DefaultValue is { } d)
                {
                    field["defaultValue"] = JsonNode.Parse(d);
                }
                if (f.Type == FieldType.Score && ScoreEvaluator.Max(config.Specs[f.Key]) is { } max)
                {
                    field["max"] = max;
                }
                return (JsonNode)field;
            })]),
        })]);
    });

    public static readonly ConfigPart Templates = new("templates", async (ctx, db, ct) =>
    {
        var config = await ctx.ConfigAsync(db, ct);
        var templates = await db.ProjectTemplates.AsNoTracking().Where(t => !t.Deleted && !t.IsArchived).OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .ToListAsync(ct);
        var list = new JsonArray();
        foreach (var template in templates)
        {
            var parts = await ItemSetup.PartsAsync(db, config, template, ct);
            list.Add(new JsonObject
            {
                ["id"] = template.Id.ToString(),
                ["name"] = template.Name,
                ["description"] = template.Description,
                ["defaultStatus"] = config.Status(ItemSetup.ProjectStatus(config, parts))?.Name,
                ["statuses"] = new JsonArray([.. (await config.AllowedAsync(db, template.Id, ct)).Select(s => (JsonNode)s.Name)]),
                ["sections"] = new JsonArray([.. parts.Sections.Where(s => config.Sections.ContainsKey(s.SectionId))
                    .Select(s => (JsonNode)config.Sections[s.SectionId].Name)]),
                ["defaultShares"] = JsonNode.Parse(template.DefaultShares),
                ["milestones"] = new JsonArray([.. parts.Milestones.Select(m => (JsonNode)new JsonObject
                {
                    ["kind"] = ToolConfig.EnumText(m.Kind),
                    ["title"] = m.Title,
                    ["offsetDays"] = m.OffsetDays,
                    ["dod"] = new JsonArray([.. ItemSetup.DodTexts(m.Dod).Select(t => (JsonNode)t)]),
                    ["onGoStatus"] = config.Status(m.OnGoStatusId)?.Name,
                    ["onNoGoStatus"] = config.Status(m.OnNoGoStatusId)?.Name,
                })]),
            });
        }
        return list;
    });

    public static readonly ConfigPart Roles = new("roles", async (_, db, ct) =>
    {
        var roles = await db.AssignmentRoles.AsNoTracking().Where(r => !r.Deleted && !r.IsArchived).OrderBy(r => r.SortOrder).ThenBy(r => r.Name)
            .Select(r => new { r.Name, r.Description }).ToListAsync(ct);
        return new JsonArray([.. roles.Select(r => (JsonNode)new JsonObject { ["name"] = r.Name, ["description"] = r.Description })]);
    });

    public static readonly IReadOnlyList<ConfigPart> All = [Groups, ItemTypes, Statuses, Sections, Templates, Roles];
}
