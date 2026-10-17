using System.Globalization;
using OrderTrackerBot.Api.Admin;

namespace OrderTrackerBot.Api.Client;

/// <summary>
/// How recently a seller recorded anything, and the streak and gaps around it. Same rules as the dashboard's
/// entry-activity notice: 0-1 days ok, 2-3 behind, 4+ or never stale. Today may still be open, so it is never a
/// missed day.
/// </summary>
public static class EntryActivityRules
{
    public const int BehindAfterDays = 2;
    public const int StaleAfterDays = 4;
    public const int LookbackDays = 14;

    /// <param name="entryDays">Seller days (Pakistan time) that have at least one order or expense.</param>
    /// <param name="lastEntry">Newest entry day across all time, or null if there has never been one.</param>
    /// <param name="today">The seller's today.</param>
    public static EntryActivityDto Compute(IReadOnlySet<DateOnly> entryDays, DateOnly? lastEntry, DateOnly today)
    {
        int? daysSince = lastEntry is null ? null : Math.Max(0, today.DayNumber - lastEntry.Value.DayNumber);
        var status = daysSince is null || daysSince >= StaleAfterDays ? "stale"
            : daysSince >= BehindAfterDays ? "behind"
            : "ok";

        // A streak runs back from today, or from yesterday while today is still open.
        var cursor = entryDays.Contains(today) ? today : today.AddDays(-1);
        var streak = 0;
        while (entryDays.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        var missed = new List<string>();
        for (var back = 1; back <= LookbackDays; back++)
        {
            var day = today.AddDays(-back);
            if (!entryDays.Contains(day)) missed.Add(day.ToString(AdminDateRange.DateFormat, CultureInfo.InvariantCulture));
        }

        return new EntryActivityDto(
            lastEntry?.ToString(AdminDateRange.DateFormat, CultureInfo.InvariantCulture),
            daysSince,
            streak,
            missed,
            status);
    }
}
