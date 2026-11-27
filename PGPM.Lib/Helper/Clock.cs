namespace PGPM.Lib.Helper;

/// <summary>
/// Time for code without an injected <see cref="TimeProvider"/> (PNMTD's <c>Clock</c>):
/// <list type="bullet">
/// <item>Instants are UTC: everything stored, every age and every comparison.</item>
/// <item>"Local" is the configured time zone (<c>TZ</c>, an IANA name; default UTC), never the host's own zone.</item>
/// </list>
/// Tests replace <see cref="Provider"/> and <see cref="TimeZone"/> (PGPM_TEST_TZ).
/// </summary>
public static class Clock
{
    public static TimeProvider Provider { get; set; } = TimeProvider.System;

    public static TimeZoneInfo TimeZone { get; set; } = FromEnvironment();

    public static DateTime UtcNow => Provider.GetUtcNow().UtcDateTime;

    /// <summary>The wall-clock time in <see cref="TimeZone"/> (Kind Unspecified).</summary>
    public static DateTime ToLocal(DateTime instant) => TimeZoneInfo.ConvertTimeFromUtc(AsUtc(instant), TimeZone);

    /// <summary>Unspecified is taken as UTC; Local (the process's zone) is converted.</summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static TimeZoneInfo FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return TimeZoneInfo.Utc;
        }
        // glibc accepts ":Europe/Zurich" as well.
        return TimeZoneInfo.FindSystemTimeZoneById(name.Trim().TrimStart(':'));
    }

    private static TimeZoneInfo FromEnvironment()
    {
        try
        {
            return FromName(Environment.GetEnvironmentVariable("TZ"));
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
