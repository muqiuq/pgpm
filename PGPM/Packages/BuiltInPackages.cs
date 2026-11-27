using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PGPM.Packages;

/// <summary>Packages:* (docs/03 §14).</summary>
public sealed class PackageOptions
{
    /// <summary>The built-in packages the first start imports, in order; empty: none (the tests' default).</summary>
    public string SeedOnFirstStart { get; set; } = "core,innovation-ai";
}

/// <summary>
/// The built-in configuration packages (<c>PGPM/Packages/BuiltIn/*.pgpm.json</c>, embedded) and the format's schema
/// (<c>spec/config-package.schema.json</c>, embedded), docs/03 §17.
/// </summary>
public static class BuiltInPackages
{
    private const string Prefix = "PGPM.Packages.BuiltIn.";
    private const string Suffix = ".pgpm.json";

    private static readonly Lazy<PackageSchema> SchemaInstance = new(() => new PackageSchema(Read("PGPM.Packages.config-package.schema.json")));

    public static PackageSchema Schema => SchemaInstance.Value;

    /// <summary>The names, in the order they build on each other (<c>core</c> first).</summary>
    public static IReadOnlyList<string> Names { get; } = typeof(BuiltInPackages).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(Suffix, StringComparison.Ordinal))
        .Select(n => n[Prefix.Length..^Suffix.Length]).OrderBy(n => n == "core" ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal).ToList();

    public static bool Exists(string name) => Names.Contains(name);

    /// <summary>A built-in package, parsed afresh (the caller may change it).</summary>
    public static JsonObject Load(string name) => Read(Prefix + name + Suffix);

    private static JsonObject Read(string resource)
    {
        using var stream = typeof(BuiltInPackages).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"No embedded resource {resource}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd()).AsObject();
    }

    /// <summary>A package's JSON: duplicate keys are refused (as check-packages.py does), so is anything but an object.</summary>
    public static JsonNode Parse(string text) =>
        JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false, CommentHandling = JsonCommentHandling.Disallow })
        ?? throw new JsonException("A package is a JSON object.");
}

/// <summary>
/// The canonical text of JSON (object members sorted, numbers normalised), for comparing an entity's package form with the
/// database's and for <c>config_keys.hash</c>.
/// </summary>
public static class CanonicalJson
{
    public static string Of(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    public static string Hash(JsonNode? node) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Of(node))));

    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in o.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    sb.Append(JsonSerializer.Serialize(key)).Append(':');
                    Write(value, sb);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(',');
                    }
                    Write(a[i], sb);
                }
                sb.Append(']');
                break;
            default:
                var kind = node.GetValueKind();
                if (kind == JsonValueKind.Number)
                {
                    sb.Append(Sync.WireJson.TryDecimal(node, out var d)
                        ? (d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture)
                        : node.ToJsonString());
                }
                else if (kind == JsonValueKind.Null)
                {
                    sb.Append("null");
                }
                else
                {
                    sb.Append(node.ToJsonString());
                }
                break;
        }
    }
}
