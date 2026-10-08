namespace OrderTrackerBot.Application.Time;

/// <summary>Converts "start of the seller's local day" into a UTC instant (timestamps are stored UTC).</summary>
public static class SellerClock
{
    public const string DefaultTimeZoneId = "Asia/Karachi";

    public static TimeZoneInfo Resolve(string? timeZoneId)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZoneId) ? DefaultTimeZoneId : timeZoneId); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Fixed UTC+5 (PKT, no DST) when tzdata is unavailable, e.g. slim containers.
            return TimeZoneInfo.CreateCustomTimeZone("PKT", TimeSpan.FromHours(5), "PKT", "PKT");
        }
    }

    public static DateTime StartOfLocalDayUtc(string? timeZoneId, DateTime utcNow)
    {
        var tz = Resolve(timeZoneId);
        var localDate = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz).Date;
        return LocalMidnightToUtc(tz, localDate);
    }

    /// <summary>[start, end) of the seller's local day, <paramref name="dayOffset"/> days from today (-1 = yesterday).</summary>
    public static (DateTime StartUtc, DateTime EndUtc) LocalDayRangeUtc(string? timeZoneId, DateTime utcNow, int dayOffset)
    {
        var tz = Resolve(timeZoneId);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz).Date;
        return (LocalMidnightToUtc(tz, today.AddDays(dayOffset)), LocalMidnightToUtc(tz, today.AddDays(dayOffset + 1)));
    }

    /// <summary>[start, end) of the current calendar month in the seller's local time.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) CurrentMonthRangeUtc(string? timeZoneId, DateTime utcNow)
    {
        var tz = Resolve(timeZoneId);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz).Date;
        var thisMonth = new DateTime(today.Year, today.Month, 1);
        return (LocalMidnightToUtc(tz, thisMonth), LocalMidnightToUtc(tz, thisMonth.AddMonths(1)));
    }

    /// <summary>[start, end) of the previous calendar month in the seller's local time.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) PreviousMonthRangeUtc(string? timeZoneId, DateTime utcNow)
    {
        var tz = Resolve(timeZoneId);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), tz).Date;
        var thisMonth = new DateTime(today.Year, today.Month, 1);
        return (LocalMidnightToUtc(tz, thisMonth.AddMonths(-1)), LocalMidnightToUtc(tz, thisMonth));
    }

    private static DateTime LocalMidnightToUtc(TimeZoneInfo tz, DateTime localDate)
    {
        var localMidnight = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(localMidnight)) localMidnight = localMidnight.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(localMidnight, tz);
    }
}
