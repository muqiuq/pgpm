using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PGPM.Packages;

/// <summary>
/// The semantic rules of configuration packages (docs/03 §17), the C# twin of <c>tools/check-packages.py</c>'s
/// <c>Checker</c> (same rules, same wording, so its self-test cases hold here too): keys unique across packages, references
/// resolvable within a package and what it requires, score inputs numeric and without cycles, derivable rescale/normalise
/// maxima, default statuses, template statuses, section <c>defaultFor</c> ⊆ <c>appliesTo</c>, gate suggestions, map elements
/// on existing layers, views, matrix presets and field defaults fitting their fields, icons in PrimeIcons.
/// </summary>
public sealed partial class PackageChecker
{
    public static readonly string[] Collections = ["groups", "statuses", "sections", "fields", "templates", "roles", "savedViews", "mapTemplates", "matrixPresets"];

    private static readonly Dictionary<string, string> Entity = new()
    {
        ["groups"] = "group", ["statuses"] = "status", ["sections"] = "section", ["fields"] = "field", ["templates"] = "template", ["roles"] = "role",
        ["savedViews"] = "view", ["mapTemplates"] = "map template", ["matrixPresets"] = "matrix preset",
    };

    private static readonly HashSet<string> NumericTypes = ["number", "rating", "select", "multiselect", "score"];
    private static readonly HashSet<string> AxisTypes = ["number", "rating", "score"];

    private static readonly HashSet<string> CommonColumns = ["key", "title", "status", "owner", "persons", "color", "created_at", "updated_at"];

    /// <summary>Built-in list columns a saved view may name besides <c>field:&lt;key&gt;</c> (tools/check-packages.py COLUMNS).</summary>
    public static readonly Dictionary<string, HashSet<string>> Columns = new()
    {
        ["note"] = [.. CommonColumns.Where(c => c != "status"), "pinned", "links"],
        ["idea"] = [.. CommonColumns, "days_in_status", "links"],
        ["lead"] = [.. CommonColumns, "days_in_status", "organisation", "value", "probability", "decision_date", "next_action", "next_action_due", "source"],
        ["project"] = [.. CommonColumns, "days_in_status", "links", "template", "start_date", "end_date"],
        ["gate"] = ["project", "title", "status", "due_date", "decision", "decided_at", "decided_by", "dod_progress", "persons"],
    };

    private readonly Dictionary<string, (string File, JsonObject Data)> packages;
    private readonly bool oneDefaultAcrossAll;
    private readonly Dictionary<string, string> owner = new(StringComparer.Ordinal);
    private string pkg = "";
    private string file = "";

    /// <param name="packages">By name: the file's label and the (structurally valid) package.</param>
    /// <param name="oneDefaultAcrossAll">
    /// The built-in set's rule: exactly one default status per item type across all packages. An import checks at most one
    /// per item type within each package (the importer makes it the default).
    /// </param>
    public PackageChecker(Dictionary<string, (string File, JsonObject Data)> packages, bool oneDefaultAcrossAll)
    {
        this.packages = packages;
        this.oneDefaultAcrossAll = oneDefaultAcrossAll;
    }

    public List<string> Problems { get; } = [];

    private void Err(string path, string message) => Problems.Add($"{file}: {path}: {message}");

    private static string Show(JsonNode? v) => PackageSchema.Show(v);

    private static string? Str(JsonNode? node, string key) => node?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static bool Bool(JsonNode? node, string key) => node?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    private static JsonObject Obj(JsonNode? node, string key) => node?[key] as JsonObject ?? [];

    private static JsonArray Arr(JsonNode? node, string key) => node?[key] as JsonArray ?? [];

    private static decimal? Num(JsonNode? node, string key) => Sync.WireJson.TryDecimal(node?[key], out var d) ? d : null;

    private static decimal Dec(JsonNode? node) => Sync.WireJson.TryDecimal(node, out var d) ? d : 0;

    private static bool Has(JsonNode? node, string key) => node is JsonObject o && o.ContainsKey(key);

    // --- visibility and lookups ---------------------------------------------------------------------------------------

    private HashSet<string> Scope(string name, HashSet<string>? seen = null)
    {
        seen ??= [];
        if (seen.Contains(name) || !packages.ContainsKey(name))
        {
            return seen;
        }
        seen.Add(name);
        foreach (var r in Arr(packages[name].Data, "requires"))
        {
            Scope(r!.GetValue<string>(), seen);
        }
        return seen;
    }

    private JsonObject? Get(string collection, string key, string path, string? expect = null)
    {
        var who = owner.GetValueOrDefault(key);
        var entity = who is null ? null : Obj(packages[who].Data, collection)[key] as JsonObject;
        if (entity is null)
        {
            Err(path, $"unknown {expect ?? Entity[collection]} '{key}'");
            return null;
        }
        if (!Scope(pkg).Contains(who!))
        {
            Err(path, $"'{key}' is defined in package '{who}', which '{pkg}' does not require");
            return null;
        }
        return entity;
    }

    private JsonObject? Field(string bareKey, string path) => Get("fields", "field:" + bareKey, path);

    private JsonObject? SectionOf(JsonObject field)
    {
        var key = Str(field, "section")!;
        return owner.TryGetValue(key, out var who) ? Obj(packages[who].Data, "sections")[key] as JsonObject : null;
    }

    private HashSet<string> AppliesTo(JsonObject field) =>
        SectionOf(field) is { } section ? Arr(section, "appliesTo").Select(x => x!.GetValue<string>()).ToHashSet() : [];

    private IEnumerable<(string Key, JsonObject Entity, string Path)> Each(string collection)
    {
        foreach (var name in packages.Keys.Order(StringComparer.Ordinal))
        {
            pkg = name;
            file = packages[name].File;
            foreach (var (key, entity) in Obj(packages[name].Data, collection).ToList())
            {
                yield return (key, entity!.AsObject(), $"{collection}.{key}");
            }
        }
    }

    // --- the checks ---------------------------------------------------------------------------------------------------

    public List<string> Run()
    {
        CheckPackages();
        if (Problems.Count > 0)
        {
            return Problems;
        }
        foreach (var name in packages.Keys.Order(StringComparer.Ordinal))
        {
            (pkg, file) = (name, packages[name].File);
            foreach (var collection in Collections)
            {
                foreach (var (key, _) in Obj(packages[name].Data, collection))
                {
                    if (owner.TryGetValue(key, out var other))
                    {
                        Err($"{collection}.{key}", $"key is also defined by package '{other}'");
                    }
                    else
                    {
                        owner[key] = name;
                    }
                }
            }
        }
        CheckUniqueNames("groups", "group name");
        CheckStatuses();
        CheckSections();
        CheckFields();
        CheckScores();
        CheckTemplates();
        CheckUniqueNames("roles", "role name");
        CheckViews();
        CheckMapTemplates();
        CheckMatrixPresets();
        return Problems;
    }

    private void CheckPackages()
    {
        foreach (var name in packages.Keys.Order(StringComparer.Ordinal))
        {
            (pkg, file) = (name, packages[name].File);
            var requires = Arr(packages[name].Data, "requires").Select(r => r!.GetValue<string>()).ToList();
            for (var i = 0; i < requires.Count; i++)
            {
                if (requires[i] == name)
                {
                    Err($"requires[{i}]", "a package can't require itself");
                }
                else if (!packages.ContainsKey(requires[i]))
                {
                    Err($"requires[{i}]", $"unknown package '{requires[i]}'");
                }
            }
            var seen = new HashSet<string>();
            var stack = new Stack<(string Current, List<string> Trail)>([(name, [name])]);
            while (stack.Count > 0)
            {
                var (current, trail) = stack.Pop();
                foreach (var r in packages.TryGetValue(current, out var p) ? Arr(p.Data, "requires").Select(x => x!.GetValue<string>()) : [])
                {
                    if (r == name && trail.Count > 1)
                    {
                        Err("requires", $"cycle: {string.Join(" → ", trail.Append(r))}");
                    }
                    else if (!seen.Contains(r) && packages.ContainsKey(r) && r != name)
                    {
                        seen.Add(r);
                        stack.Push((r, [.. trail, r]));
                    }
                }
            }
        }
    }

    private void CheckUniqueNames(string collection, string label, Func<JsonObject, string>? by = null)
    {
        var names = new Dictionary<string, string>();
        foreach (var (key, entity, path) in Each(collection))
        {
            var n = $"{by?.Invoke(entity)}\u0000{Str(entity, "name")!.Trim().ToLowerInvariant()}";
            if (names.TryGetValue(n, out var other))
            {
                Err(path + ".name", $"{label} '{Str(entity, "name")}' is already used by {other}");
            }
            names[n] = key;
        }
    }

    private void CheckIcon(JsonObject entity, string path)
    {
        if (Str(entity, "icon") is { } icon && !PrimeIcons.Names.Contains(icon[3..]))
        {
            Err(path + ".icon", $"'{icon}' is not a PrimeIcons {PrimeIcons.Version} icon");
        }
    }

    private void CheckStatuses()
    {
        CheckUniqueNames("statuses", "status name", s => Str(s, "itemType")!);
        var defaults = new Dictionary<string, List<string>> { ["idea"] = [], ["lead"] = [], ["project"] = [] };
        var perPackage = new Dictionary<(string, string), List<string>>();
        foreach (var (key, s, path) in Each("statuses"))
        {
            var type = Str(s, "itemType")!;
            if (key.Split(':', 2)[1].Split('.', 2)[0] != type)
            {
                Err(path, $"the key's item type differs from itemType '{type}'");
            }
            if (Bool(s, "isDefault"))
            {
                defaults[type].Add(key);
                (perPackage.TryGetValue((pkg, type), out var list) ? list : perPackage[(pkg, type)] = []).Add(key);
            }
            CheckIcon(s, path);
        }
        if (oneDefaultAcrossAll)
        {
            foreach (var (type, keys) in defaults)
            {
                if (keys.Count != 1)
                {
                    file = "packages";
                    Err("statuses", $"item type '{type}' needs exactly one default status across all packages, has {keys.Count}"
                        + (keys.Count > 0 ? $" ({string.Join(", ", keys)})" : ""));
                }
            }
        }
        else
        {
            foreach (var ((name, type), keys) in perPackage.Where(p => p.Value.Count > 1))
            {
                file = packages[name].File;
                Err("statuses", $"item type '{type}' has {keys.Count} default statuses ({string.Join(", ", keys)}); at most one");
            }
        }
    }

    private void CheckSections()
    {
        foreach (var (_, s, path) in Each("sections"))
        {
            var applies = Arr(s, "appliesTo").Select(x => x!.GetValue<string>()).ToHashSet();
            var extra = Arr(s, "defaultFor").Select(x => x!.GetValue<string>()).Where(x => !applies.Contains(x)).Order().ToList();
            if (extra.Count > 0)
            {
                Err(path + ".defaultFor", $"{string.Join(", ", extra)} not in appliesTo");
            }
            var groups = new HashSet<string>();
            var i = 0;
            foreach (var a in Arr(s, "access"))
            {
                var group = Str(a, "group")!;
                Get("groups", group, $"{path}.access[{i}].group");
                if (!groups.Add(group))
                {
                    Err($"{path}.access[{i}]", $"group '{group}' listed twice");
                }
                i++;
            }
        }
    }

    /// <summary>A field value (default value, template default) in the shape of <c>field_values</c> (docs/02 §7.5).</summary>
    private void CheckValue(JsonObject field, JsonNode? value, string path)
    {
        var t = Str(field, "type")!;
        var c = Obj(field, "config");
        if (value is null || value.GetValueKind() == JsonValueKind.Null)
        {
            return;
        }
        var kind = PackageSchema.TypeOf(value);
        switch (t)
        {
            case "score" or "person":
                Err(path, $"{t} fields have no default value");
                break;
            case "text" or "textarea" or "markdown" or "url" or "email":
                if (kind != "string")
                {
                    Err(path, "must be a string");
                }
                else if (t == "text" && Num(c, "maxLength") is { } max && value.GetValue<string>().Length > max)
                {
                    Err(path, $"is longer than the field's maxLength {max}");
                }
                break;
            case "bool":
                if (kind != "boolean")
                {
                    Err(path, "must be true or false");
                }
                break;
            case "number":
                if (kind != "number")
                {
                    Err(path, "must be a number");
                }
                else if (Num(c, "min") is { } lo && Dec(value) < lo || Num(c, "max") is { } hi && Dec(value) > hi)
                {
                    Err(path, $"{Show(value)} is outside the field's min/max");
                }
                break;
            case "date" or "datetime":
                var pattern = t == "date" ? DatePattern() : DateTimePattern();
                if (kind != "string" || !pattern.IsMatch(value.GetValue<string>()))
                {
                    Err(path, $"must be a {t} string");
                }
                break;
            case "select" or "multiselect":
            {
                var ids = Arr(c, "options").Where(o => !Bool(o, "archived")).Select(o => Str(o, "id")).ToHashSet();
                if (t == "select" && kind != "string" || t == "multiselect" && kind != "array")
                {
                    Err(path, t == "select" ? "must be an option id" : "must be a list of option ids");
                    return;
                }
                foreach (var v in t == "select" ? new List<JsonNode?> { value } : [.. value.AsArray()])
                {
                    if (PackageSchema.TypeOf(v) != "string" || !ids.Contains(v!.GetValue<string>()))
                    {
                        Err(path, $"{Show(v)} is not an option of the field");
                    }
                }
                break;
            }
            case "rating":
            {
                var lo = Num(c, "min") ?? 0;
                var hi = Num(c, "max") ?? 5;
                if (kind == "string" && value.GetValue<string>() == "na")
                {
                    if (!Bool(c, "allowNa"))
                    {
                        Err(path, "\"na\" needs allowNa");
                    }
                }
                else if (!PackageSchema.IsInteger(value) || Dec(value) < lo || Dec(value) > hi)
                {
                    Err(path, $"must be an integer from {lo} to {hi}{(Bool(c, "allowNa") ? " or \"na\"" : "")}");
                }
                break;
            }
        }
    }

    private void CheckFields()
    {
        foreach (var (_, f, path) in Each("fields"))
        {
            Get("sections", Str(f, "section")!, path + ".section");
            var c = Obj(f, "config");
            var t = Str(f, "type");
            if (t is "select" or "multiselect")
            {
                var ids = new HashSet<string>();
                var i = 0;
                foreach (var o in Arr(c, "options"))
                {
                    if (!ids.Add(Str(o, "id")!))
                    {
                        Err($"{path}.config.options[{i}].id", $"option id '{Str(o, "id")}' used twice");
                    }
                    i++;
                }
            }
            if (t == "rating")
            {
                var lo = Num(c, "min") ?? 0;
                var hi = Num(c, "max") ?? 5;
                if (lo >= hi)
                {
                    Err(path + ".config", "min must be below max");
                }
                foreach (var (label, _) in Obj(c, "labels"))
                {
                    if (!int.TryParse(label, out var n) || n < lo || n > hi)
                    {
                        Err($"{path}.config.labels.{label}", $"outside {lo}–{hi}");
                    }
                }
            }
            if (t == "number" && Num(c, "min") is { } min && Num(c, "max") is { } max && min > max)
            {
                Err(path + ".config", "min is above max");
            }
            if (Has(f, "defaultValue"))
            {
                CheckValue(f, f["defaultValue"], path + ".defaultValue");
            }
        }
    }

    /// <summary>The maximum of a score input (docs/03 §16), or null when it can't be derived.</summary>
    private static decimal? InputMax(JsonObject f, decimal? cap)
    {
        var c = Obj(f, "config");
        decimal? m = Str(f, "type") switch
        {
            "number" => Num(c, "max"),
            "rating" => Num(c, "max") ?? 5,
            "select" => Arr(c, "options").Where(o => !Bool(o, "archived") && Has(o, "points")).Select(o => Num(o, "points")).Max(),
            "multiselect" => (Str(c, "multiPoints") ?? "count") == "count"
                ? Arr(c, "options").Count(o => !Bool(o, "archived"))
                : Arr(c, "options").Where(o => !Bool(o, "archived")).Sum(o => Math.Max(Num(o, "points") ?? 0, 0)),
            "score" => Bool(c, "normalize") ? 100 : Num(c, "max"),
            _ => null,
        };
        if (cap is { } limit)
        {
            m = m is null ? limit : Math.Min(m.Value, limit);
        }
        return m;
    }

    private void CheckScores()
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, f, path) in Each("fields"))
        {
            if (Str(f, "type") != "score")
            {
                continue;
            }
            var c = Obj(f, "config");
            var me = key["field:".Length..];
            var inputs = Arr(c, "inputs").Select(x => x!.GetValue<string>()).ToList();
            graph[me] = inputs;
            var p = path + ".config";
            var applies = AppliesTo(f);
            var caps = Obj(c, "cap");
            foreach (var (name, _) in caps)
            {
                if (!inputs.Contains(name))
                {
                    Err($"{p}.cap.{name}", "is not an input of the score");
                }
            }
            var maxima = new List<(string Name, decimal? Max)>();
            for (var i = 0; i < inputs.Count; i++)
            {
                var name = inputs[i];
                var ip = $"{p}.inputs[{i}]";
                if (name == me)
                {
                    Err(ip, "a score can't be its own input");
                    continue;
                }
                if (Field(name, ip) is not { } g)
                {
                    continue;
                }
                var t = Str(g, "type")!;
                var gc = Obj(g, "config");
                if (!NumericTypes.Contains(t))
                {
                    Err(ip, $"'{name}' is a {t} field, which has no numeric value");
                    continue;
                }
                if (t == "select" || t == "multiselect" && (Str(gc, "multiPoints") ?? "count") == "sum")
                {
                    var missing = Arr(gc, "options").Where(o => !Bool(o, "archived") && !Has(o, "points")).Select(o => Str(o, "id")).ToList();
                    if (missing.Count > 0)
                    {
                        Err(ip, $"options of '{name}' without points: {string.Join(", ", missing)}");
                    }
                }
                var gApplies = AppliesTo(g);
                if (!applies.IsSubsetOf(gApplies))
                {
                    Err(ip, $"'{name}' does not apply to {string.Join(", ", applies.Except(gApplies).Order())}, the score does");
                }
                maxima.Add((name, InputMax(g, Num(caps, name))));
            }
            var rescale = Str(c, "na") == "rescale";
            if (rescale || Bool(c, "normalize"))
            {
                if (!Has(c, "max"))
                {
                    Err(p, $"{(rescale ? "na: rescale" : "normalize")} needs 'max'");
                }
                foreach (var (name, m) in maxima.Where(x => x.Max is null))
                {
                    Err(p, $"the maximum of input '{name}' can't be derived (number fields need config.max)");
                }
                if (Str(c, "op") == "sum" && rescale && Num(c, "max") is { } max && maxima.All(x => x.Max is not null))
                {
                    var total = maxima.Sum(x => x.Max!.Value);
                    if (Math.Abs(total - max) > 0.000000001m)
                    {
                        Err(p + ".max", $"{Show(c["max"])} differs from the sum of the inputs' maxima, {total.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                            + " – rescale would scale complete answers too");
                    }
                }
            }
        }
        var state = new Dictionary<string, bool>(StringComparer.Ordinal);
        void Visit(string node, List<string> trail)
        {
            state[node] = true;
            foreach (var next in graph.GetValueOrDefault(node) ?? [])
            {
                if (!graph.ContainsKey(next))
                {
                    continue;
                }
                if (state.TryGetValue(next, out var open) && open)
                {
                    var cycle = trail[trail.IndexOf(next)..].Append(next);
                    pkg = owner.GetValueOrDefault("field:" + node) ?? pkg;
                    file = packages[pkg].File;
                    Err($"fields.field:{node}.config.inputs", $"score cycle: {string.Join(" → ", cycle)}");
                }
                else if (!state.ContainsKey(next))
                {
                    Visit(next, [.. trail, next]);
                }
            }
            state[node] = false;
        }
        foreach (var node in graph.Keys.Order(StringComparer.Ordinal))
        {
            if (!state.ContainsKey(node))
            {
                Visit(node, [node]);
            }
        }
    }

    private JsonObject? ProjectStatus(string key, string path)
    {
        var s = Get("statuses", key, path);
        if (s is not null && Str(s, "itemType") != "project")
        {
            Err(path, $"'{key}' is a status of {Str(s, "itemType")}s, not of projects");
            return null;
        }
        return s;
    }

    private void CheckTemplates()
    {
        foreach (var (_, t, path) in Each("templates"))
        {
            CheckIcon(t, path);
            var allowed = Arr(t, "allowedStatuses").Select(x => x!.GetValue<string>()).ToList();
            for (var i = 0; i < allowed.Count; i++)
            {
                ProjectStatus(allowed[i], $"{path}.allowedStatuses[{i}]");
            }
            if (Str(t, "defaultStatus") is { } defaultStatus && ProjectStatus(defaultStatus, path + ".defaultStatus") is not null
                && allowed.Count > 0 && !allowed.Contains(defaultStatus))
            {
                Err(path + ".defaultStatus", $"'{defaultStatus}' is not among the template's allowed statuses");
            }
            var sections = Arr(t, "sections").Select(x => x!.GetValue<string>()).ToList();
            for (var i = 0; i < sections.Count; i++)
            {
                if (Get("sections", sections[i], $"{path}.sections[{i}]") is { } sec && !Arr(sec, "appliesTo").Any(x => x!.GetValue<string>() == "project"))
                {
                    Err($"{path}.sections[{i}]", $"section '{sections[i]}' does not apply to projects");
                }
            }
            foreach (var (fkey, value) in Obj(t, "fieldDefaults"))
            {
                var fp = $"{path}.fieldDefaults.{fkey}";
                if (Get("fields", fkey, fp) is not { } f)
                {
                    continue;
                }
                if (!sections.Contains(Str(f, "section")!))
                {
                    Err(fp, $"the field's section '{Str(f, "section")}' is not one of the template's sections");
                }
                CheckValue(f, value, fp);
            }
            var groups = new HashSet<string>();
            var n = 0;
            foreach (var share in Arr(t, "defaultShares"))
            {
                var group = Str(share, "group")!;
                Get("groups", group, $"{path}.defaultShares[{n}].group");
                if (!groups.Add(group))
                {
                    Err($"{path}.defaultShares[{n}]", $"group '{group}' listed twice");
                }
                n++;
            }
            var keys = new HashSet<string>();
            var orders = new HashSet<decimal>();
            var m = 0;
            foreach (var milestone in Arr(t, "milestones"))
            {
                var mp = $"{path}.milestones[{m++}]";
                if (!keys.Add(Str(milestone, "key")!))
                {
                    Err(mp + ".key", $"milestone key '{Str(milestone, "key")}' used twice in the template");
                }
                if (!orders.Add(Num(milestone, "sortOrder")!.Value))
                {
                    Err(mp + ".sortOrder", $"sortOrder {Num(milestone, "sortOrder")} used twice in the template");
                }
                foreach (var prop in new[] { "onGoStatus", "onNoGoStatus" })
                {
                    if (Str(milestone, prop) is { } s && ProjectStatus(s, $"{mp}.{prop}") is not null && allowed.Count > 0 && !allowed.Contains(s))
                    {
                        Err($"{mp}.{prop}", $"'{s}' is not among the template's allowed statuses");
                    }
                }
            }
        }
    }

    private JsonObject? CheckViewField(string viewType, string reference, string path)
    {
        if (viewType is not ("idea" or "project"))
        {
            Err(path, $"{viewType} views have no fields");
            return null;
        }
        var f = Get("fields", reference, path);
        if (f is not null && !AppliesTo(f).Contains(viewType))
        {
            Err(path, $"field '{reference}' does not apply to {viewType}s");
            return null;
        }
        return f;
    }

    private void CheckColumn(string viewType, string column, string path)
    {
        if (column.StartsWith("field:", StringComparison.Ordinal))
        {
            CheckViewField(viewType, column, path);
        }
        else if (!Columns[viewType].Contains(column))
        {
            Err(path, $"unknown {viewType} column '{column}' (built-in: {string.Join(", ", Columns[viewType].Order(StringComparer.Ordinal))})");
        }
    }

    private void CheckViews()
    {
        var names = new Dictionary<string, string>();
        foreach (var (key, v, path) in Each("savedViews"))
        {
            var vt = Str(v, "itemType")!;
            var d = Obj(v, "definition");
            var n = $"{vt}\u0000{Str(v, "name")!.Trim().ToLowerInvariant()}";
            if (!Has(v, "isShared") || Bool(v, "isShared"))
            {
                if (names.TryGetValue(n, out var other))
                {
                    Err(path + ".name", $"shared {vt} view '{Str(v, "name")}' is already defined by {other}");
                }
                names[n] = key;
            }
            var fp = path + ".definition.filters";
            var filters = Obj(d, "filters");
            var i = 0;
            foreach (var s in Arr(filters, "statuses"))
            {
                var sp = $"{fp}.statuses[{i++}]";
                if (vt is not ("idea" or "lead" or "project"))
                {
                    Err(sp, $"{vt} views have no item statuses");
                    break;
                }
                if (Get("statuses", s!.GetValue<string>(), sp) is { } st && Str(st, "itemType") != vt)
                {
                    Err(sp, $"'{s}' is a status of {Str(st, "itemType")}s, the view lists {vt}s");
                }
            }
            if (Has(filters, "categories") && vt is not ("idea" or "lead" or "project"))
            {
                Err(fp + ".categories", $"{vt} views have no status categories");
            }
            i = 0;
            foreach (var t in Arr(filters, "templates"))
            {
                if (vt is not ("project" or "gate"))
                {
                    Err($"{fp}.templates[{i}]", "only project and gate views filter by template");
                    break;
                }
                Get("templates", t!.GetValue<string>(), $"{fp}.templates[{i++}]");
            }
            i = 0;
            foreach (var g in Arr(filters, "sharedWith"))
            {
                Get("groups", g!.GetValue<string>(), $"{fp}.sharedWith[{i++}]");
            }
            if (Has(filters, "decisions") && vt != "gate")
            {
                Err(fp + ".decisions", "only gate views filter by decision");
            }
            if (Has(filters, "hasLink") && vt == "gate")
            {
                Err(fp + ".hasLink", "gates have no links");
            }
            i = 0;
            foreach (var ff in Arr(filters, "fields"))
            {
                CheckFieldFilter(vt, ff!.AsObject(), $"{fp}.fields[{i++}]");
            }
            i = 0;
            foreach (var c in Arr(d, "columns"))
            {
                CheckColumn(vt, c!.GetValue<string>(), $"{path}.definition.columns[{i++}]");
            }
            i = 0;
            foreach (var c in Arr(d, "sort"))
            {
                CheckColumn(vt, c!.GetValue<string>().TrimStart('-'), $"{path}.definition.sort[{i++}]");
            }
        }
    }

    private static readonly Dictionary<string, HashSet<string>> FilterOps = new()
    {
        ["eq"] = ["bool", "number", "rating", "select", "text", "url", "email"],
        ["in"] = ["select", "multiselect"],
        ["contains"] = ["text", "textarea", "markdown", "url", "email"],
        ["range"] = ["number", "rating", "score", "date", "datetime"],
    };

    private void CheckFieldFilter(string vt, JsonObject ff, string path)
    {
        if (CheckViewField(vt, Str(ff, "field")!, path + ".field") is not { } f)
        {
            return;
        }
        var t = Str(f, "type")!;
        var op = Str(ff, "op")!;
        if (FilterOps.TryGetValue(op, out var ok) && !ok.Contains(t))
        {
            Err(path + ".op", $"'{op}' can't be used on a {t} field");
            return;
        }
        if (op == "eq")
        {
            var value = ff["value"];
            var expected = t switch { "bool" => "boolean", "number" or "rating" => "number", _ => "string" };
            if (PackageSchema.TypeOf(value) != expected && !(t == "rating" && PackageSchema.TypeOf(value) == "string" && value!.GetValue<string>() == "na"))
            {
                Err(path + ".value", $"a {t} field compares with a {expected}");
            }
            else if (t == "select")
            {
                CheckValue(f, value, path + ".value");
            }
        }
        if (op == "in")
        {
            var ids = Arr(Obj(f, "config"), "options").Select(o => Str(o, "id")).ToHashSet();
            var i = 0;
            foreach (var v in Arr(ff, "values"))
            {
                if (!ids.Contains(v!.GetValue<string>()))
                {
                    Err($"{path}.values[{i}]", $"{Show(v)} is not an option of {Str(ff, "field")}");
                }
                i++;
            }
        }
        if (op == "range")
        {
            foreach (var bound in new[] { "min", "max" })
            {
                if (Has(ff, bound))
                {
                    var want = t is "date" or "datetime" ? "string" : "number";
                    if (PackageSchema.TypeOf(ff[bound]) != want)
                    {
                        Err($"{path}.{bound}", $"must be a {want} for a {t} field");
                    }
                }
            }
        }
    }

    private void CheckSettings(JsonObject settings, string path)
    {
        var i = 0;
        foreach (var name in Arr(settings, "cardBadges"))
        {
            Field(name!.GetValue<string>(), $"{path}.cardBadges[{i++}]");
        }
    }

    private void CheckMapTemplates()
    {
        foreach (var (_, m, path) in Each("mapTemplates"))
        {
            CheckSettings(Obj(m, "settings"), path + ".settings");
            var layers = new HashSet<string>();
            var orders = new HashSet<decimal>();
            var i = 0;
            foreach (var layer in Arr(m, "layers"))
            {
                if (!layers.Add(Str(layer, "key")!))
                {
                    Err($"{path}.layers[{i}].key", $"layer key '{Str(layer, "key")}' used twice");
                }
                if (!orders.Add(Num(layer, "sortOrder")!.Value))
                {
                    Err($"{path}.layers[{i}].sortOrder", $"sortOrder {Num(layer, "sortOrder")} used twice");
                }
                i++;
            }
            var keys = new HashSet<string>();
            var zs = new HashSet<(string, decimal)>();
            i = 0;
            foreach (var e in Arr(m, "elements"))
            {
                var ep = $"{path}.elements[{i++}]";
                if (!keys.Add(Str(e, "key")!))
                {
                    Err(ep + ".key", $"element key '{Str(e, "key")}' used twice");
                }
                var layer = Str(e, "layer")!;
                if (!layers.Contains(layer))
                {
                    Err(ep + ".layer", $"unknown layer '{layer}' (layers: {string.Join(", ", layers.Order(StringComparer.Ordinal))})");
                }
                if (!zs.Add((layer, Num(e, "z")!.Value)))
                {
                    Err(ep + ".z", $"z {Num(e, "z")} used twice in layer '{layer}'");
                }
                if (Math.Abs(Num(e, "x")!.Value + Num(e, "width")!.Value) > 1_000_000 || Math.Abs(Num(e, "y")!.Value + Num(e, "height")!.Value) > 1_000_000)
                {
                    Err(ep, "reaches beyond ±1,000,000");
                }
            }
        }
    }

    private void CheckMatrixPresets()
    {
        foreach (var (_, preset, path) in Each("matrixPresets"))
        {
            CheckSettings(Obj(preset, "settings"), path + ".settings");
            var m = Obj(preset, "matrix");
            var p = path + ".matrix";
            var filter = Obj(m, "filter");
            var types = Has(filter, "types") ? Arr(filter, "types").Select(x => x!.GetValue<string>()).ToHashSet() : ["idea", "project"];
            foreach (var axis in new[] { "x", "y", "size" })
            {
                if (!Has(m, axis))
                {
                    continue;
                }
                var ap = $"{p}.{axis}";
                var name = Str(m[axis], "field")!;
                if (Field(name, ap + ".field") is { } f)
                {
                    if (!AxisTypes.Contains(Str(f, "type")!))
                    {
                        Err(ap + ".field", $"'{name}' is a {Str(f, "type")} field; axes need number, rating or score");
                    }
                    var applies = AppliesTo(f);
                    if (!types.IsSubsetOf(applies))
                    {
                        Err(ap + ".field", $"'{name}' does not apply to {string.Join(", ", types.Except(applies).Order())}");
                    }
                }
                if (axis != "size")
                {
                    var a = m[axis]!.AsObject();
                    if (Num(a, "min") >= Num(a, "max"))
                    {
                        Err(ap, "min must be below max");
                    }
                    else if (Num(a, "split") is { } split && !(Num(a, "min") <= split && split <= Num(a, "max")))
                    {
                        Err(ap + ".split", "outside min–max");
                    }
                }
            }
            var i = 0;
            foreach (var s in Arr(filter, "statuses"))
            {
                if (Get("statuses", s!.GetValue<string>(), $"{p}.filter.statuses[{i}]") is { } st && !types.Contains(Str(st, "itemType")!))
                {
                    Err($"{p}.filter.statuses[{i}]", $"a {Str(st, "itemType")} status, the matrix shows {string.Join(", ", types.Order())}");
                }
                i++;
            }
            i = 0;
            foreach (var t in Arr(filter, "templates"))
            {
                Get("templates", t!.GetValue<string>(), $"{p}.filter.templates[{i++}]");
            }
            i = 0;
            foreach (var g in Arr(filter, "groups"))
            {
                Get("groups", g!.GetValue<string>(), $"{p}.filter.groups[{i++}]");
            }
        }
    }

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:]{5,8}Z\z")]
    private static partial Regex DateTimePattern();
}
