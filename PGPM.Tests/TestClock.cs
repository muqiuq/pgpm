using PGPM.Auth;
using PGPM.Lib.Helper;

namespace PGPM.Tests;

/// <summary>Where tests that freeze <see cref="Clock"/> start.</summary>
public static class TestClock
{
    /// <summary>
    /// Now, unless the day's reset (<see cref="AuthOptions.ResetHour"/>, 04:00 in <c>PGPM_TEST_TZ</c>) is less than an
    /// hour away: then an hour after that reset. A test that steps its frozen clock (a TOTP enrolment moves on 30 s)
    /// never crosses a reset by accident, whatever the time of day the suite runs at.
    /// </summary>
    public static DateTimeOffset AwayFromReset(int resetHour = 4)
    {
        var now = DateTime.UtcNow;
        var reset = SessionRules.NextReset(now, resetHour);
        var start = reset - now < TimeSpan.FromHours(1) ? reset.AddHours(1) : now;
        return new DateTimeOffset(start, TimeSpan.Zero);
    }
}
