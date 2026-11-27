using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PGPM.Mcp.Tools;

/// <summary>
/// A tool's result as JSON within a budget of characters (PNMTD's ToolResults), never cut in the middle: a list gives up its
/// last items, a long text its middle, until the result fits, and then says so with <c>truncated</c>.
/// </summary>
public static class ToolResults
{
    /// <summary>Web JSON, nulls left out, text as it is (a result is JSON inside JSON-RPC, never HTML).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const string TruncatedNote = "Shortened to fit: ask for fewer (limit), the next page (cursor), or one object at a time.";

    public static (string Text, JsonNode? Node, bool Truncated) Serialize(object? result, int maxChars)
    {
        var node = JsonSerializer.SerializeToNode(result, Json);
        var text = node?.ToJsonString(Json) ?? "null";
        if (text.Length <= maxChars || node is null)
        {
            return (text, node, false);
        }
        for (var round = 0; round < 400 && text.Length > maxChars; round++)
        {
            if (!ShortenList(node, text.Length - maxChars) && !ShortenText(node))
            {
                break;
            }
            text = node.ToJsonString(Json);
        }
        node = Marked(node);
        return (node.ToJsonString(Json), node, true);
    }

    private static JsonNode Marked(JsonNode node)
    {
        if (node is JsonObject o)
        {
            o["truncated"] = true;
            o["note"] = TruncatedNote;
            return o;
        }
        return new JsonObject { ["items"] = node.DeepClone(), ["truncated"] = true, ["note"] = TruncatedNote };
    }

    private static bool ShortenList(JsonNode root, int over)
    {
        JsonArray? longest = null;
        var longestLength = 0;
        foreach (var array in Walk(root).OfType<JsonArray>())
        {
            if (array.Count < 2)
            {
                continue;
            }
            var length = array.ToJsonString(Json).Length;
            if (length > longestLength)
            {
                (longest, longestLength) = (array, length);
            }
        }
        if (longest is null)
        {
            return false;
        }
        var perItem = Math.Max(1, longestLength / longest.Count);
        var remove = Math.Clamp((over / perItem) + 1, 1, longest.Count - 1);
        for (var i = 0; i < remove; i++)
        {
            longest.RemoveAt(longest.Count - 1);
        }
        return true;
    }

    /// <summary>The longest text above 200 characters keeps its head and tail, half as long.</summary>
    private static bool ShortenText(JsonNode root)
    {
        JsonValue? longest = null;
        var longestLength = 200;
        foreach (var value in Walk(root).OfType<JsonValue>())
        {
            if (value.TryGetValue<string>(out var s) && s.Length > longestLength)
            {
                (longest, longestLength) = (value, s.Length);
            }
        }
        if (longest is null)
        {
            return false;
        }
        var text = longest.GetValue<string>();
        var keep = text.Length / 4;
        var cut = $"{text[..keep]} … [{text.Length - 2 * keep} characters left out] … {text[^keep..]}";
        switch (longest.Parent)
        {
            case JsonObject o:
                o[longest.GetPropertyName()] = cut;
                break;
            case JsonArray a:
                a[longest.GetElementIndex()] = cut;
                break;
        }
        return true;
    }

    private static IEnumerable<JsonNode> Walk(JsonNode node)
    {
        yield return node;
        var children = node switch
        {
            JsonObject o => o.Select(p => p.Value).ToList(),
            JsonArray a => a.ToList(),
            _ => [],
        };
        foreach (var child in children.Where(c => c is not null))
        {
            foreach (var n in Walk(child!))
            {
                yield return n;
            }
        }
    }
}
