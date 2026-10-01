using OrderTrackerBot.Application.Time;

namespace OrderTrackerBot.Tests;

public class SellerClockTests
{
    [Fact]
    public void Pkt_DayStartsAt19UtcPreviousDay()
    {
        // 01:00 PKT on 2 Oct == 20:00 UTC on 1 Oct -> local day began 19:00 UTC on 1 Oct
        var start = SellerClock.StartOfLocalDayUtc("Asia/Karachi", new DateTime(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 10, 1, 19, 0, 0), start);
    }

    [Fact]
    public void Pkt_EarlyUtcMorningStaysInSameLocalDay()
    {
        // 03:00 UTC == 08:00 PKT same date
        var start = SellerClock.StartOfLocalDayUtc("Asia/Karachi", new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 9, 30, 19, 0, 0), start);
    }

    [Fact]
    public void LocalDayRange_Yesterday_IsPreviousLocalDay()
    {
        var (start, end) = SellerClock.LocalDayRangeUtc("Asia/Karachi", new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc), -1);
        Assert.Equal(new DateTime(2026, 9, 29, 19, 0, 0), start);
        Assert.Equal(new DateTime(2026, 9, 30, 19, 0, 0), end);
    }

    [Fact]
    public void PreviousMonthRange_CoversLocalCalendarMonth()
    {
        var (start, end) = SellerClock.PreviousMonthRangeUtc("Asia/Karachi", new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 8, 31, 19, 0, 0), start);
        Assert.Equal(new DateTime(2026, 9, 30, 19, 0, 0), end);
    }
}
