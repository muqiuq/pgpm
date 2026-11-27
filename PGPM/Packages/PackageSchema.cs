using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PGPM.Packages;

/// <summary>
/// The JSON Schema (draft 2020-12) keywords <c>spec/config-package.schema.json</c> uses, evaluated as
/// <c>tools/check-packages.py</c>'s evaluator does (the importer's structural check, docs/03 §17): a keyword it doesn't know
/// is a problem of the schema, so no rule is skipped silently. Errors are (path, message) with the checker's wording.
/// </summary>
public sealed class PackageSchema
{
    private static readonly HashSet<string> Annotations = ["$schema", "$id", "$comment", "$defs", "title", "description", "default", "examples"];

    private static readonly HashSet<string> Keywords =
    [
        .. Annotations, "$ref", "type", "const", "enum", "pattern", "minLength", "maxLength", "minimum", "maximum", "exclusiveMinimum",
        "exclusiveMaximum", "required", "properties", "additionalProperties", "propertyNames", "minProperties", "maxProperties", "items",
        "prefixItems", "minItems", "maxItems", "uniqueItems", "allOf", "anyOf", "oneOf", "not", "if", "then", "else",
    ];

    private readonly JsonObject root;
    private readonly Dictionary<string, Regex> patterns = new(StringComparer.Ordinal);

    public PackageSchema(JsonObject root)
    {
        this.root = root;
        Audit(root, "#");
    }

    /// <summary>Problems of the schema itself (an unknown keyword, a $ref that doesn't resolve).</summary>
    public List<string> Problems { get; } = [];

    private void Audit(JsonNode? s, string where)
    {
        if (s is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            return;
        }
        if (s is not JsonObject o)
        {
            Problems.Add($"{where}: a schema must be an object or a boolean");
            return;
        }
        foreach (var (k, value) in o)
        {
            if (!Keywords.Contains(k))
            {
                Problems.Add($"{where}: keyword '{k}' is not supported");
            }
            else if (k is "additionalProperties" or "propertyNames" or "items" or "not" or "if" or "then" or "else")
            {
                Audit(value, $"{where}/{k}");
            }
            else if (k is "allOf" or "anyOf" or "oneOf" or "prefixItems")
            {
                var i = 0;
                foreach (var x in value!.AsArray())
                {
                    Audit(x, $"{where}/{k}/{i++}");
                }
            }
            else if (k is "properties" or "$defs")
            {
                foreach (var (name, x) in value!.AsObject())
                {
                    Audit(x, $"{where}/{k}/{name}");
                }
            }
            else if (k == "$ref" && Resolve(value!.GetValue<string>()) is null)
            {
                Problems.Add($"{where}: $ref '{value}' does not resolve");
            }
        }
    }

    private JsonNode? Resolve(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }
        JsonNode? node = root;
        foreach (var part in reference[2..].Split('/'))
        {
            node = node is JsonObject o && o.TryGetPropertyValue(part.Replace("~1", "/").Replace("~0", "~"), out var next) ? next : null;
        }
        return node;
    }

    private Regex Pattern(string p)
    {
        if (!patterns.TryGetValue(p, out var regex))
        {
            // ECMA-262's $ ends the input; .NET's would also match before a final newline.
            var text = p.EndsWith('$') && !p.EndsWith("\\$", StringComparison.Ordinal) ? p[..^1] + @"\z" : p;
            patterns[p] = regex = new Regex(text, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        return regex;
    }

    /// <summary>The errors of <paramref name="instance"/> against the schema (or a part of it); empty when valid.</summary>
    public List<(string Path, string Message)> Errors(JsonNode? instance, JsonNode? schema = null, string path = "")
    {
        var output = new List<(string, string)>();
        Validate(instance, schema ?? root, path, output);
        return output;
    }

    private bool Valid(JsonNode? instance, JsonNode? schema) => Errors(instance, schema).Count == 0;

    public static string TypeOf(JsonNode? v) => v is null ? "null" : v.GetValueKind() switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Number => "number",
        JsonValueKind.String => "string",
        JsonValueKind.Array => "array",
        _ => "object",
    };

    public static bool IsInteger(JsonNode? v) => Sync.WireJson.TryDecimal(v, out var d) && d == decimal.Truncate(d);

    public static string Show(JsonNode? v) => v?.ToJsonString() ?? "null";

    private static decimal Number(JsonNode node) => Sync.WireJson.TryDecimal(node, out var d) ? d : 0;

    private static string Join(string path, string name) => path.Length > 0 ? $"{path}.{name}" : name;

    private void Validate(JsonNode? v, JsonNode? schemaNode, string path, List<(string, string)> output)
    {
        if (schemaNode is JsonValue b && b.GetValueKind() is JsonValueKind.True)
        {
            return;
        }
        if (schemaNode is JsonValue f && f.GetValueKind() is JsonValueKind.False)
        {
            output.Add((path, "is not allowed here"));
            return;
        }
        var s = schemaNode!.AsObject();
        if (s["$ref"] is { } reference)
        {
            Validate(v, Resolve(reference.GetValue<string>()), path, output);
        }
        var t = TypeOf(v);
        if (s["type"] is { } typeNode)
        {
            var types = typeNode is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToList() : [typeNode.GetValue<string>()];
            if (!types.Any(x => t == x || (x == "integer" && IsInteger(v))))
            {
                output.Add((path, $"must be {string.Join(" or ", types)}, not {t}"));
                return;
            }
        }
        if (s["const"] is { } constant && !JsonEqual(v, constant))
        {
            output.Add((path, $"must be {Show(constant)}"));
        }
        if (s["enum"] is JsonArray values && !values.Any(x => JsonEqual(v, x)))
        {
            output.Add((path, $"{Show(v)} is not one of {string.Join(", ", values.Select(Show))}"));
        }
        if (t == "string")
        {
            var text = v!.GetValue<string>();
            var length = text.EnumerateRunes().Count();
            if (s["minLength"] is { } min && length < Number(min))
            {
                output.Add((path, Number(min) == 1 ? "must not be empty" : $"must have at least {min} characters"));
            }
            if (s["maxLength"] is { } max && length > Number(max))
            {
                output.Add((path, $"must have at most {max} characters"));
            }
            if (s["pattern"] is { } pattern && !Pattern(pattern.GetValue<string>()).IsMatch(text))
            {
                output.Add((path, $"{Show(v)} does not match {pattern.GetValue<string>()}"));
            }
        }
        if (t == "number")
        {
            var n = Number(v!);
            if (s["minimum"] is { } min && n < Number(min))
            {
                output.Add((path, $"must be ≥ {min.ToJsonString()}"));
            }
            if (s["maximum"] is { } max && n > Number(max))
            {
                output.Add((path, $"must be ≤ {max.ToJsonString()}"));
            }
            if (s["exclusiveMinimum"] is { } emin && n <= Number(emin))
            {
                output.Add((path, $"must be > {emin.ToJsonString()}"));
            }
            if (s["exclusiveMaximum"] is { } emax && n >= Number(emax))
            {
                output.Add((path, $"must be < {emax.ToJsonString()}"));
            }
        }
        if (t == "object")
        {
            var o = v!.AsObject();
            foreach (var name in s["required"] as JsonArray ?? [])
            {
                if (!o.ContainsKey(name!.GetValue<string>()))
                {
                    output.Add((path, $"missing required property '{name.GetValue<string>()}'"));
                }
            }
            if (s["minProperties"] is { } minp && o.Count < Number(minp))
            {
                output.Add((path, $"must have at least {minp} properties"));
            }
            if (s["maxProperties"] is { } maxp && o.Count > Number(maxp))
            {
                output.Add((path, $"must have at most {maxp} properties"));
            }
            var properties = s["properties"] as JsonObject;
            foreach (var (name, x) in o)
            {
                var sub = Join(path, name);
                if (s["propertyNames"] is { } names)
                {
                    foreach (var (_, message) in Errors(JsonValue.Create(name), names))
                    {
                        output.Add((sub, "key " + message));
                    }
                }
                if (properties is not null && properties.TryGetPropertyValue(name, out var property))
                {
                    Validate(x, property, sub, output);
                }
                else if (s.TryGetPropertyValue("additionalProperties", out var additional))
                {
                    if (additional is JsonValue no && no.GetValueKind() == JsonValueKind.False)
                    {
                        output.Add((sub, "unknown property"));
                    }
                    else
                    {
                        Validate(x, additional, sub, output);
                    }
                }
            }
        }
        if (t == "array")
        {
            var a = v!.AsArray();
            if (s["minItems"] is { } mini && a.Count < Number(mini))
            {
                output.Add((path, $"must have at least {mini} items"));
            }
            if (s["maxItems"] is { } maxi && a.Count > Number(maxi))
            {
                output.Add((path, $"must have at most {maxi} items"));
            }
            var prefix = s["prefixItems"] as JsonArray ?? [];
            for (var i = 0; i < a.Count; i++)
            {
                if (i < prefix.Count)
                {
                    Validate(a[i], prefix[i], $"{path}[{i}]", output);
                }
                else if (s["items"] is { } items)
                {
                    Validate(a[i], items, $"{path}[{i}]", output);
                }
            }
            if (s["uniqueItems"] is JsonValue unique && unique.GetValueKind() == JsonValueKind.True)
            {
                for (var i = 0; i < a.Count; i++)
                {
                    if (Enumerable.Range(0, i).Any(j => JsonEqual(a[i], a[j])))
                    {
                        output.Add(($"{path}[{i}]", $"{Show(a[i])} appears twice"));
                    }
                }
            }
        }
        foreach (var x in s["allOf"] as JsonArray ?? [])
        {
            Validate(v, x, path, output);
        }
        if (s["anyOf"] is JsonArray any && !any.Any(x => Valid(v, x)))
        {
            output.Add((path, "matches none of the allowed shapes: " + string.Join(" | ", any.Select(x => string.Join("; ", Errors(v, x).Select(e => e.Message))))));
        }
        if (s["oneOf"] is JsonArray one && one.Count(x => Valid(v, x)) != 1)
        {
            output.Add((path, "must match exactly one of the allowed shapes"));
        }
        if (s["not"] is { } not && Valid(v, not))
        {
            output.Add((path, "is not allowed in this shape"));
        }
        if (s["if"] is { } condition)
        {
            var branch = Valid(v, condition) ? s["then"] : s["else"];
            if (branch is not null)
            {
                Validate(v, branch, path, output);
            }
        }
    }

    /// <summary>JSON equality as Python's: types alike, objects by their members, arrays by position, numbers by value.</summary>
    public static bool JsonEqual(JsonNode? a, JsonNode? b)
    {
        var (ta, tb) = (TypeOf(a), TypeOf(b));
        if (ta != tb)
        {
            return false;
        }
        return ta switch
        {
            "null" => true,
            "object" => a!.AsObject().Count == b!.AsObject().Count
                && a.AsObject().All(p => b.AsObject().TryGetPropertyValue(p.Key, out var other) && JsonEqual(p.Value, other)),
            "array" => a!.AsArray().Count == b!.AsArray().Count && a.AsArray().Zip(b.AsArray()).All(p => JsonEqual(p.First, p.Second)),
            "number" => Number(a!) == Number(b!),
            "boolean" => a!.GetValueKind() == b!.GetValueKind(),
            _ => a!.GetValue<string>() == b!.GetValue<string>(),
        };
    }
}
