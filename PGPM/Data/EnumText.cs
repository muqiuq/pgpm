using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace PGPM.Data;

/// <summary>
/// An enum value as the database, the wire and the API spell it (docs/02 §1 "enum-like values"): the member's
/// <see cref="JsonStringEnumMemberNameAttribute"/> when it has one (<c>GateDecision.NoGo</c> → <c>no_go</c>), else its name
/// in lower case (<c>ItemType.Note</c> → <c>note</c>). One table per enum type, built once.
/// </summary>
public static class EnumText
{
    private sealed record Names(Dictionary<long, string> ByValue, Dictionary<string, object> ByText);

    private static readonly ConcurrentDictionary<Type, Names> Cache = new();

    private static Names Of(Type type) => Cache.GetOrAdd(type, t =>
    {
        var byValue = new Dictionary<long, string>();
        var byText = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = field.GetValue(null)!;
            var text = field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? field.Name.ToLowerInvariant();
            byValue[Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)] = text;
            byText[text] = value;
        }
        return new Names(byValue, byText);
    });

    public static string Of<TEnum>(TEnum value) where TEnum : struct, Enum =>
        Of(typeof(TEnum)).ByValue.TryGetValue(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture), out var text)
            ? text : value.ToString().ToLowerInvariant();

    /// <summary>The value of a spelling (any case); false for anything else, numbers included.</summary>
    public static bool TryParse<TEnum>(string? text, out TEnum value) where TEnum : struct, Enum
    {
        if (text is { Length: > 0 } && Of(typeof(TEnum)).ByText.TryGetValue(text, out var found))
        {
            value = (TEnum)found;
            return true;
        }
        value = default;
        return false;
    }

    public static TEnum Parse<TEnum>(string text) where TEnum : struct, Enum =>
        TryParse<TEnum>(text, out var value) ? value : Enum.Parse<TEnum>(text, true);

    /// <summary>All spellings of the type, in declaration order.</summary>
    public static IReadOnlyList<string> All<TEnum>() where TEnum : struct, Enum => Enum.GetValues<TEnum>().Select(Of).ToList();

    public static IReadOnlyList<string> All(Type type) => [.. Of(type).ByValue.Values];
}
