using OrderTrackerBot.Api.Client;
using Xunit;

namespace OrderTrackerBot.Tests;

public class EntryActivityRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 17);

    private static HashSet<DateOnly> Days(params string[] days) => days.Select(DateOnly.Parse).ToHashSet();

    [Fact]
    public void Never_recorded_is_stale_with_no_last_day()
    {
        var result = EntryActivityRules.Compute(Days(), null, Today);

        Assert.Null(result.LastEntryDay);
        Assert.Null(result.DaysSinceLastEntry);
        Assert.Equal("stale", result.Status);
        Assert.Equal(EntryActivityRules.LookbackDays, result.MissedDaysLast14.Count);
    }

    [Fact]
    public void Entry_today_is_ok_and_today_is_never_missed()
    {
        var result = EntryActivityRules.Compute(Days("2026-10-17"), Today, Today);

        Assert.Equal("ok", result.Status);
        Assert.Equal(0, result.DaysSinceLastEntry);
        Assert.Equal(1, result.StreakDays);
        Assert.DoesNotContain("2026-10-17", result.MissedDaysLast14);
    }

    [Fact]
    public void Only_yesterday_is_still_ok_while_today_is_open()
    {
        var result = EntryActivityRules.Compute(Days("2026-10-16"), new DateOnly(2026, 10, 16), Today);

        Assert.Equal("ok", result.Status);
        Assert.Equal(1, result.StreakDays);
    }

    [Fact]
    public void Two_days_without_an_entry_is_behind()
    {
        var result = EntryActivityRules.Compute(Days("2026-10-15"), new DateOnly(2026, 10, 15), Today);

        Assert.Equal("behind", result.Status);
        Assert.Equal(2, result.DaysSinceLastEntry);
        Assert.Contains("2026-10-16", result.MissedDaysLast14);
    }

    [Fact]
    public void Four_days_without_an_entry_is_stale()
    {
        var result = EntryActivityRules.Compute(Days("2026-10-13"), new DateOnly(2026, 10, 13), Today);

        Assert.Equal("stale", result.Status);
        Assert.Equal(4, result.DaysSinceLastEntry);
    }

    [Fact]
    public void Streak_stops_at_the_first_gap()
    {
        var result = EntryActivityRules.Compute(Days("2026-10-17", "2026-10-16", "2026-10-14"), Today, Today);

        Assert.Equal(2, result.StreakDays);
        Assert.Contains("2026-10-15", result.MissedDaysLast14);
    }

    [Fact]
    public void A_last_entry_in_the_future_counts_as_today()
    {
        var result = EntryActivityRules.Compute(Days("2026-10-19"), new DateOnly(2026, 10, 19), Today);

        Assert.Equal(0, result.DaysSinceLastEntry);
    }
}
