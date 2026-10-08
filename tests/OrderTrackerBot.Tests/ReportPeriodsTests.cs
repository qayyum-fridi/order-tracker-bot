using OrderTrackerBot.Application.Time;

namespace OrderTrackerBot.Tests;

public class ReportPeriodsTests
{
    // Thursday 8 Oct 2026, 13:00 in Karachi (UTC+5, no DST).
    private static readonly DateTime Now = new(2026, 10, 8, 8, 0, 0, DateTimeKind.Utc);
    private const string Tz = "Asia/Karachi";

    private static string? Key(string text) => ReportPeriods.TryParseKey(text, Now, out var key) ? key : null;

    [Theory]
    // day words
    [InlineData("today", "today")]
    [InlineData("aaj", "today")]
    [InlineData("آج", "today")]
    [InlineData("yesterday", "yesterday")]
    [InlineData("kal", "yesterday")]
    [InlineData("کل", "yesterday")]
    [InlineData("kal ka", "yesterday")]
    [InlineData("guzishta kal", "yesterday")]
    [InlineData("tomorrow", "tomorrow")]
    [InlineData("aane wala kal", "tomorrow")]
    [InlineData("agla din", "tomorrow")]
    [InlineData("آنے والا کل", "tomorrow")]
    [InlineData("parso", "d-2")]
    [InlineData("پرسوں", "d-2")]
    [InlineData("day before yesterday", "d-2")]
    [InlineData("day after tomorrow", "d+2")]
    // week / month
    [InlineData("this week", "thisweek")]
    [InlineData("is hafte", "thisweek")]
    [InlineData("اس ہفتے", "thisweek")]
    [InlineData("last week", "lastweek")]
    [InlineData("pichle hafte", "lastweek")]
    [InlineData("پچھلے ہفتے", "lastweek")]
    [InlineData("week", "7d")]
    [InlineData("hafta", "7d")]
    [InlineData("this month", "thismonth")]
    [InlineData("is mahine", "thismonth")]
    [InlineData("اس مہینے", "thismonth")]
    [InlineData("month", "thismonth")]
    [InlineData("monthly", "thismonth")]
    [InlineData("last month", "lastmonth")]
    [InlineData("pichle mahine", "lastmonth")]
    [InlineData("pichle mahine ka", "lastmonth")]
    [InlineData("guzishta mahina", "lastmonth")]
    [InlineData("پچھلے مہینے", "lastmonth")]
    [InlineData("گزشتہ ماہ", "lastmonth")]
    // quarter / year
    [InlineData("quarterly", "thisquarter")]
    [InlineData("this quarter", "thisquarter")]
    [InlineData("sehmahi", "thisquarter")]
    [InlineData("سہ ماہی", "thisquarter")]
    [InlineData("last quarter", "lastquarter")]
    [InlineData("pichli quarter", "lastquarter")]
    [InlineData("پچھلی سہ ماہی", "lastquarter")]
    [InlineData("yearly", "thisyear")]
    [InlineData("this year", "thisyear")]
    [InlineData("is saal", "thisyear")]
    [InlineData("اس سال", "thisyear")]
    [InlineData("annual", "thisyear")]
    [InlineData("last year", "lastyear")]
    [InlineData("pichle saal", "lastyear")]
    [InlineData("پچھلے سال", "lastyear")]
    [InlineData("گزشتہ سال", "lastyear")]
    // last N
    [InlineData("last 7 days", "7d")]
    [InlineData("7 din", "7d")]
    [InlineData("pichle 15 din", "15d")]
    [InlineData("30 days", "30d")]
    [InlineData("پچھلے 10 دن", "10d")]
    [InlineData("3 weeks", "3w")]
    [InlineData("pichle 2 hafte", "2w")]
    [InlineData("6 months", "6m")]
    [InlineData("pichle 3 mahine", "3m")]
    public void NamedPeriods_InEnglishRomanUrduAndUrduScript(string text, string expected) => Assert.Equal(expected, Key(text));

    [Theory]
    [InlineData("1 May se 15 May", "range:2026-05-01..2026-05-15")]
    [InlineData("1 may to 15 may", "range:2026-05-01..2026-05-15")]
    [InlineData("from 1/5 to 15/5", "range:2026-05-01..2026-05-15")]
    [InlineData("1 May - 15 May", "range:2026-05-01..2026-05-15")]
    [InlineData("1 مئی سے 15 مئی تک", "range:2026-05-01..2026-05-15")]
    [InlineData("2026-05-01 to 2026-05-31", "range:2026-05-01..2026-05-31")]
    [InlineData("5 May", "range:2026-05-05..2026-05-05")]
    [InlineData("5 May 2025", "range:2025-05-05..2025-05-05")]
    [InlineData("May 5", "range:2026-05-05..2026-05-05")]
    [InlineData("25 Dec", "range:2025-12-25..2025-12-25")] // not yet this year -> the most recent one
    [InlineData("25 Dec 2025 se 5 Jan 2026", "range:2025-12-25..2026-01-05")]
    public void CustomDatesAndRanges(string text, string expected) => Assert.Equal(expected, Key(text));

    [Theory]
    [InlineData("aaj 2pm se 6pm", "range:2026-10-08T14:00..2026-10-08T18:00")]
    [InlineData("aaj 2pm to 6pm", "range:2026-10-08T14:00..2026-10-08T18:00")]
    [InlineData("kal 10am to 2pm", "range:2026-10-07T10:00..2026-10-07T14:00")]
    [InlineData("kal 10:30 am se 2:15 pm tak", "range:2026-10-07T10:30..2026-10-07T14:15")]
    [InlineData("5 May 2 baje se 6 baje tak", "range:2026-05-05T14:00..2026-05-05T18:00")]
    [InlineData("today 14:00 - 18:00", "range:2026-10-08T14:00..2026-10-08T18:00")]
    [InlineData("aaj 9 baje se 5 baje", "range:2026-10-08T09:00..2026-10-08T17:00")]
    [InlineData("tomorrow 11am to 1pm", "range:2026-10-09T11:00..2026-10-09T13:00")]
    public void TimeWindowsOnASingleDay(string text, string expected) => Assert.Equal(expected, Key(text));

    [Theory]
    [InlineData("Ayesha")]
    [InlineData("12")]
    [InlineData("hassan")]
    [InlineData("orders")]
    [InlineData("")]
    [InlineData("kal ka hisab xyz")]
    [InlineData("1 se 15")]            // two bare numbers are not dates
    [InlineData("32 May")]
    [InlineData("last week 2pm to 6pm")] // a time window needs a single day
    public void NotAPeriod_ReturnsNothing_SoNamesAndNumbersAreNeverHijacked(string text) => Assert.Null(Key(text));

    private static ReportPeriod Resolve(string key) => ReportPeriods.Resolve(key, Tz, Now)!;

    [Theory]
    [InlineData("today", "2026-10-07T19:00:00", "2026-10-08T19:00:00")]
    [InlineData("yesterday", "2026-10-06T19:00:00", "2026-10-07T19:00:00")]
    [InlineData("tomorrow", "2026-10-08T19:00:00", "2026-10-09T19:00:00")]
    [InlineData("d-2", "2026-10-05T19:00:00", "2026-10-06T19:00:00")]
    [InlineData("d+2", "2026-10-09T19:00:00", "2026-10-10T19:00:00")]
    [InlineData("thisweek", "2026-10-04T19:00:00", "2026-10-11T19:00:00")]        // Monday 5 Oct .. Sunday 11 Oct
    [InlineData("lastweek", "2026-09-27T19:00:00", "2026-10-04T19:00:00")]
    [InlineData("thismonth", "2026-09-30T19:00:00", "2026-10-31T19:00:00")]
    [InlineData("lastmonth", "2026-08-31T19:00:00", "2026-09-30T19:00:00")]
    [InlineData("thisquarter", "2026-09-30T19:00:00", "2026-12-31T19:00:00")]
    [InlineData("lastquarter", "2026-06-30T19:00:00", "2026-09-30T19:00:00")]
    [InlineData("thisyear", "2025-12-31T19:00:00", "2026-12-31T19:00:00")]
    [InlineData("lastyear", "2024-12-31T19:00:00", "2025-12-31T19:00:00")]
    [InlineData("7d", "2026-10-01T19:00:00", "2026-10-08T19:00:00")]                // today and the 6 days before
    [InlineData("2w", "2026-09-24T19:00:00", "2026-10-08T19:00:00")]
    [InlineData("3m", "2026-07-08T19:00:00", "2026-10-08T19:00:00")]
    [InlineData("range:2026-05-01..2026-05-15", "2026-04-30T19:00:00", "2026-05-15T19:00:00")] // 15 May is included
    [InlineData("range:2026-10-08T14:00..2026-10-08T18:00", "2026-10-08T09:00:00", "2026-10-08T13:00:00")]
    public void Resolve_GivesTheSellersLocalWindowInUtc(string key, string start, string end)
    {
        var period = Resolve(key);
        Assert.Equal(DateTime.Parse(start), period.StartUtc);
        Assert.Equal(DateTime.Parse(end), period.EndUtc);
    }

    [Fact]
    public void Resolve_MarksFuturePeriods()
    {
        Assert.True(Resolve("tomorrow").IsFuture);
        Assert.True(Resolve("d+2").IsFuture);
        Assert.False(Resolve("today").IsFuture);
        Assert.False(Resolve("lastmonth").IsFuture);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("range:2026-05-15..2026-05-01x")]
    [InlineData("range:2026-05-05T18:00..2026-05-05T14:00")]
    public void Resolve_UnknownOrBackwardsKeys_ReturnNull(string key) => Assert.Null(ReportPeriods.Resolve(key, Tz, Now));

    [Theory]
    [InlineData("yesterday", false, "Yesterday")]
    [InlineData("lastquarter", false, "Last quarter")]
    [InlineData("lastyear", true, "پچھلے سال")]
    [InlineData("15d", false, "Last 15 days")]
    [InlineData("range:2026-05-01..2026-05-15", false, "1 May – 15 May 2026")]
    [InlineData("range:2026-05-05..2026-05-05", false, "5 May 2026")]
    [InlineData("range:2026-05-05T14:00..2026-05-05T18:00", false, "5 May, 2:00 PM–6:00 PM")]
    public void Describe_Labels(string key, bool urdu, string expected) => Assert.Equal(expected, ReportPeriods.Describe(key, urdu));

    private static DateTime? Expiry(string text) => ReportPeriods.TryParseExpiry(text, Now, Tz, out var at) ? at : null;

    [Theory]
    // a duration counts from now
    [InlineData("15 days", "2026-10-23T08:00:00")]
    [InlineData("expires 15 days", "2026-10-23T08:00:00")]
    [InlineData("15 din mein", "2026-10-23T08:00:00")]
    [InlineData("15 دن", "2026-10-23T08:00:00")]
    [InlineData("2 weeks", "2026-10-22T08:00:00")]
    [InlineData("2 hafte", "2026-10-22T08:00:00")]
    [InlineData("2 ہفتے", "2026-10-22T08:00:00")]
    [InlineData("3 months", "2027-01-08T08:00:00")]
    [InlineData("3 mahine", "2027-01-08T08:00:00")]
    [InlineData("3 مہینے", "2027-01-08T08:00:00")]
    [InlineData("1 year", "2027-10-08T08:00:00")]
    [InlineData("1 saal", "2027-10-08T08:00:00")]
    // a day or date expires at the end of that local day (Karachi midnight = 19:00 UTC the evening before)
    [InlineData("today", "2026-10-08T19:00:00")]
    [InlineData("tomorrow", "2026-10-09T19:00:00")]
    [InlineData("kal", "2026-10-09T19:00:00")]
    [InlineData("end of week", "2026-10-11T19:00:00")]
    [InlineData("month end", "2026-10-31T19:00:00")]
    [InlineData("end of month", "2026-10-31T19:00:00")]
    [InlineData("mahine ke aakhir tak", "2026-10-31T19:00:00")]
    [InlineData("year end", "2026-12-31T19:00:00")]
    [InlineData("31 Dec", "2026-12-31T19:00:00")]
    [InlineData("expires on 31 Dec 2026", "2026-12-31T19:00:00")]
    [InlineData("1 Oct", "2027-10-01T19:00:00")]            // already past this year -> the next one
    [InlineData("25 دسمبر تک", "2026-12-25T19:00:00")]
    public void DiscountExpiry_Durations_Dates_AndEndsOfPeriods(string text, string expected) =>
        Assert.Equal(DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal), Expiry(text));

    [Theory]
    [InlineData("")]
    [InlineData("foo")]
    [InlineData("0 days")]
    [InlineData("yesterday")]
    public void DiscountExpiry_NotUnderstood_ReturnsNothing(string text) => Assert.Null(Expiry(text));
}
