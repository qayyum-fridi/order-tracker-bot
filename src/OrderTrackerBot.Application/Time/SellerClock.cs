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
        var localMidnight = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(localMidnight)) localMidnight = localMidnight.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(localMidnight, tz);
    }
}
