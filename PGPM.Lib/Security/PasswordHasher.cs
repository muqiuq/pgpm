using System.Globalization;
using System.Security.Cryptography;

namespace PGPM.Lib.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashes (docs/03 §4.1, from PNMTD). The encoded form carries its parameters:
/// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;key&gt;</c> (Base64), so older parameters keep verifying and
/// <see cref="NeedsRehash"/> can upgrade them at the next sign-in.
/// </summary>
public static class PasswordHasher
{
    private const string Prefix = "pbkdf2-sha256";
    private const char Delimiter = '$';
    private const int SaltSize = 16;
    private const int KeySize = 32;

    /// <summary>OWASP's 2023 recommendation for PBKDF2-HMAC-SHA256.</summary>
    public const int Iterations = 210_000;

    public const int MinimumLength = 10;

    public const int MaximumLength = 256;

    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    /// <summary>Verified when a username does not exist, so that it takes as long as a wrong password.</summary>
    public static readonly string DummyHash = Hash(SecurePasswordGenerator.Generate(32));

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, KeySize);
        return Encode(Iterations, salt, key);
    }

    public static bool Verify(string? password, string? encodedHash)
    {
        if (string.IsNullOrEmpty(password) || !TryDecode(encodedHash, out var iterations, out var salt, out var expected))
        {
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>True for a hash with fewer iterations than today's (replaced after the next successful login).</summary>
    public static bool NeedsRehash(string? encodedHash) =>
        !TryDecode(encodedHash, out var iterations, out _, out var key) || iterations < Iterations || key.Length != KeySize;

    /// <summary>The length rule (docs/03 §4.1): 10 to 256 characters, no composition rules.</summary>
    public static string? PolicyViolation(string? password) => password switch
    {
        null or "" => "A password is required.",
        { Length: < MinimumLength } => $"At least {MinimumLength} characters.",
        { Length: > MaximumLength } => $"At most {MaximumLength} characters.",
        _ => null,
    };

    private static string Encode(int iterations, byte[] salt, byte[] key) =>
        string.Join(Delimiter, Prefix, iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(key));

    private static bool TryDecode(string? encodedHash, out int iterations, out byte[] salt, out byte[] key)
    {
        iterations = 0;
        salt = key = [];
        var parts = encodedHash?.Split(Delimiter);
        if (parts is not { Length: 4 } || parts[0] != Prefix
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations) || iterations < 1)
        {
            return false;
        }
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            key = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        return salt.Length > 0 && key.Length > 0;
    }
}
