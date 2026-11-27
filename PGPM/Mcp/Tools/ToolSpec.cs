using System.Text.Json;
using System.Text.Json.Nodes;
using PGPM.Auth.Rights;
using PGPM.Data;

namespace PGPM.Mcp.Tools;

/// <summary>
/// What a tool does (docs/03 §7.2): the read token gets <see cref="Read"/> and <see cref="Ask"/>; <see cref="Delete"/> is
/// reserved (no delete tools in v1).
/// </summary>
public enum ToolKind { Read, Write, Action, Delete, Ask }

/// <summary>
/// One tool of the catalog (docs/03 §7.2, PNMTD's ToolSpec): its name, what it does, its parameters as JSON Schema, the right
/// it needs, its kind, and for a change the audit events it writes. The runner checks the right in code, whatever a model
/// asks; object access is the tool's own check (through <c>AccessService</c> and <c>SyncWriter</c>).
/// </summary>
public sealed record ToolSpec(
    string Name,
    string Title,
    string Description,
    JsonObject InputSchema,
    Area? Area,
    Level Level,
    ToolKind Kind,
    Func<JsonElement, ToolContext, PgpmDbContext, CancellationToken, Task<object>> Run)
{
    /// <summary>A second right it needs, as converting an idea needs <i>Projects: Create</i> besides edit access.</summary>
    public IReadOnlyList<(Area Area, Level Level)> Requires { get; init; } = [];

    /// <summary>The events it may write: required for writes and actions (an <c>mcp.*</c> event, actor <c>mcp</c>).</summary>
    public IReadOnlyList<string> AuditEvents { get; init; } = [];

    /// <summary>It removes something: MCP's <c>destructiveHint</c>, so clients ask the person first.</summary>
    public bool Destructive { get; init; }

    public bool Idempotent { get; init; }

    /// <summary>Whether it is offered at all right now (a tool that depends on something configured), failing closed.</summary>
    public Func<IServiceProvider, bool>? Available { get; init; }

    /// <summary>It changes something.</summary>
    public bool Writes => Kind is ToolKind.Write or ToolKind.Action or ToolKind.Delete;

    public bool Allows(UserRights rights) => (Area is not { } area || rights.Has(area, Level)) && Requires.All(r => rights.Has(r.Area, r.Level));

    /// <summary>What it asks for: "Notes: Create", "none" for tools every token of its kind has.</summary>
    public string RightText
    {
        get
        {
            var all = (Area is { } a ? new[] { (a, Level) } : []).Concat(Requires).ToList();
            return all.Count == 0 ? "none" : string.Join(" and ", all.Select(r => $"{RightsCatalog.Of(r.Item1).Label}: {r.Item2}"));
        }
    }
}

/// <summary>A tool's failure the caller can read and act on: a code (<c>not_found</c>, <c>invalid_argument</c>, …) and a sentence.</summary>
public sealed class ToolException(string code, string message, IReadOnlyDictionary<string, object?>? extensions = null) : Exception(message)
{
    public string Code { get; } = code;

    public IReadOnlyDictionary<string, object?>? Extensions { get; } = extensions;

    public static ToolException NotFound(string what) => new("not_found", $"No such {what}, or you may not see it.");

    public static ToolException Invalid(string message) => new("invalid_argument", message);
}

/// <summary>JSON Schema for the tools' parameters, and reading them back, strictly (PNMTD's ToolArgs).</summary>
public static class ToolArgs
{
    public sealed record Field(string Name, string Type, string Description, bool Required = false)
    {
        public JsonObject? Extra { get; init; }
    }

    public static Field Text(string name, string description, bool required = false) => new(name, "string", description, required);

    public static Field Int(string name, string description, bool required = false) => new(name, "integer", description, required);

    public static Field Bool(string name, string description, bool required = false) => new(name, "boolean", description, required);

    public static Field Id(string name, string description, bool required = true) => new(name, "string", description, required)
    {
        Extra = new JsonObject { ["format"] = "uuid" },
    };

    public static Field Choice(string name, string description, IEnumerable<string> values, bool required = false) => new(name, "string", description, required)
    {
        Extra = new JsonObject { ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]) },
    };

    /// <summary>A list of objects with fields of their own, as <c>share_with</c>'s groups.</summary>
    public static Field Objects(string name, string description, params Field[] fields) => new(name, "array", description)
    {
        Extra = new JsonObject { ["items"] = Schema(fields) },
    };

    /// <summary>The paging pair every list takes.</summary>
    public static readonly Field[] PageArgs =
    [
        Int("limit", "How many, 1 to 100; 20 when left out"),
        Text("cursor", "From the previous page's nextCursor, for the next page"),
    ];

    public static JsonObject Schema(params Field[] fields)
    {
        var properties = new JsonObject();
        foreach (var f in fields)
        {
            var property = new JsonObject { ["type"] = f.Type, ["description"] = f.Description };
            if (f.Extra is { } extra)
            {
                foreach (var (key, value) in extra)
                {
                    property[key] = value?.DeepClone();
                }
            }
            properties[f.Name] = property;
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. fields.Where(f => f.Required).Select(f => (JsonNode)f.Name)]),
            ["additionalProperties"] = false,
        };
    }

    public static bool Has(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    public static Guid IdOf(JsonElement args, string name = "id") =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var id)
            ? id
            : throw ToolException.Invalid($"{name} must be an id from your data.");

    public static Guid? OptionalId(JsonElement args, string name) => Has(args, name) ? IdOf(args, name) : null;

    public static int IntOf(JsonElement args, string name, int? fallback = null) =>
        Has(args, name) ? args.GetProperty(name) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var n) ? n : throw ToolException.Invalid($"{name} must be a number.")
            : fallback ?? throw ToolException.Invalid($"{name} must be a number.");

    public static int? OptionalInt(JsonElement args, string name) => Has(args, name) ? IntOf(args, name) : null;

    public static bool? OptionalBool(JsonElement args, string name) =>
        Has(args, name) ? args.GetProperty(name).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw ToolException.Invalid($"{name} must be true or false."),
        } : null;

    /// <summary>A text of at least <paramref name="min"/> characters, trimmed.</summary>
    public static string TextOf(JsonElement args, string name, int min = 1, int max = 4000) =>
        OptionalText(args, name, max) is { } text && text.Length >= min ? text : throw ToolException.Invalid($"{name} needs at least {min} characters.");

    public static string? OptionalText(JsonElement args, string name, int max = 4000, bool trim = true)
    {
        if (!Has(args, name))
        {
            return null;
        }
        var v = args.GetProperty(name);
        if (v.ValueKind != JsonValueKind.String)
        {
            throw ToolException.Invalid($"{name} must be a text.");
        }
        var text = trim ? v.GetString()!.Trim() : v.GetString()!;
        return text.Length <= max ? text : throw ToolException.Invalid($"{name} has at most {max} characters.");
    }

    /// <summary>One of <paramref name="values"/>, or null when left out.</summary>
    public static string? OptionalChoice(JsonElement args, string name, IReadOnlyCollection<string> values) =>
        OptionalText(args, name, 64) is { } text
            ? values.FirstOrDefault(v => string.Equals(v, text, StringComparison.OrdinalIgnoreCase))
                ?? throw ToolException.Invalid($"{name} is one of {string.Join(", ", values)}.")
            : null;

    /// <summary>The objects of a list argument, or none when left out.</summary>
    public static IReadOnlyList<JsonElement> ObjectsOf(JsonElement args, string name, int max)
    {
        if (!Has(args, name))
        {
            return [];
        }
        var list = args.GetProperty(name);
        if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.Object))
        {
            throw ToolException.Invalid($"{name} must be a list of objects.");
        }
        var items = list.EnumerateArray().ToList();
        return items.Count <= max ? items : throw ToolException.Invalid($"{name} has at most {max} entries.");
    }
}
