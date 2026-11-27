using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PGPM.Api;
using PGPM.Api.V1.Contracts;
using PGPM.Audit;
using PGPM.Auth;
using PGPM.Auth.Rights;
using PGPM.Config;
using PGPM.Data;
using PGPM.Items;
using PGPM.Lib.Helper;
using PGPM.Maps;
using PGPM.Sharing;
using PGPM.Sync;

namespace PGPM.Packages;

/// <summary>
/// Configuration packages (docs/03 §17): checks a package (the schema and <see cref="PackageChecker"/>'s rules, with the
/// packages it requires), compares each entity with the database (<b>dry run</b>: create, update, unchanged, or conflict
/// with a local edit), applies it in one transaction through a <see cref="SyncWrite"/> (every device gets it live), and
/// exports the configuration as a package.
/// <list type="bullet">
/// <item>Entities are found by <c>config_keys</c>; without a key the importer adopts one with the same natural key (a group
///   by its seeded id or normalized name, a status by item type and name, a field by key) instead of making a second.</item>
/// <item>An entity is compared in its package form (keys instead of ids, children in order, integer orders as ranks):
///   equal is <i>unchanged</i>; different is an <i>update</i> when the database still holds what was imported last
///   (<c>config_keys.hash</c>), else a <i>conflict</i> – a local edit, which wins unless <c>overwrite</c> names the key.</item>
/// <item>Integer <c>sortOrder</c>/<c>z</c> become fractional keys where the column is one; matrix presets become matrix map
///   templates with one layer; saved views are shared views owned by the importing user; groups and rights need an admin.</item>
/// <item>A second import of the same package changes nothing.</item>
/// </list>
/// </summary>
public sealed class PackageImporter(PgpmDbContext db, SyncWriter writer, ConfigService config, SessionCache sessions, IHttpContextAccessor accessor)
{
    public const string StatePrefix = "package:";

    /// <summary>The package being applied (the children's <c>config_keys.package</c>).</summary>
    private string? importing;

    private static readonly string[] Order = ["groups", "statuses", "sections", "fields", "templates", "roles", "savedViews", "mapTemplates", "matrixPresets"];

    /// <summary>The <c>config_keys.entity</c> of a package collection (its table's wire name).</summary>
    private static readonly Dictionary<string, string> Tables = new()
    {
        ["groups"] = "groups", ["statuses"] = "statuses", ["sections"] = "sections", ["fields"] = "field_definitions", ["templates"] = "project_templates",
        ["roles"] = "assignment_roles", ["savedViews"] = "saved_views", ["mapTemplates"] = "maps", ["matrixPresets"] = "maps",
    };

    private static readonly Dictionary<string, string> Labels = new()
    {
        ["groups"] = "group", ["statuses"] = "status", ["sections"] = "section", ["fields"] = "field", ["templates"] = "template", ["roles"] = "role",
        ["savedViews"] = "view", ["mapTemplates"] = "map template", ["matrixPresets"] = "matrix preset",
    };

    // --- the list ----------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<PackageInfo>> ListAsync(CancellationToken ct)
    {
        var states = await StatesAsync(ct);
        var list = BuiltInPackages.Names.Select(name =>
        {
            var p = BuiltInPackages.Load(name);
            return new PackageInfo(name, Str(p, "title") ?? name, Str(p, "description"), Str(p, "version") ?? "", true, states.GetValueOrDefault(name));
        }).ToList();
        list.AddRange(states.Where(s => !BuiltInPackages.Exists(s.Key))
            .Select(s => new PackageInfo(s.Key, s.Value.Title ?? s.Key, null, s.Value.Version, false, s.Value)));
        return list;
    }

    private async Task<Dictionary<string, PackageImportState>> StatesAsync(CancellationToken ct)
    {
        var rows = await db.KeyValues.AsNoTracking().Where(k => k.Key.StartsWith(StatePrefix)).ToListAsync(ct);
        var states = new Dictionary<string, PackageImportState>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            try
            {
                if (JsonSerializer.Deserialize<PackageImportState>(row.Value, JsonSerializerOptions.Web) is { } state)
                {
                    states[row.Key[StatePrefix.Length..]] = state;
                }
            }
            catch (JsonException)
            {
                // Not ours.
            }
        }
        return states;
    }

    // --- checking ----------------------------------------------------------------------------------------------------

    /// <summary>The package of a request: a built-in one by name, or the JSON given; 400 <c>invalid_package</c> otherwise.</summary>
    public static JsonObject PackageOf(PackageRequest request)
    {
        if (request.BuiltIn is { } name)
        {
            return BuiltInPackages.Exists(name) ? BuiltInPackages.Load(name) : throw ApiException.Invalid("builtIn", $"No built-in package {name}.");
        }
        if (request.Package is null)
        {
            throw ApiException.Invalid("package", "A package (JSON) or the name of a built-in one.");
        }
        // Parsed again from its text: duplicate keys are refused, and the request's nodes stay untouched.
        try
        {
            return BuiltInPackages.Parse(request.Package.ToJsonString()).AsObject();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw Invalid([e.Message]);
        }
    }

    private static ApiException Invalid(IReadOnlyList<string> problems) =>
        new(StatusCodes.Status400BadRequest, "invalid_package", $"The package has {problems.Count} problem(s).",
            extensions: new Dictionary<string, object?> { ["problems"] = problems.Take(200).ToList() });

    /// <summary>
    /// The schema and the rules (docs/03 §17) over the package and the packages it requires (built-in ones as shipped,
    /// others as imported): the problems, empty when it may be imported. A required package must be imported first.
    /// </summary>
    public async Task<List<string>> CheckAsync(JsonObject package, CancellationToken ct)
    {
        var schema = BuiltInPackages.Schema;
        var problems = schema.Errors(package).Select(e => $"{Label(package)}: {(e.Path.Length == 0 ? "(top level)" : e.Path)}: {e.Message}").ToList();
        if (problems.Count > 0)
        {
            return problems;
        }
        var name = Str(package, "name")!;
        var set = new Dictionary<string, (string File, JsonObject Data)>(StringComparer.Ordinal) { [name] = (Label(package), package) };
        var states = await StatesAsync(ct);
        var pending = new Queue<string>(Arr(package, "requires").Select(r => r!.GetValue<string>()));
        while (pending.TryDequeue(out var required))
        {
            if (set.ContainsKey(required))
            {
                continue;
            }
            if (!states.ContainsKey(required))
            {
                problems.Add($"{Label(package)}: requires: package '{required}' is not imported yet: import it first");
                continue;
            }
            var data = BuiltInPackages.Exists(required) ? BuiltInPackages.Load(required) : await ExportAsync(required, ct);
            set[required] = ($"{required}.pgpm.json", data);
            foreach (var r in Arr(data, "requires"))
            {
                pending.Enqueue(r!.GetValue<string>());
            }
        }
        if (problems.Count > 0)
        {
            return problems;
        }
        return new PackageChecker(set, oneDefaultAcrossAll: false).Run();
    }

    private static string Label(JsonObject package) => $"{Str(package, "name") ?? "package"}.pgpm.json";

    // --- the dry run -------------------------------------------------------------------------------------------------

    private sealed record Plan(string Collection, string Key, JsonObject Entry, JsonObject Wanted, Guid? Id, bool Adopted, string Action,
        IReadOnlyList<string> Changed, bool Deleted);

    public async Task<PackageDiff> DryRunAsync(Viewer viewer, JsonObject package, IReadOnlyCollection<string>? overwrite, CancellationToken ct)
    {
        var problems = await CheckAsync(package, ct);
        if (problems.Count > 0)
        {
            throw Invalid(problems);
        }
        var (_, diff) = await PlanAsync(viewer, package, overwrite ?? [], ct);
        return diff;
    }

    private async Task<(List<Plan> Plans, PackageDiff Diff)> PlanAsync(Viewer viewer, JsonObject package, IReadOnlyCollection<string> overwrite,
        CancellationToken ct)
    {
        var keys = await KeyMap.LoadAsync(db, ct);
        var plans = new List<Plan>();
        foreach (var collection in Order)
        {
            foreach (var (key, node) in Obj(package, collection))
            {
                var entry = node!.AsObject();
                var wanted = Normalize(collection, entry);
                var table = Tables[collection];
                var (id, adopted) = keys.Find(table, key) is { } known ? (known, false) : (await AdoptAsync(collection, key, entry, ct), true);
                if (id is null)
                {
                    plans.Add(new Plan(collection, key, entry, wanted, null, false, PackageActions.Create, [], false));
                    continue;
                }
                var current = await DescribeAsync(collection, id.Value, keys, ct);
                if (current is null)
                {
                    // Deleted here since the last import: a local edit too.
                    var action = overwrite.Contains(key) ? PackageActions.Create : PackageActions.Conflict;
                    plans.Add(new Plan(collection, key, entry, wanted, null, false, action, ["(deleted)"], true));
                    continue;
                }
                var changed = Differences(current, wanted);
                if (changed.Count == 0)
                {
                    plans.Add(new Plan(collection, key, entry, wanted, id, adopted, PackageActions.Unchanged, [], false));
                    continue;
                }
                var untouched = !adopted && keys.HashOf(table, key) is { } hash && hash == CanonicalJson.Hash(current);
                plans.Add(new Plan(collection, key, entry, wanted, id, adopted, untouched || overwrite.Contains(key) ? PackageActions.Update : PackageActions.Conflict,
                    changed, false));
            }
        }
        var entries = plans.Select(p => new PackageDiffEntry(p.Key, Labels[p.Collection], Str(p.Entry, "name") ?? Str(p.Entry, "label") ?? p.Key, p.Action,
            p.Adopted, p.Changed)).ToList();
        var needsAdmin = plans.Any(p => p.Collection == "groups" && p.Action is PackageActions.Create or PackageActions.Update);
        var counts = new PackageDiffCounts(Count(PackageActions.Create), Count(PackageActions.Update), Count(PackageActions.Unchanged), Count(PackageActions.Conflict));
        return (plans, new PackageDiff(Str(package, "name")!, Str(package, "version")!, entries, counts, needsAdmin, viewer.IsAdmin));

        int Count(string action) => plans.Count(p => p.Action == action);
    }

    /// <summary>The top-level members whose values differ.</summary>
    private static List<string> Differences(JsonObject current, JsonObject wanted) =>
        current.Select(p => p.Key).Union(wanted.Select(p => p.Key))
            .Where(k => CanonicalJson.Of(current[k]) != CanonicalJson.Of(wanted[k])).Order(StringComparer.Ordinal).ToList();

    /// <summary>An existing entity with the natural key of one the package brings (docs/03 §17 "Adoption"), not yet keyed.</summary>
    private async Task<Guid?> AdoptAsync(string collection, string key, JsonObject entry, CancellationToken ct)
    {
        var keyed = db.ConfigKeys.Where(k => k.Entity == Tables[collection]).Select(k => k.Id);
        switch (collection)
        {
            case "groups":
                var seeded = key switch { "group:members" => GroupEntity.MembersId, "group:viewers" => GroupEntity.ViewersId, _ => (Guid?)null };
                var normalized = GroupEntity.Normalize(Str(entry, "name")!);
                return await db.Groups.Where(g => (g.Id == seeded || g.NormalizedName == normalized) && !keyed.Contains(g.Id))
                    .OrderBy(g => g.Id == seeded ? 0 : 1).Select(g => (Guid?)g.Id).FirstOrDefaultAsync(ct);
            case "statuses":
                var type = EnumText.Parse<ItemType>(Str(entry, "itemType")!);
                var name = Str(entry, "name")!.Trim().ToLower();
                return await db.Statuses.Where(s => s.ItemType == type && !s.Deleted && !s.IsArchived && s.Name.ToLower() == name && !keyed.Contains(s.Id))
                    .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
            case "fields":
                var fieldKey = key["field:".Length..];
                return await db.FieldDefinitions.Where(f => f.Key == fieldKey && !f.Deleted && !keyed.Contains(f.Id)).Select(f => (Guid?)f.Id).FirstOrDefaultAsync(ct);
            default:
                return null;
        }
    }

    // --- the package form of an entity (both sides of the comparison) -------------------------------------------------

    /// <summary>A package entry with its defaults filled in and orders as ranks: what the database would hold.</summary>
    private static JsonObject Normalize(string collection, JsonObject e)
    {
        JsonNode? Opt(string key) => e[key]?.DeepClone();
        switch (collection)
        {
            case "groups":
                return new JsonObject { ["name"] = Opt("name"), ["description"] = Opt("description"), ["rights"] = Sorted(Obj(e, "rights")) };
            case "statuses":
                return new JsonObject
                {
                    ["itemType"] = Opt("itemType"), ["name"] = Opt("name"), ["color"] = Opt("color"), ["icon"] = Opt("icon"), ["category"] = Opt("category"),
                    ["sortOrder"] = Opt("sortOrder"), ["isDefault"] = Bool(e, "isDefault"), ["maxDays"] = Opt("maxDays"),
                };
            case "sections":
                return new JsonObject
                {
                    ["name"] = Opt("name"), ["description"] = Opt("description"), ["appliesTo"] = SortedStrings(Arr(e, "appliesTo")),
                    ["defaultFor"] = SortedStrings(Arr(e, "defaultFor")), ["sortOrder"] = Opt("sortOrder"), ["collapsedByDefault"] = Bool(e, "collapsedByDefault"),
                    ["access"] = new JsonArray([.. Arr(e, "access").Select(a => (JsonNode)new JsonObject { ["group"] = Opt2(a, "group"), ["level"] = Opt2(a, "level") })
                        .OrderBy(a => Str(a, "group"), StringComparer.Ordinal)]),
                };
            case "fields":
                return new JsonObject
                {
                    ["section"] = Opt("section"), ["label"] = Opt("label"), ["helpText"] = Opt("helpText"), ["type"] = Opt("type"),
                    ["config"] = Opt("config") ?? new JsonObject(), ["required"] = Bool(e, "required"), ["defaultValue"] = Opt("defaultValue"),
                    ["sortOrder"] = Opt("sortOrder"),
                };
            case "templates":
                return new JsonObject
                {
                    ["name"] = Opt("name"), ["description"] = Opt("description"), ["color"] = Opt("color"), ["icon"] = Opt("icon"),
                    ["sortOrder"] = Opt("sortOrder") ?? 0, ["defaultStatus"] = Opt("defaultStatus"), ["allowedStatuses"] = SortedStrings(Arr(e, "allowedStatuses")),
                    ["sections"] = Arr(e, "sections").DeepClone(), ["fieldDefaults"] = Sorted(Obj(e, "fieldDefaults")),
                    ["defaultShares"] = new JsonArray([.. Arr(e, "defaultShares").Select(s => (JsonNode)new JsonObject { ["group"] = Opt2(s, "group"), ["access"] = Opt2(s, "access") })
                        .OrderBy(s => Str(s, "group"), StringComparer.Ordinal)]),
                    ["milestones"] = new JsonArray([.. Arr(e, "milestones").OrderBy(m => Num(m, "sortOrder")).Select(m => (JsonNode)new JsonObject
                    {
                        ["key"] = Opt2(m, "key"), ["kind"] = Opt2(m, "kind"), ["title"] = Opt2(m, "title"), ["description"] = Opt2(m, "description"),
                        ["offsetDays"] = Opt2(m, "offsetDays"), ["dod"] = Arr(m, "dod").DeepClone(), ["onGoStatus"] = Opt2(m, "onGoStatus"),
                        ["onNoGoStatus"] = Opt2(m, "onNoGoStatus"),
                    })]),
                };
            case "roles":
                return new JsonObject { ["name"] = Opt("name"), ["description"] = Opt("description"), ["sortOrder"] = Opt("sortOrder") ?? 0 };
            case "savedViews":
                return new JsonObject
                {
                    ["itemType"] = Opt("itemType"), ["name"] = Opt("name"), ["description"] = Opt("description"), ["definition"] = Opt("definition") ?? new JsonObject(),
                    ["isShared"] = !Has(e, "isShared") || Bool(e, "isShared"), ["sortOrder"] = Opt("sortOrder") ?? 0,
                };
            case "mapTemplates":
            {
                var layers = Arr(e, "layers").OrderBy(l => Num(l, "sortOrder")).ToList();
                var rank = layers.Select((l, i) => (Str(l, "key")!, i)).ToDictionary(x => x.Item1, x => x.i);
                return new JsonObject
                {
                    ["name"] = Opt("name"), ["description"] = Opt("description"), ["settings"] = Opt("settings") ?? new JsonObject(),
                    ["layers"] = new JsonArray([.. layers.Select(l => (JsonNode)new JsonObject
                    {
                        ["key"] = Opt2(l, "key"), ["name"] = Opt2(l, "name"), ["isVisible"] = !Has(l, "isVisible") || Bool(l, "isVisible"),
                        ["isLocked"] = Bool(l, "isLocked"),
                    })]),
                    ["elements"] = new JsonArray([.. Arr(e, "elements").OrderBy(x => rank.GetValueOrDefault(Str(x, "layer")!)).ThenBy(x => Num(x, "z"))
                        .Select(x => (JsonNode)ElementForm(Str(x, "key")!, Str(x, "kind")!, Str(x, "layer")!, Num(x, "x")!.Value, Num(x, "y")!.Value,
                            Num(x, "width")!.Value, Num(x, "height")!.Value, Str(x, "text"), x!["points"]?.DeepClone(), x["style"]?.DeepClone(),
                            x["config"]?.DeepClone()))]),
                };
            }
            default: // matrixPresets
                return new JsonObject
                {
                    ["name"] = Opt("name"), ["description"] = Opt("description"), ["settings"] = Opt("settings") ?? new JsonObject(), ["matrix"] = Opt("matrix"),
                };
        }

        static JsonNode? Opt2(JsonNode? node, string key) => node?[key]?.DeepClone();
    }

    private static JsonObject ElementForm(string key, string kind, string layer, decimal x, decimal y, decimal width, decimal height, string? text,
        JsonNode? points, JsonNode? style, JsonNode? config) => new()
    {
        ["key"] = key, ["kind"] = kind, ["layer"] = layer, ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height, ["text"] = text,
        ["points"] = points, ["style"] = style ?? new JsonObject(), ["config"] = config,
    };

    private static JsonObject Sorted(JsonObject o) => new(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));

    private static JsonArray SortedStrings(JsonArray a) => new([.. a.Select(x => x!.GetValue<string>()).Order(StringComparer.Ordinal).Select(x => (JsonNode)x)]);

    /// <summary>The database's entity in its package form (as <see cref="Normalize"/> makes it); null when it is gone.</summary>
    private async Task<JsonObject?> DescribeAsync(string collection, Guid id, KeyMap keys, CancellationToken ct)
    {
        switch (collection)
        {
            case "groups":
            {
                var g = await db.Groups.AsNoTracking().Include(x => x.Rights).FirstOrDefaultAsync(x => x.Id == id, ct);
                return g is null ? null : new JsonObject
                {
                    ["name"] = g.Name, ["description"] = g.Description,
                    ["rights"] = new JsonObject(g.Rights.OrderBy(r => r.Area.ToString(), StringComparer.Ordinal)
                        .Select(r => KeyValuePair.Create(r.Area.ToString(), (JsonNode?)r.Level.ToString()))),
                };
            }
            case "statuses":
            {
                var s = await db.Statuses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.Deleted, ct);
                return s is null ? null : new JsonObject
                {
                    ["itemType"] = EnumText.Of(s.ItemType), ["name"] = s.Name, ["color"] = s.Color, ["icon"] = s.Icon, ["category"] = EnumText.Of(s.Category),
                    ["sortOrder"] = s.SortOrder, ["isDefault"] = s.IsDefault, ["maxDays"] = s.MaxDays,
                };
            }
            case "sections":
            {
                var s = await db.Sections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.Deleted, ct);
                if (s is null)
                {
                    return null;
                }
                var access = await db.SectionGroups.AsNoTracking().Where(g => g.SectionId == id).ToListAsync(ct);
                return new JsonObject
                {
                    ["name"] = s.Name, ["description"] = s.Description, ["appliesTo"] = Strings(s.AppliesTo.Order(StringComparer.Ordinal)),
                    ["defaultFor"] = Strings(s.DefaultFor.Order(StringComparer.Ordinal)), ["sortOrder"] = s.SortOrder, ["collapsedByDefault"] = s.CollapsedByDefault,
                    ["access"] = new JsonArray([.. access.Select(a => (JsonNode)new JsonObject { ["group"] = keys.KeyOf("groups", a.GroupId), ["level"] = EnumText.Of(a.Access) })
                        .OrderBy(a => Str(a, "group"), StringComparer.Ordinal)]),
                };
            }
            case "fields":
            {
                var f = await db.FieldDefinitions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.Deleted, ct);
                return f is null ? null : new JsonObject
                {
                    ["section"] = keys.KeyOf("sections", f.SectionId), ["label"] = f.Label, ["helpText"] = f.HelpText, ["type"] = EnumText.Of(f.Type),
                    ["config"] = JsonNode.Parse(f.Config), ["required"] = f.Required, ["defaultValue"] = f.DefaultValue is null ? null : JsonNode.Parse(f.DefaultValue),
                    ["sortOrder"] = f.SortOrder,
                };
            }
            case "templates":
            {
                var t = await db.ProjectTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.Deleted, ct);
                if (t is null)
                {
                    return null;
                }
                var statuses = await db.TemplateStatuses.AsNoTracking().Where(s => s.TemplateId == id && !s.Deleted).Select(s => s.StatusId).ToListAsync(ct);
                var sections = await db.TemplateSections.AsNoTracking().Where(s => s.TemplateId == id && !s.Deleted).OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
                    .Select(s => s.SectionId).ToListAsync(ct);
                var defaults = await db.TemplateFieldDefaults.AsNoTracking().Where(d => d.TemplateId == id && !d.Deleted).ToListAsync(ct);
                var milestones = (await db.TemplateMilestones.AsNoTracking().Where(m => m.TemplateId == id && !m.Deleted).ToListAsync(ct))
                    .OrderBy(m => m.SortOrder, StringComparer.Ordinal).ThenBy(m => m.Id).ToList();
                var shares = (JsonNode.Parse(t.DefaultShares) as JsonArray ?? []).OfType<JsonObject>()
                    .Select(s => (JsonNode)new JsonObject { ["group"] = keys.KeyOf("groups", Guid.Parse(Str(s, "groupId")!)), ["access"] = s["access"]?.DeepClone() })
                    .OrderBy(s => Str(s, "group"), StringComparer.Ordinal);
                var templateKey = keys.KeyOf("project_templates", id);
                return new JsonObject
                {
                    ["name"] = t.Name, ["description"] = t.Description, ["color"] = t.Color, ["icon"] = t.Icon, ["sortOrder"] = t.SortOrder,
                    ["defaultStatus"] = t.DefaultStatusId is { } ds ? keys.KeyOf("statuses", ds) : null,
                    ["allowedStatuses"] = Strings(statuses.Select(s => keys.KeyOf("statuses", s)).Order(StringComparer.Ordinal)),
                    ["sections"] = Strings(sections.Select(s => keys.KeyOf("sections", s))),
                    ["fieldDefaults"] = new JsonObject(defaults.Select(d => KeyValuePair.Create(keys.KeyOf("field_definitions", d.FieldId), JsonNode.Parse(d.Value)))
                        .OrderBy(p => p.Key, StringComparer.Ordinal)),
                    ["defaultShares"] = new JsonArray([.. shares]),
                    ["milestones"] = new JsonArray([.. milestones.Select(m => (JsonNode)new JsonObject
                    {
                        ["key"] = ChildKey(keys.KeyOf("template_milestones", m.Id), templateKey), ["kind"] = EnumText.Of(m.Kind), ["title"] = m.Title,
                        ["description"] = m.Description, ["offsetDays"] = m.OffsetDays, ["dod"] = JsonNode.Parse(m.Dod),
                        ["onGoStatus"] = m.OnGoStatusId is { } go ? keys.KeyOf("statuses", go) : null,
                        ["onNoGoStatus"] = m.OnNoGoStatusId is { } nogo ? keys.KeyOf("statuses", nogo) : null,
                    })]),
                };
            }
            case "roles":
            {
                var r = await db.AssignmentRoles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.Deleted, ct);
                return r is null ? null : new JsonObject { ["name"] = r.Name, ["description"] = r.Description, ["sortOrder"] = r.SortOrder };
            }
            case "savedViews":
            {
                var v = await db.SavedViews.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.Deleted, ct);
                return v is null ? null : new JsonObject
                {
                    ["itemType"] = EnumText.Of(v.ItemType), ["name"] = v.Name, ["description"] = v.Description,
                    ["definition"] = DefinitionToKeys(JsonNode.Parse(v.Definition) as JsonObject ?? [], keys), ["isShared"] = v.IsShared, ["sortOrder"] = v.SortOrder,
                };
            }
            case "mapTemplates":
            {
                var m = await db.Maps.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.Deleted, ct);
                if (m is null)
                {
                    return null;
                }
                var mapKey = keys.KeyOf("maps", id);
                var layers = (await db.MapLayers.AsNoTracking().Where(l => l.MapId == id && !l.Deleted).ToListAsync(ct))
                    .OrderBy(l => l.SortOrder, StringComparer.Ordinal).ThenBy(l => l.Id).ToList();
                var rank = layers.Select((l, i) => (l.Id, i)).ToDictionary(x => x.Id, x => x.i);
                var elements = (await db.MapElements.AsNoTracking().Where(x => x.MapId == id && !x.Deleted).ToListAsync(ct))
                    .OrderBy(x => rank.GetValueOrDefault(x.LayerId)).ThenBy(x => x.Z, StringComparer.Ordinal).ThenBy(x => x.Id).ToList();
                return new JsonObject
                {
                    ["name"] = m.Name, ["description"] = m.Description, ["settings"] = JsonNode.Parse(m.Settings),
                    ["layers"] = new JsonArray([.. layers.Select(l => (JsonNode)new JsonObject
                    {
                        ["key"] = ChildKey(keys.KeyOf("map_layers", l.Id), mapKey), ["name"] = l.Name, ["isVisible"] = l.IsVisible, ["isLocked"] = l.IsLocked,
                    })]),
                    ["elements"] = new JsonArray([.. elements.Select(x => (JsonNode)ElementForm(ChildKey(keys.KeyOf("map_elements", x.Id), mapKey), EnumText.Of(x.Kind),
                        ChildKey(keys.KeyOf("map_layers", x.LayerId), mapKey), (decimal)x.X, (decimal)x.Y, (decimal)x.Width, (decimal)x.Height, x.Text,
                        x.Points is null ? null : JsonNode.Parse(x.Points), JsonNode.Parse(x.Style), x.Config is null ? null : JsonNode.Parse(x.Config)))]),
                };
            }
            default:
            {
                var m = await db.Maps.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.Deleted, ct);
                return m is null ? null : new JsonObject
                {
                    ["name"] = m.Name, ["description"] = m.Description, ["settings"] = JsonNode.Parse(m.Settings),
                    ["matrix"] = m.Matrix is null ? null : MatrixToKeys(JsonNode.Parse(m.Matrix) as JsonObject ?? [], keys),
                };
            }
        }
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)v)]);

    /// <summary><c>template:ai_use_case/g1</c> → <c>g1</c> (a child's local key).</summary>
    private static string ChildKey(string key, string parentKey) =>
        key.StartsWith(parentKey + "/", StringComparison.Ordinal) ? key[(parentKey.Length + 1)..] : key;

    // Stored JSON holds ids where the package holds keys (docs/03 §17 "References"): statuses → statusIds, templates →
    // templateIds, sharedWith / groups → groupIds.

    private static readonly (string Package, string Stored, string Table)[] ViewFilterKeys =
        [("statuses", "statusIds", "statuses"), ("templates", "templateIds", "project_templates"), ("sharedWith", "groupIds", "groups")];

    private static readonly (string Package, string Stored, string Table)[] MatrixFilterKeys =
        [("statuses", "statusIds", "statuses"), ("templates", "templateIds", "project_templates"), ("groups", "groupIds", "groups")];

    private static JsonObject DefinitionToKeys(JsonObject definition, KeyMap keys)
    {
        var copy = definition.DeepClone().AsObject();
        if (copy["filters"] is JsonObject filters)
        {
            Swap(filters, ViewFilterKeys, toIds: false, keys);
        }
        return copy;
    }

    private static JsonObject MatrixToKeys(JsonObject matrix, KeyMap keys)
    {
        var copy = matrix.DeepClone().AsObject();
        if (copy["filter"] is JsonObject filter)
        {
            Swap(filter, MatrixFilterKeys, toIds: false, keys);
        }
        return copy;
    }

    private static void Swap(JsonObject filters, (string Package, string Stored, string Table)[] names, bool toIds, KeyMap keys)
    {
        foreach (var (packageName, stored, table) in names)
        {
            var (from, to) = toIds ? (packageName, stored) : (stored, packageName);
            if (filters[from] is not JsonArray values)
            {
                continue;
            }
            filters.Remove(from);
            filters[to] = new JsonArray([.. values.Select(v => (JsonNode)(toIds
                ? keys.Find(table, v!.GetValue<string>())?.ToString() ?? v!.GetValue<string>()
                : Guid.TryParse(v!.GetValue<string>(), out var id) ? keys.KeyOf(table, id) : v.GetValue<string>()))]);
        }
    }

    // --- applying ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// The dry run's plan, carried out in one transaction: creates and updates (conflicts only where <paramref name="overwrite"/>
    /// names them), the keys and hashes recorded, the package's import state. 403 <c>admin_required</c> when it would change
    /// groups and the user is no admin.
    /// </summary>
    public async Task<PackageApplyResult> ApplyAsync(Viewer viewer, JsonObject package, IReadOnlyCollection<string>? overwrite, ChangeSource source,
        CancellationToken ct)
    {
        var problems = await CheckAsync(package, ct);
        if (problems.Count > 0)
        {
            throw Invalid(problems);
        }
        await using var write = await writer.BeginAsync(viewer, source, accessor.HttpContext?.TraceIdentifier, ct);
        using var _ = ChangeContext.Use(source == ChangeSource.System ? ChangeContext.System : write.Context());
        var (plans, diff) = await PlanAsync(viewer, package, overwrite ?? [], ct);
        if (diff.NeedsAdmin && !viewer.IsAdmin)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "admin_required", "The package changes groups and their rights: an admin imports it.");
        }
        var name = Str(package, "name")!;
        importing = name;
        var keys = await KeyMap.LoadAsync(db, ct);
        var forget = new HashSet<Guid>();
        int created = 0, updated = 0;
        foreach (var plan in plans)
        {
            // Keys first: references within the package resolve to the entities as they are (or will be) here.
            if (plan.Id is { } id)
            {
                keys.Set(Tables[plan.Collection], plan.Key, id);
            }
        }
        foreach (var plan in plans)
        {
            var table = Tables[plan.Collection];
            if (plan.Action == PackageActions.Conflict)
            {
                continue;
            }
            if (plan.Action is PackageActions.Create or PackageActions.Update)
            {
                var id = plan.Id ?? Ids.New();
                keys.Set(table, plan.Key, id);
                await WriteAsync(write, viewer, plan, id, plan.Id is null, keys, forget, ct);
                if (plan.Id is null)
                {
                    created++;
                }
                else
                {
                    updated++;
                }
                await db.SaveChangesAsync(ct);
            }
            // The state as imported: what a later dry run compares the database with.
            var state = await DescribeAsync(plan.Collection, keys.Find(table, plan.Key)!.Value, keys, ct);
            await keys.RecordAsync(db, table, plan.Key, keys.Find(table, plan.Key)!.Value, name, state is null ? null : CanonicalJson.Hash(state), ct);
        }
        var stateKey = StatePrefix + name;
        var importState = new PackageImportState(Str(package, "version")!, Clock.UtcNow, viewer.UserId == Guid.Empty ? null : viewer.UserId,
            await db.Users.Where(u => u.Id == viewer.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct), Str(package, "title"));
        var row = await db.KeyValues.FirstOrDefaultAsync(k => k.Key == stateKey, ct);
        var value = JsonSerializer.Serialize(importState, JsonSerializerOptions.Web);
        if (row is null)
        {
            db.KeyValues.Add(new KeyValueEntity { Key = stateKey, Value = value, UpdatedAt = Clock.UtcNow });
        }
        else if (created + updated > 0 || row.Value != value)
        {
            row.Value = value;
            row.UpdatedAt = Clock.UtcNow;
        }
        var skipped = plans.Count(p => p.Action == PackageActions.Conflict);
        AuditLog.Target(new AuditTarget(ConfigService.AuditSubject, null, $"Package {name} {Str(package, "version")}"));
        AuditLog.Record(db, AuditEvents.PackageImported, $"Package {name} {Str(package, "version")}: {created} created, {updated} updated, "
            + $"{diff.Counts.Unchanged} unchanged, {skipped} kept (local edits)", source == ChangeSource.System ? AuditWho.Seed : AuditWho.From(accessor));
        await db.SaveChangesAsync(ct);
        await write.CommitAsync(ct);
        foreach (var user in forget.Concat(config.Forget).Distinct())
        {
            sessions.Forget(user);
        }
        config.Forget.Clear();
        return new PackageApplyResult(diff, created, updated, skipped);
    }

    private async Task WriteAsync(SyncWrite write, Viewer viewer, Plan plan, Guid id, bool create, KeyMap keys, HashSet<Guid> forget, CancellationToken ct)
    {
        var e = plan.Entry;
        Guid Ref(string table, string key) => keys.Find(table, key) ?? throw Invalid([$"{plan.Key}: '{key}' is not known here"]);
        switch (plan.Collection)
        {
            case "groups":
            {
                var name = Str(e, "name")!.Trim();
                var group = create ? null : await db.Groups.Include(g => g.Rights).FirstAsync(g => g.Id == id, ct);
                if (group is null)
                {
                    group = new GroupEntity { Id = id, Name = name, NormalizedName = GroupEntity.Normalize(name) };
                    db.Groups.Add(group);
                }
                group.Name = name;
                group.NormalizedName = GroupEntity.Normalize(name);
                group.Description = Str(e, "description");
                var rights = Obj(e, "rights").Select(r => (Area: Enum.Parse<Area>(r.Key), Level: Enum.Parse<Level>(r.Value!.GetValue<string>()))).ToList();
                if (!rights.ToHashSet().SetEquals(group.Rights.Select(r => (r.Area, r.Level))))
                {
                    db.GroupRights.RemoveRange(group.Rights);
                    await db.SaveChangesAsync(ct);
                    group.Rights = rights.Select(r => new GroupRightEntity { GroupId = id, Area = r.Area, Level = r.Level }).ToList();
                    forget.UnionWith(await db.UserGroups.Where(m => m.GroupId == id).Select(m => m.UserId).ToListAsync(ct));
                }
                break;
            }
            case "statuses":
            {
                var status = create ? new StatusEntity { Id = id } : await db.Statuses.FirstAsync(s => s.Id == id, ct);
                status.ItemType = EnumText.Parse<ItemType>(Str(e, "itemType")!);
                status.Name = Str(e, "name")!.Trim();
                status.Color = Str(e, "color")!;
                status.Icon = Str(e, "icon");
                status.Category = EnumText.Parse<StatusCategory>(Str(e, "category")!);
                status.SortOrder = (int)Num(e, "sortOrder")!.Value;
                status.MaxDays = Num(e, "maxDays") is { } days ? (int)days : null;
                status.IsArchived = false;
                var isDefault = Bool(e, "isDefault");
                if (isDefault && !status.IsDefault)
                {
                    var current = await db.Statuses.Where(s => s.ItemType == status.ItemType && s.IsDefault && !s.Deleted && s.Id != id).ToListAsync(ct);
                    current.ForEach(s => s.IsDefault = false);
                    await db.SaveChangesAsync(ct);
                }
                status.IsDefault = isDefault;
                if (create)
                {
                    db.Statuses.Add(status);
                }
                break;
            }
            case "sections":
            {
                var section = create ? new SectionEntity { Id = id } : await db.Sections.FirstAsync(s => s.Id == id, ct);
                section.Name = Str(e, "name")!.Trim();
                section.Description = Str(e, "description");
                section.AppliesTo = [.. Arr(e, "appliesTo").Select(x => x!.GetValue<string>())];
                section.DefaultFor = [.. Arr(e, "defaultFor").Select(x => x!.GetValue<string>())];
                section.SortOrder = (int)Num(e, "sortOrder")!.Value;
                section.CollapsedByDefault = Bool(e, "collapsedByDefault");
                section.IsArchived = false;
                if (create)
                {
                    db.Sections.Add(section);
                }
                await db.SaveChangesAsync(ct);
                await config.SetSectionAccessAsync(section,
                    [.. Arr(e, "access").Select(a => (Ref("groups", Str(a, "group")!), EnumText.Parse<SectionAccess>(Str(a, "level")!)))], ct);
                break;
            }
            case "fields":
            {
                var field = create ? new FieldDefinitionEntity { Id = id, Key = plan.Key["field:".Length..] } : await db.FieldDefinitions.FirstAsync(f => f.Id == id, ct);
                field.SectionId = Ref("sections", Str(e, "section")!);
                field.Label = Str(e, "label")!.Trim();
                field.HelpText = Str(e, "helpText");
                field.Type = EnumText.Parse<FieldType>(Str(e, "type")!);
                field.Config = (e["config"] ?? new JsonObject()).ToJsonString(WireJson.Options);
                field.Required = Bool(e, "required");
                field.DefaultValue = e["defaultValue"] is { } d && d.GetValueKind() != JsonValueKind.Null ? d.ToJsonString(WireJson.Options) : null;
                field.SortOrder = (int)Num(e, "sortOrder")!.Value;
                field.IsArchived = false;
                if (create)
                {
                    db.FieldDefinitions.Add(field);
                }
                break;
            }
            case "templates":
                await WriteTemplateAsync(write, plan, id, create, keys, ct);
                break;
            case "roles":
            {
                var role = create ? new AssignmentRoleEntity { Id = id } : await db.AssignmentRoles.FirstAsync(r => r.Id == id, ct);
                role.Name = Str(e, "name")!.Trim();
                role.Description = Str(e, "description");
                role.SortOrder = (int)(Num(e, "sortOrder") ?? 0);
                role.IsArchived = false;
                if (create)
                {
                    db.AssignmentRoles.Add(role);
                }
                break;
            }
            case "savedViews":
            {
                var view = create ? new SavedViewEntity { Id = id, OwnerUserId = viewer.UserId } : await db.SavedViews.FirstAsync(v => v.Id == id, ct);
                view.ItemType = EnumText.Parse<ViewItemType>(Str(e, "itemType")!);
                view.Name = Str(e, "name")!.Trim();
                view.Description = Str(e, "description");
                var definition = (e["definition"]?.DeepClone() as JsonObject) ?? [];
                if (definition["filters"] is JsonObject filters)
                {
                    Swap(filters, ViewFilterKeys, toIds: true, keys);
                }
                view.Definition = definition.ToJsonString(WireJson.Options);
                view.IsShared = !Has(e, "isShared") || Bool(e, "isShared");
                view.SortOrder = (int)(Num(e, "sortOrder") ?? 0);
                if (create)
                {
                    db.SavedViews.Add(view);
                }
                break;
            }
            case "mapTemplates":
                await WriteMapTemplateAsync(write, viewer, plan, id, create, keys, ct);
                break;
            default:
                await WriteMatrixPresetAsync(write, viewer, plan, id, create, keys, ct);
                break;
        }
    }

    private async Task WriteTemplateAsync(SyncWrite write, Plan plan, Guid id, bool create, KeyMap keys, CancellationToken ct)
    {
        var e = plan.Entry;
        Guid Ref(string table, string key) => keys.Find(table, key) ?? throw Invalid([$"{plan.Key}: '{key}' is not known here"]);
        var template = create ? new ProjectTemplateEntity { Id = id } : await db.ProjectTemplates.FirstAsync(t => t.Id == id, ct);
        template.Name = Str(e, "name")!.Trim();
        template.Description = Str(e, "description");
        template.Color = Str(e, "color");
        template.Icon = Str(e, "icon");
        template.SortOrder = (int)(Num(e, "sortOrder") ?? 0);
        template.DefaultStatusId = Str(e, "defaultStatus") is { } ds ? Ref("statuses", ds) : null;
        template.DefaultShares = new JsonArray([.. Arr(e, "defaultShares").Select(s => (JsonNode)new JsonObject
        {
            ["groupId"] = Ref("groups", Str(s, "group")!).ToString(), ["access"] = Str(s, "access"),
        })]).ToJsonString(WireJson.Options);
        template.IsArchived = false;
        if (create)
        {
            db.ProjectTemplates.Add(template);
        }
        await db.SaveChangesAsync(ct);
        DeleteBatchEntity? batch = null;
        Guid Batch() => (batch ??= write.NewBatch(DeleteRootKind.Other, "project_templates", id)).Id;

        var statusIds = Arr(e, "allowedStatuses").Select(s => Ref("statuses", s!.GetValue<string>())).ToList();
        var statuses = await db.TemplateStatuses.Where(s => s.TemplateId == id && !s.Deleted).ToListAsync(ct);
        foreach (var gone in statuses.Where(s => !statusIds.Contains(s.StatusId)))
        {
            (gone.Deleted, gone.DeleteBatchId) = (true, Batch());
        }
        db.TemplateStatuses.AddRange(statusIds.Where(s => statuses.All(x => x.StatusId != s))
            .Select(s => new TemplateStatusEntity { Id = Ids.New(), TemplateId = id, StatusId = s }));

        var sectionIds = Arr(e, "sections").Select(s => Ref("sections", s!.GetValue<string>())).ToList();
        var sections = await db.TemplateSections.Where(s => s.TemplateId == id && !s.Deleted).ToListAsync(ct);
        foreach (var gone in sections.Where(s => !sectionIds.Contains(s.SectionId)))
        {
            (gone.Deleted, gone.DeleteBatchId) = (true, Batch());
        }
        for (var i = 0; i < sectionIds.Count; i++)
        {
            var row = sections.FirstOrDefault(s => s.SectionId == sectionIds[i]);
            if (row is null)
            {
                db.TemplateSections.Add(new TemplateSectionEntity { Id = Ids.New(), TemplateId = id, SectionId = sectionIds[i], SortOrder = (i + 1) * 10 });
            }
            else if (row.SortOrder != (i + 1) * 10)
            {
                row.SortOrder = (i + 1) * 10;
            }
        }

        var defaults = Obj(e, "fieldDefaults").Select(d => (Field: Ref("field_definitions", d.Key), Value: d.Value)).ToList();
        var existing = await db.TemplateFieldDefaults.Where(d => d.TemplateId == id && !d.Deleted).ToListAsync(ct);
        foreach (var gone in existing.Where(d => defaults.All(x => x.Field != d.FieldId)))
        {
            (gone.Deleted, gone.DeleteBatchId) = (true, Batch());
        }
        foreach (var (field, value) in defaults)
        {
            var text = value?.ToJsonString(WireJson.Options) ?? "null";
            var row = existing.FirstOrDefault(d => d.FieldId == field);
            if (row is null)
            {
                db.TemplateFieldDefaults.Add(new TemplateFieldDefaultEntity { Id = Ids.New(), TemplateId = id, FieldId = field, Value = text });
            }
            else if (CanonicalJson.Of(JsonNode.Parse(row.Value)) != CanonicalJson.Of(value))
            {
                row.Value = text;
            }
        }

        var milestones = Arr(e, "milestones").OrderBy(m => Num(m, "sortOrder")).ToList();
        var sortKeys = FractionalKey.Sequence(milestones.Count);
        var rows = await db.TemplateMilestones.Where(m => m.TemplateId == id && !m.Deleted).ToListAsync(ct);
        var wantedIds = new HashSet<Guid>();
        for (var i = 0; i < milestones.Count; i++)
        {
            var m = milestones[i];
            var childKey = $"{plan.Key}/{Str(m, "key")}";
            var row = keys.Find("template_milestones", childKey) is { } known ? rows.FirstOrDefault(r => r.Id == known) : null;
            if (row is null)
            {
                row = new TemplateMilestoneEntity { Id = Ids.New(), TemplateId = id };
                db.TemplateMilestones.Add(row);
            }
            wantedIds.Add(row.Id);
            row.Kind = EnumText.Parse<MilestoneKind>(Str(m, "kind")!);
            row.Title = Str(m, "title")!.Trim();
            row.Description = Str(m, "description");
            row.OffsetDays = Num(m, "offsetDays") is { } days ? (int)days : null;
            var dod = Arr(m, "dod").DeepClone();
            if (CanonicalJson.Of(JsonNode.Parse(row.Dod)) != CanonicalJson.Of(dod))
            {
                row.Dod = dod.ToJsonString(WireJson.Options);
            }
            row.OnGoStatusId = Str(m, "onGoStatus") is { } go ? Ref("statuses", go) : null;
            row.OnNoGoStatusId = Str(m, "onNoGoStatus") is { } nogo ? Ref("statuses", nogo) : null;
            row.SortOrder = sortKeys[i];
            keys.Set("template_milestones", childKey, row.Id);
            await keys.RecordAsync(db, "template_milestones", childKey, row.Id, importing, null, ct);
        }
        foreach (var gone in rows.Where(r => !wantedIds.Contains(r.Id)))
        {
            (gone.Deleted, gone.DeleteBatchId) = (true, Batch());
        }
    }

    private async Task<MapEntity> MapRowAsync(Viewer viewer, Plan plan, Guid id, bool create, MapKind kind, CancellationToken ct)
    {
        var e = plan.Entry;
        var map = create ? new MapEntity { Id = id, OwnerUserId = viewer.UserId, Kind = kind } : await db.Maps.FirstAsync(m => m.Id == id, ct);
        map.Name = Str(e, "name")!.Trim();
        map.Description = Str(e, "description");
        var settings = e["settings"] ?? new JsonObject();
        if (CanonicalJson.Of(JsonNode.Parse(map.Settings)) != CanonicalJson.Of(settings))
        {
            map.Settings = settings.ToJsonString(WireJson.Options);
        }
        map.IsTemplate = true;
        if (create)
        {
            db.Maps.Add(map);
        }
        return map;
    }

    private async Task WriteMapTemplateAsync(SyncWrite write, Viewer viewer, Plan plan, Guid id, bool create, KeyMap keys, CancellationToken ct)
    {
        var e = plan.Entry;
        await MapRowAsync(viewer, plan, id, create, MapKind.Canvas, ct);
        await db.SaveChangesAsync(ct);
        DeleteBatchEntity? batch = null;
        Guid Batch() => (batch ??= write.NewBatch(DeleteRootKind.Other, "maps", id)).Id;

        var layerSpecs = Arr(e, "layers").OrderBy(l => Num(l, "sortOrder")).ToList();
        var layerKeys = FractionalKey.Sequence(layerSpecs.Count);
        var layers = await db.MapLayers.Where(l => l.MapId == id && !l.Deleted).ToListAsync(ct);
        var layerIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var i = 0; i < layerSpecs.Count; i++)
        {
            var spec = layerSpecs[i];
            var childKey = $"{plan.Key}/{Str(spec, "key")}";
            var layer = keys.Find("map_layers", childKey) is { } known ? layers.FirstOrDefault(l => l.Id == known) : null;
            if (layer is null)
            {
                layer = new MapLayerEntity { Id = Ids.New(), MapId = id };
                db.MapLayers.Add(layer);
                layers.Add(layer);
            }
            layer.Name = Str(spec, "name")!;
            layer.SortOrder = layerKeys[i];
            layer.IsVisible = !Has(spec, "isVisible") || Bool(spec, "isVisible");
            layer.IsLocked = Bool(spec, "isLocked");
            layerIds[Str(spec, "key")!] = layer.Id;
            keys.Set("map_layers", childKey, layer.Id);
            await keys.RecordAsync(db, "map_layers", childKey, layer.Id, importing, null, ct);
        }
        await db.SaveChangesAsync(ct);
        var elements = await db.MapElements.Where(x => x.MapId == id && !x.Deleted).ToListAsync(ct);
        var wanted = new HashSet<Guid>();
        foreach (var group in Arr(e, "elements").GroupBy(x => Str(x, "layer")!))
        {
            var specs = group.OrderBy(x => Num(x, "z")).ToList();
            var zs = FractionalKey.Sequence(specs.Count);
            for (var i = 0; i < specs.Count; i++)
            {
                var spec = specs[i];
                var childKey = $"{plan.Key}/{Str(spec, "key")}";
                var element = keys.Find("map_elements", childKey) is { } known ? elements.FirstOrDefault(x => x.Id == known) : null;
                if (element is null)
                {
                    element = new MapElementEntity { Id = Ids.New(), MapId = id };
                    db.MapElements.Add(element);
                }
                wanted.Add(element.Id);
                element.LayerId = layerIds[group.Key];
                element.Kind = EnumText.Parse<ElementKind>(Str(spec, "kind")!);
                element.Z = zs[i];
                element.X = (double)Num(spec, "x")!.Value;
                element.Y = (double)Num(spec, "y")!.Value;
                element.Width = (double)Num(spec, "width")!.Value;
                element.Height = (double)Num(spec, "height")!.Value;
                element.Text = Str(spec, "text");
                element.Points = spec!["points"]?.ToJsonString(WireJson.Options);
                var style = spec["style"] ?? new JsonObject();
                if (CanonicalJson.Of(JsonNode.Parse(element.Style)) != CanonicalJson.Of(style))
                {
                    element.Style = style.ToJsonString(WireJson.Options);
                }
                element.Config = spec["config"] is { } c && c.GetValueKind() != JsonValueKind.Null ? c.ToJsonString(WireJson.Options) : null;
                keys.Set("map_elements", childKey, element.Id);
                await keys.RecordAsync(db, "map_elements", childKey, element.Id, importing, null, ct);
            }
        }
        foreach (var gone in elements.Where(x => !wanted.Contains(x.Id)))
        {
            (gone.Deleted, gone.DeleteBatchId) = (true, Batch());
        }
        var keepLayers = layerIds.Values.ToHashSet();
        foreach (var gone in layers.Where(l => !keepLayers.Contains(l.Id)))
        {
            (gone.Deleted, gone.DeleteBatchId) = (true, Batch());
        }
    }

    private async Task WriteMatrixPresetAsync(SyncWrite write, Viewer viewer, Plan plan, Guid id, bool create, KeyMap keys, CancellationToken ct)
    {
        var map = await MapRowAsync(viewer, plan, id, create, MapKind.Matrix, ct);
        var matrix = plan.Entry["matrix"]!.DeepClone().AsObject();
        if (matrix["filter"] is JsonObject filter)
        {
            Swap(filter, MatrixFilterKeys, toIds: true, keys);
        }
        if (map.Matrix is null || CanonicalJson.Of(JsonNode.Parse(map.Matrix)) != CanonicalJson.Of(matrix))
        {
            map.Matrix = matrix.ToJsonString(WireJson.Options);
        }
        await db.SaveChangesAsync(ct);
        // One layer for the free elements (docs/03 §17 "Matrix presets").
        var layerKey = $"{plan.Key}/base";
        if (keys.Find("map_layers", layerKey) is not { } known || !await db.MapLayers.AnyAsync(l => l.Id == known && !l.Deleted, ct))
        {
            var layer = new MapLayerEntity { Id = Ids.New(), MapId = id, Name = "Base", SortOrder = "a0" };
            db.MapLayers.Add(layer);
            keys.Set("map_layers", layerKey, layer.Id);
            await keys.RecordAsync(db, "map_layers", layerKey, layer.Id, importing, null, ct);
        }
    }

    // --- export ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The configuration as a package (docs/03 §17 "Export"): every live, non-archived configuration entity – or, with
    /// <paramref name="package"/>, those that package brought – under its key (a generated one where it has none).
    /// </summary>
    public async Task<JsonObject> ExportAsync(string? package, CancellationToken ct)
    {
        var keys = await KeyMap.LoadAsync(db, ct);
        var only = package is null ? null : (await db.ConfigKeys.AsNoTracking().Where(k => k.Package == package).Select(k => k.Id).ToListAsync(ct)).ToHashSet();
        var result = new JsonObject
        {
            ["format"] = "pgpm-config-package", ["formatVersion"] = 1, ["name"] = package ?? "export", ["title"] = package ?? "Export",
            ["description"] = $"Exported {Clock.UtcNow:yyyy-MM-dd}.", ["version"] = "1.0.0", ["requires"] = new JsonArray(),
        };
        // Keys for what has none, so references resolve.
        foreach (var g in await db.Groups.AsNoTracking().ToListAsync(ct))
        {
            keys.Ensure("groups", g.Id, "group:" + Slug(g.Name));
        }
        foreach (var s in await db.Statuses.AsNoTracking().Where(s => !s.Deleted).ToListAsync(ct))
        {
            keys.Ensure("statuses", s.Id, $"status:{EnumText.Of(s.ItemType)}.{Slug(s.Name)}");
        }
        foreach (var s in await db.Sections.AsNoTracking().Where(s => !s.Deleted).ToListAsync(ct))
        {
            keys.Ensure("sections", s.Id, "section:" + Slug(s.Name));
        }
        foreach (var f in await db.FieldDefinitions.AsNoTracking().Where(f => !f.Deleted).ToListAsync(ct))
        {
            keys.Ensure("field_definitions", f.Id, "field:" + f.Key);
        }
        foreach (var t in await db.ProjectTemplates.AsNoTracking().Where(t => !t.Deleted).ToListAsync(ct))
        {
            keys.Ensure("project_templates", t.Id, "template:" + Slug(t.Name));
        }
        foreach (var t in await db.TemplateMilestones.AsNoTracking().Where(m => !m.Deleted).ToListAsync(ct))
        {
            keys.Ensure("template_milestones", t.Id, $"{keys.KeyOf("project_templates", t.TemplateId)}/m{t.Id.ToString("N")[^8..]}");
        }
        var templates = await db.Maps.AsNoTracking().Where(m => m.IsTemplate && !m.Deleted).ToListAsync(ct);
        foreach (var m in templates)
        {
            keys.Ensure("maps", m.Id, (m.Kind == MapKind.Matrix ? "matrixpreset:" : "maptemplate:") + Slug(m.Name));
        }
        var templateIds = templates.Select(m => m.Id).ToList();
        foreach (var l in await db.MapLayers.AsNoTracking().Where(l => templateIds.Contains(l.MapId) && !l.Deleted).ToListAsync(ct))
        {
            keys.Ensure("map_layers", l.Id, $"{keys.KeyOf("maps", l.MapId)}/l{l.Id.ToString("N")[^8..]}");
        }
        foreach (var x in await db.MapElements.AsNoTracking().Where(x => templateIds.Contains(x.MapId) && !x.Deleted).ToListAsync(ct))
        {
            keys.Ensure("map_elements", x.Id, $"{keys.KeyOf("maps", x.MapId)}/e{x.Id.ToString("N")[^8..]}");
        }

        async Task AddAsync(string collection, IEnumerable<Guid> ids)
        {
            var entries = new JsonObject();
            foreach (var id in ids.Where(i => only is null || only.Contains(i)))
            {
                if (await DescribeAsync(collection, id, keys, ct) is { } form)
                {
                    entries[keys.KeyOf(Tables[collection], id)] = Denormalize(collection, form);
                }
            }
            if (entries.Count > 0)
            {
                result[collection] = entries;
            }
        }
        await AddAsync("groups", await db.Groups.AsNoTracking().OrderBy(g => g.NormalizedName).Select(g => g.Id).ToListAsync(ct));
        await AddAsync("statuses", await db.Statuses.AsNoTracking().Where(s => !s.Deleted && !s.IsArchived).OrderBy(s => s.ItemType).ThenBy(s => s.SortOrder)
            .Select(s => s.Id).ToListAsync(ct));
        await AddAsync("sections", await db.Sections.AsNoTracking().Where(s => !s.Deleted && !s.IsArchived).OrderBy(s => s.SortOrder).Select(s => s.Id).ToListAsync(ct));
        await AddAsync("fields", await db.FieldDefinitions.AsNoTracking().Where(f => !f.Deleted && !f.IsArchived).OrderBy(f => f.SortOrder).Select(f => f.Id)
            .ToListAsync(ct));
        await AddAsync("templates", await db.ProjectTemplates.AsNoTracking().Where(t => !t.Deleted && !t.IsArchived).OrderBy(t => t.SortOrder).Select(t => t.Id)
            .ToListAsync(ct));
        await AddAsync("roles", await db.AssignmentRoles.AsNoTracking().Where(r => !r.Deleted && !r.IsArchived).OrderBy(r => r.SortOrder).Select(r => r.Id)
            .ToListAsync(ct));
        await AddAsync("savedViews", await db.SavedViews.AsNoTracking().Where(v => !v.Deleted && v.IsShared).OrderBy(v => v.SortOrder).Select(v => v.Id)
            .ToListAsync(ct));
        await AddAsync("mapTemplates", templates.Where(m => m.Kind == MapKind.Canvas).Select(m => m.Id));
        await AddAsync("matrixPresets", templates.Where(m => m.Kind == MapKind.Matrix).Select(m => m.Id));
        return result;
    }

    /// <summary>A package form back to a package entry: nulls dropped, ranks as sortOrder/z again.</summary>
    private static JsonObject Denormalize(string collection, JsonObject form)
    {
        var e = new JsonObject();
        foreach (var (key, value) in form)
        {
            if (value is not null && value.GetValueKind() != JsonValueKind.Null)
            {
                e[key] = value.DeepClone();
            }
        }
        switch (collection)
        {
            case "sections" when e["access"] is JsonArray { Count: 0 }:
                e.Remove("access");
                break;
            case "templates":
                var i = 0;
                foreach (var m in e["milestones"] as JsonArray ?? [])
                {
                    var milestone = m!.AsObject();
                    foreach (var key in milestone.Where(p => p.Value is null || p.Value.GetValueKind() == JsonValueKind.Null).Select(p => p.Key).ToList())
                    {
                        milestone.Remove(key);
                    }
                    milestone["sortOrder"] = (i++ + 1) * 10;
                }
                break;
            case "mapTemplates":
                var l = 0;
                foreach (var layer in e["layers"] as JsonArray ?? [])
                {
                    layer!["sortOrder"] = l++;
                }
                var z = new Dictionary<string, int>();
                foreach (var x in e["elements"] as JsonArray ?? [])
                {
                    var element = x!.AsObject();
                    foreach (var key in element.Where(p => p.Value is null || p.Value.GetValueKind() == JsonValueKind.Null).Select(p => p.Key).ToList())
                    {
                        element.Remove(key);
                    }
                    var layerKey = Str(element, "layer")!;
                    z[layerKey] = z.GetValueOrDefault(layerKey, -1) + 1;
                    element["z"] = z[layerKey];
                }
                break;
        }
        return e;
    }

    private static string Slug(string name)
    {
        var slug = Files.FileNames.Slug(name).Replace('-', '_').Replace('.', '_');
        return slug.Length > 0 && char.IsAsciiLetterOrDigit(slug[0]) ? slug : "x" + slug;
    }

    // --- helpers -----------------------------------------------------------------------------------------------------

    internal static string? Str(JsonNode? node, string key) => node?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static bool Bool(JsonNode? node, string key) => node?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    private static bool Has(JsonNode? node, string key) => node is JsonObject o && o.ContainsKey(key);

    private static JsonObject Obj(JsonNode? node, string key) => node?[key] as JsonObject ?? [];

    private static JsonArray Arr(JsonNode? node, string key) => node?[key] as JsonArray ?? [];

    private static decimal? Num(JsonNode? node, string key) => WireJson.TryDecimal(node?[key], out var d) ? d : null;

    /// <summary><c>config_keys</c> in memory: key ↔ id per entity, with the hashes as imported.</summary>
    private sealed class KeyMap
    {
        private readonly Dictionary<(string, string), Guid> ids = [];
        private readonly Dictionary<(string, Guid), string> keysOf = [];
        private readonly Dictionary<(string, string), string?> hashes = [];

        public static async Task<KeyMap> LoadAsync(PgpmDbContext db, CancellationToken ct)
        {
            var map = new KeyMap();
            foreach (var row in await db.ConfigKeys.AsNoTracking().ToListAsync(ct))
            {
                map.ids[(row.Entity, row.Key)] = row.Id;
                map.keysOf[(row.Entity, row.Id)] = row.Key;
                map.hashes[(row.Entity, row.Key)] = row.Hash;
            }
            return map;
        }

        public Guid? Find(string entity, string key) => ids.TryGetValue((entity, key), out var id) ? id : null;

        public string? HashOf(string entity, string key) => hashes.GetValueOrDefault((entity, key));

        /// <summary>The key of an id; one made up from the id where it has none (it then matches nothing in a package).</summary>
        public string KeyOf(string entity, Guid id) => keysOf.TryGetValue((entity, id), out var key) ? key : $"{entity}:{id}";

        public void Set(string entity, string key, Guid id)
        {
            if (ids.TryGetValue((entity, key), out var old))
            {
                keysOf.Remove((entity, old));
            }
            ids[(entity, key)] = id;
            keysOf[(entity, id)] = key;
        }

        /// <summary>A key for an id without one (export), unique within the entity.</summary>
        public void Ensure(string entity, Guid id, string wanted)
        {
            if (keysOf.ContainsKey((entity, id)))
            {
                return;
            }
            var key = wanted;
            for (var n = 2; ids.ContainsKey((entity, key)); n++)
            {
                key = $"{wanted}_{n}";
            }
            Set(entity, key, id);
        }

        public async Task RecordAsync(PgpmDbContext db, string entity, string key, Guid id, string? package, string? hash, CancellationToken ct)
        {
            var row = await db.ConfigKeys.FirstOrDefaultAsync(k => k.Entity == entity && k.Key == key, ct);
            if (row is null)
            {
                db.ConfigKeys.Add(new ConfigKeyEntity { Entity = entity, Key = key, Id = id, Package = package, Hash = hash });
            }
            else
            {
                row.Id = id;
                row.Package = package ?? row.Package;
                row.Hash = hash ?? row.Hash;
            }
            hashes[(entity, key)] = hash ?? hashes.GetValueOrDefault((entity, key));
            await db.SaveChangesAsync(ct);
        }
    }
}

/// <summary>The actions of a dry run's entries.</summary>
public static class PackageActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Unchanged = "unchanged";
    public const string Conflict = "conflict";
}
