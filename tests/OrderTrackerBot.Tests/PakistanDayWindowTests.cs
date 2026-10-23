using OrderTrackerBot.Application.Time;
using Xunit;

namespace OrderTrackerBot.Tests;

/// <summary>Report windows are Pakistan days, whatever zone a seller row still carries (no UTC cut-offs).</summary>
public class PakistanDayWindowTests
{
    [Fact]
    public void Resolve_IsPakistanWhateverTheSellerZone() =>
        Assert.Equal(TimeSpan.FromHours(5), SellerClock.Resolve("America/New_York").BaseUtcOffset);

    [Fact]
    public void DaysAgo_StartsAtPakistanMidnight()
    {
        // 2026-10-24 01:00 in Pakistan. Thirty days back is 24 Sep, whose Pakistan midnight is 23 Sep 19:00 UTC.
        var now = new DateTime(2026, 10, 23, 20, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 9, 23, 19, 0, 0, DateTimeKind.Utc), SellerClock.DaysAgoUtc(30, now));
    }
}
