namespace PGPM.Lib.Helper;

/// <summary>
/// Fractional index keys (data model §10): <c>z</c> of map elements, <c>sort_order</c> of layers, milestones, DoD items
/// and assignments, generated on the clients by the npm package <c>fractional-indexing</c> (PGPM.WebUI/src/domain/
/// fractional.ts) in its default format: a head letter (<c>a</c>–<c>z</c> for integer parts of 2–27 characters,
/// <c>Z</c>–<c>A</c> for 2–27 below them), the rest of the integer part and an optional fraction in base 62, the
/// fraction without a trailing <c>0</c>. Keys compare ordinally (PostgreSQL: <c>COLLATE "C"</c>); ties are broken by id.
/// </summary>
public static class FractionalKey
{
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>A bound for what a client may store: ~1,500 inserts in a row at the same place.</summary>
    public const int MaxLength = 255;

    /// <summary>The smallest integer, which the package never produces and refuses as a bound.</summary>
    private static readonly string Smallest = "A" + new string('0', 26);

    /// <summary>A key the package produces and accepts: what the server stores.</summary>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxLength || key == Smallest)
        {
            return false;
        }
        var integerLength = key[0] switch
        {
            >= 'a' and <= 'z' => key[0] - 'a' + 2,
            >= 'A' and <= 'Z' => 'Z' - key[0] + 2,
            _ => 0,
        };
        if (integerLength == 0 || key.Length < integerLength)
        {
            return false;
        }
        for (var i = 1; i < key.Length; i++)
        {
            if (!Digits.Contains(key[i]))
            {
                return false;
            }
        }
        return key.Length == integerLength || key[^1] != '0';
    }

    /// <summary>The order of two rows: by key (ordinal), then by id.</summary>
    public static int Compare(string aKey, Guid aId, string bKey, Guid bId)
    {
        var byKey = string.CompareOrdinal(aKey, bKey);
        return byKey != 0 ? Math.Sign(byKey) : string.CompareOrdinal(aId.ToString(), bId.ToString()) switch
        {
            < 0 => -1,
            > 0 => 1,
            _ => 0,
        };
    }
}
