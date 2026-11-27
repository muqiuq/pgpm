using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace PGPM.Sync;

/// <summary>
/// A synced row on the wire (docs/03 §11.4 "wire format"): camelCase properties of the data model's columns
/// (<c>owner_user_id</c> → <c>ownerUserId</c>), timestamps as ISO-8601 UTC, uuids lower case, enums lower case, dates
/// <c>yyyy-MM-dd</c>, jsonb as JSON, bytea as base64.
/// </summary>
public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new LowerCaseEnumJsonConverterFactory() },
        NumberHandling = JsonNumberHandling.Strict,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>A column's value as JSON; a jsonb column's text parsed.</summary>
    public static JsonNode? ToNode(object? value, WireColumn column)
    {
        if (value is null)
        {
            return null;
        }
        if (column.IsJson)
        {
            return JsonNode.Parse((string)value);
        }
        return JsonSerializer.SerializeToNode(value, column.ClrType, Options);
    }

    /// <summary>JSON into a column's value; <see cref="JsonException"/> or <see cref="FormatException"/> when it doesn't fit.</summary>
    public static object? FromNode(JsonNode? node, WireColumn column)
    {
        if (column.IsJson)
        {
            return node is null ? (column.Nullable ? null : "null") : node.ToJsonString(Options);
        }
        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            if (!column.Nullable)
            {
                throw new FormatException("A value is required.");
            }
            return null;
        }
        return node.Deserialize(column.ClrType, Options);
    }

    /// <summary>The value as the column stores it, as JSON: <c>"0193ABC…"</c> → <c>"0193abc…"</c>, 1000 → 1000.00. Unparsable: as given.</summary>
    public static JsonNode? Normalize(JsonNode? node, WireColumn column)
    {
        try
        {
            return ToNode(FromNode(node, column), column);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NotSupportedException)
        {
            return node;
        }
    }

    /// <summary>JSON equality: objects by their members in any order, arrays by position, numbers by value.</summary>
    public static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
        {
            return IsNull(a) && IsNull(b);
        }
        return Same(JsonSerializer.SerializeToElement(a), JsonSerializer.SerializeToElement(b));
    }

    private static bool IsNull(JsonNode? node) => node is null || node.GetValueKind() == JsonValueKind.Null;

    private static bool Same(JsonElement a, JsonElement b)
    {
        if (a.ValueKind != b.ValueKind)
        {
            return false;
        }
        switch (a.ValueKind)
        {
            case JsonValueKind.Number:
                return a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) ? x == y : a.GetDouble().Equals(b.GetDouble());
            case JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case JsonValueKind.Array:
                return a.GetArrayLength() == b.GetArrayLength() && a.EnumerateArray().Zip(b.EnumerateArray()).All(p => Same(p.First, p.Second));
            case JsonValueKind.Object:
                var left = a.EnumerateObject().ToList();
                var right = b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                return left.Count == right.Count && left.All(p => right.TryGetValue(p.Name, out var other) && Same(p.Value, other));
            default:
                return true;
        }
    }

    /// <summary>"OwnerUserId" → "ownerUserId".</summary>
    public static string KeyOf(string propertyName) => JsonNamingPolicy.CamelCase.ConvertName(propertyName);
}

/// <summary>A column of a synced table as the wire names it.</summary>
public sealed record WireColumn(string Key, string PropertyName, string ColumnName, Type ClrType, bool IsJson, bool Nullable, PropertyInfo Info)
{
    public object? Get(object entity) => Info.GetValue(entity);

    public void Set(object entity, object? value) => Info.SetValue(entity, value);

    public static IReadOnlyList<WireColumn> Of(IEntityType type) => type.GetProperties()
        .Where(p => p.PropertyInfo is not null)
        .Select(p => new WireColumn(WireJson.KeyOf(p.Name), p.Name, p.GetColumnName(), p.ClrType, p.GetColumnType() == "jsonb", p.IsNullable,
            p.PropertyInfo!))
        .ToList();
}

/// <summary>Enums as lower-case strings, read ignoring case; numbers and undefined names are refused.</summary>
public sealed class LowerCaseEnumJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(LowerCaseEnumJsonConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class LowerCaseEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (text is { Length: > 0 } && !char.IsDigit(text[0]) && text[0] != '-' && Enum.TryParse<T>(text, true, out var value) && Enum.IsDefined(value))
            {
                return value;
            }
            throw new JsonException($"One of: {string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }
}
