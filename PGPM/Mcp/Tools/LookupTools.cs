using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Auth.Rights;
using PGPM.Data;
using PGPM.Persons;
using static PGPM.Mcp.Tools.ToolArgs;

namespace PGPM.Mcp.Tools;

/// <summary>
/// <c>search_persons</c> and <c>list_config</c> (docs/03 §7.3): the directory every user reads, and what PGPM is configured
/// with. Both need no right.
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
            "What PGPM is configured with: the groups items are shared with, and the item types with their fields and whether this token may " +
            "create them.",
            ToolArgs.Schema(), null, Level.None, ToolKind.Read, ListConfigAsync),
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
        var json = new JsonObject();
        foreach (var part in ConfigParts.All)
        {
            json[part.Key] = await part.Read(ctx, db, ct);
        }
        ctx.Summary = string.Join(", ", ConfigParts.All.Select(p => p.Key));
        return json;
    }
}

/// <summary>A part of <c>list_config</c>'s answer.</summary>
public sealed record ConfigPart(string Key, Func<ToolContext, PgpmDbContext, CancellationToken, Task<JsonNode?>> Read);

/// <summary>
/// What <c>list_config</c> returns: the groups and the item types now. Phase 3 adds <c>statuses</c> (per type), phase 4
/// <c>sections</c>, <c>fields</c> (with score definitions) and <c>templates</c>, phase 5 <c>mapTemplates</c>.
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
            ["canCreate"] = k.Creatable && ctx.Call.Kind == McpTokenKind.Write && ctx.Rights.Has(k.Area, Level.Create),
        })])));

    public static readonly IReadOnlyList<ConfigPart> All = [Groups, ItemTypes];
}
