namespace PGPM.Auth.Microsoft;

/// <summary>
/// Where the browser may be sent after a sign-in (GGITSV): only a path of this app, so that a crafted link cannot send the
/// user to another site after signing in (an open redirect).
/// </summary>
public static class ReturnPaths
{
    /// <summary>The path when none (or no safe one) is given.</summary>
    public const string Home = "/";

    /// <summary>The path itself when it is a local path of this app; otherwise <see cref="Home"/>.</summary>
    public static string Normalize(string? returnTo) => IsLocal(returnTo) ? returnTo! : Home;

    /// <summary>
    /// Whether a value is a local path: it starts with a single <c>/</c> (not <c>//</c> or <c>/\</c>, which browsers read as
    /// another host), and contains no control characters and no backslash.
    /// </summary>
    public static bool IsLocal(string? returnTo) =>
        returnTo is { Length: > 0 and <= 2048 }
        && returnTo[0] == '/'
        && (returnTo.Length == 1 || (returnTo[1] != '/' && returnTo[1] != '\\'))
        && !returnTo.Contains('\\', StringComparison.Ordinal)
        && !returnTo.Any(char.IsControl);
}
