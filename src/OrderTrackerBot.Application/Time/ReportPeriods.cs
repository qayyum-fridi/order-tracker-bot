using System.Globalization;
using System.Text.RegularExpressions;

namespace OrderTrackerBot.Application.Time;

/// <summary>A resolved reporting window: [StartUtc, EndUtc) in UTC, plus a human label. <see cref="IsFuture"/> = starts after now (e.g. tomorrow).</summary>
public sealed record ReportPeriod(string Key, DateTime StartUtc, DateTime EndUtc, string Label, bool IsFuture);

/// <summary>
/// One place that understands "for how long": it turns what a seller says or types (English, Roman Urdu, Urdu script) into a canonical
/// period key, and a key into a [start, end) window in the seller's local time.
///
/// Keys: today | yesterday | tomorrow | d-2 (day before yesterday) | d+2 (day after tomorrow) | thisweek | lastweek (Monday-Sunday) |
/// thismonth | lastmonth | thisquarter | lastquarter | thisyear | lastyear | 7d / 30d / 90d... (last N days incl. today) | 2w | 3m |
/// range:2026-05-01..2026-05-15 (whole days, end inclusive) or range:2026-05-05T14:00..2026-05-05T18:00 (exact times, end exclusive).
///
/// "kal" is yesterday unless a future word ("aane wala kal", "agla din", "tomorrow") is present. Anything the text is not entirely made of
/// (a customer's name, an order number) is NOT a period, so callers can safely try this on free text.
/// </summary>
public static class ReportPeriods
{
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private const string B = @"(?<![\p{L}\p{N}])";
    private const string E = @"(?![\p{L}\p{N}])";

    // Words that carry no period meaning ("kal ke orders", "orders for last month", "pichle mahine ka"): dropped before matching.
    private static readonly Regex Filler = new(B + @"(?:ka|ke|ki|kay|mein|main|me|for|of|in|the|during|on|from|az|ko|total|poore|poora|پورے|پورا|کا|کے|کی|میں|کو|از)" + E, Opts);
    private static readonly Regex TrailingTak = new(@"\s*(?<![\p{L}\p{N}])(?:tak|تک)$", Opts);
    private static readonly Regex Trim = new(@"[\s.,;:!?؟۔،…]+", Opts);

    private static readonly Regex FutureMarker = new(B + @"(?:tomorrow|aane\s+wala|aanay\s+wala|aane\s+wale|aanay\s+wale|agla\s+din|agle\s+din|next\s+day|آنے\s+والا|آنے\s+والے|آئندہ|اگلا\s+دن|اگلے\s+دن)" + E, Opts);
    private static readonly Regex DayAfterYesterday = new(B + @"(?:parso|parson|parsoon|پرسوں|day\s+before\s+yesterday|day\s+after\s+tomorrow)" + E, Opts);

    private static readonly (string Key, Regex Pattern)[] Named =
    {
        ("today", new(@"^(?:today|aaj|آج|aj)$", Opts)),
        ("thisweek", new(@"^(?:(?:this|current)\s+week|(?:is|iss|isi)\s+(?:hafte|haftay|hafta|week)|اس\s+ہفتے|اس\s+ہفتہ|haftay)$", Opts)),
        ("lastweek", new(@"^(?:last\s+week|previous\s+week|past\s+week|(?:pichle|pichlay|pichla|guzishta|guzashta)\s+(?:hafte|haftay|hafta|week)|(?:پچھلے|پچھلا|گزشتہ)\s+(?:ہفتے|ہفتہ))$", Opts)),
        ("lastmonth", new(@"^(?:last\s+month|previous\s+month|(?:pichle|pichlay|pichla|guzishta|guzashta)\s+(?:mahine|mahinay|mahiny|mahina|maah|maheene|mah)|(?:پچھلے|پچھلا|گزشتہ)\s+(?:مہینے|مہینہ|ماہ))$", Opts)),
        ("thismonth", new(@"^(?:(?:this|current)\s+month|(?:is|iss|isi)\s+(?:mahine|mahinay|mahina|maah|maheene|mah|month)|اس\s+مہینے|اس\s+ماہ|monthly|mahana|mahaana|ماہانہ|month|mahina|mahine|مہینہ|مہینے)$", Opts)),
        ("lastquarter", new(@"^(?:last\s+quarter|previous\s+quarter|(?:pichli|pichle|pichla|guzishta)\s+(?:quarter|sehmahi|sehmaahi)|(?:پچھلی|گزشتہ)\s+سہ\s*ماہی)$", Opts)),
        ("thisquarter", new(@"^(?:(?:this|current)\s+quarter|(?:is|iss|isi)\s+(?:quarter|sehmahi|sehmaahi)|اس\s+سہ\s*ماہی|quarterly|quarter|sehmahi|sehmaahi|سہ\s*ماہی)$", Opts)),
        ("lastyear", new(@"^(?:last\s+year|previous\s+year|(?:pichle|pichlay|pichla|guzishta|guzashta)\s+(?:saal|sal|year)|(?:پچھلے|پچھلا|گزشتہ)\s+سال)$", Opts)),
        ("thisyear", new(@"^(?:(?:this|current)\s+year|(?:is|iss|isi)\s+(?:saal|sal|year)|اس\s+سال|yearly|annual|annually|saalana|salana|سالانہ|year|saal|sal|سال)$", Opts)),
        ("7d", new(@"^(?:week|weekly|hafta|hafte|haftay|haftawar|ہفتہ|ہفتے|ہفتہ\s+وار)$", Opts)),
    };

    private static readonly Regex LastNDays = new(@"^(?:(?:last|past|pichle|pichlay|guzishta|پچھلے|گزشتہ)\s+)?(?<n>\d{1,4})\s*(?:days?|din|dino|دن)$", Opts);
    private static readonly Regex LastNWeeks = new(@"^(?:(?:last|past|pichle|pichlay|guzishta|پچھلے|گزشتہ)\s+)?(?<n>\d{1,3})\s*(?:weeks?|hafte|haftay|ہفتے)$", Opts);
    private static readonly Regex LastNMonths = new(@"^(?:(?:last|past|pichle|pichlay|guzishta|پچھلے|گزشتہ)\s+)?(?<n>\d{1,3})\s*(?:months?|mahine|mahinay|mahino|مہینے|مہینوں)$", Opts);

    private static readonly Regex RangeSeparator = new(@"\s+(?:se|to|till|until|tak|-|–|سے|تک)\s+|\s*[–—]\s*", Opts);
    // "2pm to 6pm", "2 baje se 6 baje tak", "14:00-18:00": only counts as a time window when an am/pm/baje or a colon is present.
    private static readonly Regex TimeWindow = new(
        @"(?<![\d:])(?<h1>\d{1,2})(?::(?<m1>\d{2}))?\s*(?<a1>am|pm|baje|bje|بجے)?\s*(?:se|to|till|until|-|–|سے)\s*(?<h2>\d{1,2})(?::(?<m2>\d{2}))?\s*(?<a2>am|pm|baje|bje|بجے)?(?:\s*(?:tak|تک))?(?![\d:])", Opts);

    private static readonly Dictionary<string, int> Months = BuildMonths();

    private static Dictionary<string, int> BuildMonths()
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = new[]
        {
            new[] { "jan", "january", "جنوری" }, new[] { "feb", "february", "فروری" }, new[] { "mar", "march", "مارچ" },
            new[] { "apr", "april", "اپریل" }, new[] { "may", "مئی" }, new[] { "jun", "june", "جون" },
            new[] { "jul", "july", "جولائی" }, new[] { "aug", "august", "اگست" }, new[] { "sep", "sept", "september", "ستمبر" },
            new[] { "oct", "october", "اکتوبر" }, new[] { "nov", "november", "نومبر" }, new[] { "dec", "december", "دسمبر" }
        };
        for (var i = 0; i < names.Length; i++)
            foreach (var n in names[i]) d[n] = i + 1;
        return d;
    }

    private static readonly Regex DayMonthName = new(@"^(?<d>\d{1,2})(?:st|nd|rd|th)?\s*(?<mon>[a-z؀-ۿ]+)(?:\s*,?\s*(?<y>\d{4}))?$", Opts);
    private static readonly Regex MonthNameDay = new(@"^(?<mon>[a-z]+)\s*(?<d>\d{1,2})(?:st|nd|rd|th)?(?:\s*,?\s*(?<y>\d{4}))?$", Opts);
    private static readonly Regex NumericDate = new(@"^(?<d>\d{1,2})[/\-.](?<m>\d{1,2})(?:[/\-.](?<y>\d{2,4}))?$", Opts);
    private static readonly Regex IsoDate = new(@"^(?<y>\d{4})-(?<m>\d{1,2})-(?<d>\d{1,2})$", Opts);

    // ---------------------------------------------------------------- parsing

    /// <summary>
    /// True when <paramref name="text"/> is, in its entirety, a period expression. <paramref name="utcNow"/> only matters for dates typed
    /// without a year ("5 May" = the most recent 5 May) and for "kal" ordering.
    /// </summary>
    public static bool TryParseKey(string? text, DateTime utcNow, out string key)
    {
        key = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var today = utcNow.AddHours(5).Date; // PKT: only decides which year a year-less date belongs to

        var norm = Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " ");

        // Optional time window ("2pm se 6pm"): applies to a single day only.
        TimeSpan? from = null, to = null;
        var tw = TimeWindow.Match(norm);
        if (tw.Success && TryBuildWindow(tw, out var f, out var t))
        {
            from = f;
            to = t;
            norm = (norm[..tw.Index] + " " + norm[(tw.Index + tw.Length)..]).Trim();
        }

        var cleaned = Trim.Replace(Filler.Replace(norm, " "), " ").Trim();
        cleaned = TrailingTak.Replace(cleaned, "").Trim(); // "1 May se 15 May tak"
        if (cleaned.Length == 0 && from is null) return false;

        string? dayKey = null; // a single local day, so a time window can be attached to it
        DateTime? dayDate = null;

        if (cleaned.Length == 0)
        {
            return false;
        }
        else if (FutureMarker.IsMatch(cleaned) || (DayAfterYesterday.IsMatch(cleaned) && cleaned.Contains("after", StringComparison.Ordinal)))
        {
            var rest = Trim.Replace(FutureMarker.Replace(DayAfterYesterday.Replace(cleaned, " "), " "), " ").Trim();
            var dayAfter = DayAfterYesterday.IsMatch(cleaned);
            // Only "tomorrow"/"kal"/"day after tomorrow"/"din" words may remain.
            if (rest.Length > 0 && !Regex.IsMatch(rest, @"^(?:kal|din|day|کل|دن)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
            dayKey = dayAfter ? "d+2" : "tomorrow";
            dayDate = today.AddDays(dayAfter ? 2 : 1);
        }
        else if (Regex.IsMatch(cleaned, @"^(?:yesterday|kal|کل|guzishta\s+kal|گزشتہ\s+کل|pichla\s+din|pichle\s+din|پچھلا\s+دن)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            dayKey = "yesterday";
            dayDate = today.AddDays(-1);
        }
        else if (DayAfterYesterday.IsMatch(cleaned) && Regex.IsMatch(cleaned, @"^(?:parso|parson|parsoon|پرسوں|day\s+before\s+yesterday)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            dayKey = "d-2";
            dayDate = today.AddDays(-2);
        }
        else if (Named[0].Pattern.IsMatch(cleaned))
        {
            dayKey = "today";
            dayDate = today;
        }

        if (dayKey is not null)
        {
            key = from is { } fromTime && to is { } toTime
                ? $"range:{dayDate!.Value:yyyy-MM-dd}T{fromTime:hh\\:mm}..{dayDate.Value:yyyy-MM-dd}T{toTime:hh\\:mm}"
                : dayKey;
            return true;
        }

        // A time window only makes sense on a single day (named above, or a typed date below).
        if (from is null)
        {
            foreach (var (namedKey, pattern) in Named.Skip(1))
            {
                if (!pattern.IsMatch(cleaned)) continue;
                key = namedKey;
                return true;
            }

            Match m;
            if ((m = LastNDays.Match(cleaned)).Success) return TryCountKey(m, "d", 3650, out key);
            if ((m = LastNWeeks.Match(cleaned)).Success) return TryCountKey(m, "w", 520, out key);
            if ((m = LastNMonths.Match(cleaned)).Success) return TryCountKey(m, "m", 120, out key);
        }

        // Custom dates: "1 May se 15 May", "from 1/5 to 15/5", "5 May", "2026-05-01".
        var parts = RangeSeparator.Split(cleaned).Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        if (parts.Length == 2 && from is null
            && TryParseDate(parts[0], today, out var start) && TryParseDate(parts[1], today, out var end))
        {
            if (end < start) end = end.AddYears(1);
            key = $"range:{start:yyyy-MM-dd}..{end:yyyy-MM-dd}";
            return true;
        }

        if (parts.Length == 1 && TryParseDate(parts[0], today, out var single))
        {
            key = from is { } f2 && to is { } t2
                ? $"range:{single:yyyy-MM-dd}T{f2:hh\\:mm}..{single:yyyy-MM-dd}T{t2:hh\\:mm}"
                : $"range:{single:yyyy-MM-dd}..{single:yyyy-MM-dd}";
            return true;
        }

        return false;
    }

    private static bool TryCountKey(Match m, string unit, int max, out string key)
    {
        key = "";
        if (!int.TryParse(m.Groups["n"].Value, out var n) || n < 1 || n > max) return false;
        key = $"{n}{unit}";
        return true;
    }

    private static bool TryBuildWindow(Match m, out TimeSpan from, out TimeSpan to)
    {
        from = to = TimeSpan.Zero;
        var a1 = m.Groups["a1"].Value.ToLowerInvariant();
        var a2 = m.Groups["a2"].Value.ToLowerInvariant();
        var hasMarker = a1.Length > 0 || a2.Length > 0 || m.Groups["m1"].Success || m.Groups["m2"].Success;
        if (!hasMarker) return false;

        if (!int.TryParse(m.Groups["h1"].Value, out var h1) || !int.TryParse(m.Groups["h2"].Value, out var h2)) return false;
        var m1 = m.Groups["m1"].Success ? int.Parse(m.Groups["m1"].Value) : 0;
        var m2 = m.Groups["m2"].Success ? int.Parse(m.Groups["m2"].Value) : 0;
        if (h1 > 24 || h2 > 24 || m1 > 59 || m2 > 59) return false;

        var markerA1 = a1 is "am" or "pm" ? a1 : null;
        var markerA2 = a2 is "am" or "pm" ? a2 : markerA1; // "2 se 6pm": the second marker covers both
        markerA1 ??= a2 is "am" or "pm" ? a2 : null;

        h1 = To24(h1, markerA1);
        h2 = To24(h2, markerA2);
        // "9 baje se 5 baje": no am/pm and the end is not after the start -> the end is the evening.
        if (markerA1 is null && markerA2 is null && h2 <= h1 && h2 < 12) h2 += 12;
        if (h2 > 24 || (h2 == 24 && m2 > 0)) return false;

        from = new TimeSpan(h1 % 24, m1, 0);
        to = h2 == 24 ? new TimeSpan(23, 59, 0) : new TimeSpan(h2, m2, 0);
        return to > from;
    }

    private static int To24(int hour, string? marker)
    {
        if (marker == "pm") return hour is >= 1 and <= 11 ? hour + 12 : hour;
        if (marker == "am") return hour == 12 ? 0 : hour;
        // No marker: shop hours 1-7 mean the afternoon ("2 baje" = 14:00).
        return hour is >= 1 and <= 7 ? hour + 12 : hour;
    }

    private static bool TryParseDate(string token, DateTime today, out DateTime date, bool preferFuture = false)
    {
        date = default;
        token = token.Trim().TrimEnd(',', '.');
        int day, month;
        int? year = null;

        Match m;
        if ((m = IsoDate.Match(token)).Success)
        {
            year = int.Parse(m.Groups["y"].Value);
            month = int.Parse(m.Groups["m"].Value);
            day = int.Parse(m.Groups["d"].Value);
        }
        else if ((m = NumericDate.Match(token)).Success)
        {
            day = int.Parse(m.Groups["d"].Value);
            month = int.Parse(m.Groups["m"].Value);
            if (m.Groups["y"].Success)
            {
                var y = int.Parse(m.Groups["y"].Value);
                year = y < 100 ? 2000 + y : y;
            }
        }
        else if ((m = DayMonthName.Match(token)).Success && Months.TryGetValue(m.Groups["mon"].Value, out month))
        {
            day = int.Parse(m.Groups["d"].Value);
            if (m.Groups["y"].Success) year = int.Parse(m.Groups["y"].Value);
        }
        else if ((m = MonthNameDay.Match(token)).Success && Months.TryGetValue(m.Groups["mon"].Value, out month))
        {
            day = int.Parse(m.Groups["d"].Value);
            if (m.Groups["y"].Success) year = int.Parse(m.Groups["y"].Value);
        }
        else
        {
            return false;
        }

        if (month is < 1 or > 12 || day is < 1 or > 31) return false;
        try
        {
            if (year is { } y2)
            {
                date = new DateTime(y2, month, day);
                return y2 is >= 2000 and <= 2100;
            }

            // No year typed: the most recent such date (today included) for reports, the next upcoming one for an expiry.
            var candidate = new DateTime(today.Year, month, day);
            date = preferFuture ? (candidate < today ? candidate.AddYears(1) : candidate) : (candidate > today ? candidate.AddYears(-1) : candidate);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- expiry ("how long is it valid")

    private static readonly Regex ExpiryWords = new(B + @"(?:expires?|expiry|expiring|valid|validity|for|in|within|after|mein|main|me|ke|ki|ka|baad|tak|till|until|by|on|ختم|تک|میں|کے|کی|کا|بعد)" + E, Opts);
    private static readonly Regex ExpiryCount = new(@"^(?<n>\d{1,4})\s*(?<u>days?|din|dino|دن|weeks?|hafte|haftay|ہفتے|months?|mahine|mahinay|mahino|مہینے|مہینوں|years?|saal|sal|سال)$", Opts);
    private static readonly Regex EndOfWeek = new(@"^(?:end\s+of\s+(?:the\s+)?week|week\s*end|(?:hafte|haftay|hafta)\s+(?:end|aakhir|akhir)|ہفتے\s+آخر)$", Opts);
    private static readonly Regex EndOfMonth = new(@"^(?:end\s+of\s+(?:the\s+)?month|month\s*end|(?:mahine|mahinay|maah|mahina)\s+(?:end|aakhir|akhir)|(?:مہینے|ماہ)\s+آخر)$", Opts);
    private static readonly Regex EndOfYear = new(@"^(?:end\s+of\s+(?:the\s+)?year|year\s*end|(?:saal|sal)\s+(?:end|aakhir|akhir)|سال\s+آخر)$", Opts);

    /// <summary>
    /// When a discount code stops working, from "15 days", "2 weeks", "3 mahine", "1 saal", "tomorrow", "kal", "month end", "year end",
    /// "31 Dec" / "31 Dec 2026" (a date means the end of that day, the next one coming up when no year is typed), or the Urdu forms.
    /// A duration counts from now; a day or date expires at the end of that local day.
    /// </summary>
    public static bool TryParseExpiry(string? text, DateTime utcNow, string? timeZoneId, out DateTime expiresAtUtc)
    {
        expiresAtUtc = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var norm = Trim.Replace(ExpiryWords.Replace(Regex.Replace(text.Trim().ToLowerInvariant(), @"\s+", " "), " "), " ").Trim();
        if (norm.Length == 0) return false;

        var m = ExpiryCount.Match(norm);
        if (m.Success)
        {
            var n = int.Parse(m.Groups["n"].Value);
            if (n < 1) return false;
            var unit = m.Groups["u"].Value;
            var isWeek = unit.StartsWith("week", StringComparison.OrdinalIgnoreCase) || unit.StartsWith("haft") || unit == "ہفتے";
            var isMonth = unit.StartsWith("month", StringComparison.OrdinalIgnoreCase) || unit.StartsWith("mah") || unit is "مہینے" or "مہینوں";
            var isYear = unit.StartsWith("year", StringComparison.OrdinalIgnoreCase) || unit.StartsWith("sa") || unit == "سال";
            expiresAtUtc = isWeek ? utcNow.AddDays(7.0 * n) : isMonth ? utcNow.AddMonths(n) : isYear ? utcNow.AddYears(n) : utcNow.AddDays(n);
            return true;
        }

        var today = SellerClock.LocalToday(timeZoneId, utcNow);
        DateTime? endOfDay = null;
        if (Regex.IsMatch(norm, @"^(?:today|aaj|آج)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) endOfDay = today.AddDays(1);
        else if (Regex.IsMatch(norm, @"^(?:tomorrow|kal|کل|agle\s+din|agla\s+din|اگلے\s+دن)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) endOfDay = today.AddDays(2);
        else if (EndOfWeek.IsMatch(norm)) endOfDay = today.AddDays(7 - (((int)today.DayOfWeek + 6) % 7));
        else if (EndOfMonth.IsMatch(norm)) endOfDay = new DateTime(today.Year, today.Month, 1).AddMonths(1);
        else if (EndOfYear.IsMatch(norm)) endOfDay = new DateTime(today.Year + 1, 1, 1);
        else if (TryParseDate(norm, today, out var date, preferFuture: true)) endOfDay = date.AddDays(1);

        if (endOfDay is not { } end) return false;
        expiresAtUtc = SellerClock.LocalToUtc(timeZoneId, end);
        return expiresAtUtc > utcNow;
    }

    // ---------------------------------------------------------------- resolving

    /// <summary>The [start, end) window of a key in the seller's local time, or null when the key is unknown.</summary>
    public static ReportPeriod? Resolve(string? key, string? timeZoneId, DateTime utcNow, bool urdu = false)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var today = SellerClock.LocalToday(timeZoneId, utcNow);
        DateTime S(DateTime localDate) => SellerClock.LocalToUtc(timeZoneId, localDate.Date);

        ReportPeriod Make(DateTime startLocal, DateTime endLocal, string? label = null)
        {
            var start = S(startLocal);
            return new ReportPeriod(key, start, S(endLocal), label ?? Describe(key, urdu), start > utcNow);
        }

        switch (key)
        {
            case "today": return Make(today, today.AddDays(1));
            case "yesterday": return Make(today.AddDays(-1), today);
            case "tomorrow": return Make(today.AddDays(1), today.AddDays(2));
            case "d-2": return Make(today.AddDays(-2), today.AddDays(-1));
            case "d+2": return Make(today.AddDays(2), today.AddDays(3));
            case "thisweek":
            case "lastweek":
            {
                var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
                if (key == "lastweek") monday = monday.AddDays(-7);
                return Make(monday, monday.AddDays(7));
            }
            case "thismonth": return Make(new DateTime(today.Year, today.Month, 1), new DateTime(today.Year, today.Month, 1).AddMonths(1));
            case "lastmonth": return Make(new DateTime(today.Year, today.Month, 1).AddMonths(-1), new DateTime(today.Year, today.Month, 1));
            case "thisquarter":
            case "lastquarter":
            {
                var qStart = new DateTime(today.Year, ((today.Month - 1) / 3) * 3 + 1, 1);
                if (key == "lastquarter") qStart = qStart.AddMonths(-3);
                return Make(qStart, qStart.AddMonths(3));
            }
            case "thisyear": return Make(new DateTime(today.Year, 1, 1), new DateTime(today.Year + 1, 1, 1));
            case "lastyear": return Make(new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year, 1, 1));
        }

        var count = Regex.Match(key, @"^(?<n>\d{1,4})(?<u>[dwm])$");
        if (count.Success)
        {
            var n = int.Parse(count.Groups["n"].Value);
            var unit = count.Groups["u"].Value;
            var startLocal = unit switch { "d" => today.AddDays(-(n - 1)), "w" => today.AddDays(-(n * 7 - 1)), _ => today.AddMonths(-n).AddDays(1) };
            return Make(startLocal, today.AddDays(1));
        }

        var range = Regex.Match(key, @"^range:(?<a>\d{4}-\d{2}-\d{2})(?:T(?<ta>\d{2}:\d{2}))?\.\.(?<b>\d{4}-\d{2}-\d{2})(?:T(?<tb>\d{2}:\d{2}))?$");
        if (range.Success
            && DateTime.TryParseExact(range.Groups["a"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var a)
            && DateTime.TryParseExact(range.Groups["b"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var b))
        {
            var timed = range.Groups["ta"].Success && range.Groups["tb"].Success;
            DateTime startLocal, endLocal;
            if (timed)
            {
                startLocal = a + TimeSpan.Parse(range.Groups["ta"].Value, CultureInfo.InvariantCulture);
                endLocal = b + TimeSpan.Parse(range.Groups["tb"].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                startLocal = a;
                endLocal = b.AddDays(1);
            }

            if (endLocal <= startLocal) return null;
            var start = SellerClock.LocalToUtc(timeZoneId, startLocal);
            return new ReportPeriod(key, start, SellerClock.LocalToUtc(timeZoneId, endLocal), Describe(key, urdu), start > utcNow);
        }

        return null;
    }

    // ---------------------------------------------------------------- labels

    /// <summary>A short label for a key ("Last quarter", "1 May – 15 May 2026", "5 May, 2:00 PM–6:00 PM"); Urdu script for the named periods when asked.</summary>
    public static string Describe(string? key, bool urdu = false)
    {
        switch (key)
        {
            case null or "": return urdu ? "آج" : "Today";
            case "today": return urdu ? "آج" : "Today";
            case "yesterday": return urdu ? "کل" : "Yesterday";
            case "tomorrow": return urdu ? "آنے والا کل" : "Tomorrow";
            case "d-2": return urdu ? "پرسوں" : "Day before yesterday";
            case "d+2": return urdu ? "آنے والا پرسوں" : "Day after tomorrow";
            case "thisweek": return urdu ? "اس ہفتے" : "This week";
            case "lastweek": return urdu ? "پچھلے ہفتے" : "Last week";
            case "thismonth": return urdu ? "اس مہینے" : "This month";
            case "lastmonth": return urdu ? "پچھلے مہینے" : "Last month";
            case "thisquarter": return urdu ? "اس سہ ماہی" : "This quarter";
            case "lastquarter": return urdu ? "پچھلی سہ ماہی" : "Last quarter";
            case "thisyear": return urdu ? "اس سال" : "This year";
            case "lastyear": return urdu ? "پچھلے سال" : "Last year";
        }

        var count = Regex.Match(key, @"^(?<n>\d{1,4})(?<u>[dwm])$");
        if (count.Success)
        {
            var n = count.Groups["n"].Value;
            return count.Groups["u"].Value switch
            {
                "d" => urdu ? $"پچھلے {n} دن" : $"Last {n} days",
                "w" => urdu ? $"پچھلے {n} ہفتے" : $"Last {n} weeks",
                _ => urdu ? $"پچھلے {n} مہینے" : $"Last {n} months"
            };
        }

        var range = Regex.Match(key, @"^range:(?<a>\d{4}-\d{2}-\d{2})(?:T(?<ta>\d{2}:\d{2}))?\.\.(?<b>\d{4}-\d{2}-\d{2})(?:T(?<tb>\d{2}:\d{2}))?$");
        if (range.Success
            && DateTime.TryParseExact(range.Groups["a"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var a)
            && DateTime.TryParseExact(range.Groups["b"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var b))
        {
            var inv = CultureInfo.InvariantCulture;
            if (range.Groups["ta"].Success && range.Groups["tb"].Success)
            {
                var ta = DateTime.Today + TimeSpan.Parse(range.Groups["ta"].Value, inv);
                var tb = DateTime.Today + TimeSpan.Parse(range.Groups["tb"].Value, inv);
                return $"{a.ToString("d MMM", inv)}, {ta.ToString("h:mm tt", inv)}–{tb.ToString("h:mm tt", inv)}";
            }

            if (a == b) return a.ToString("d MMM yyyy", inv);
            return a.Year == b.Year ? $"{a.ToString("d MMM", inv)} – {b.ToString("d MMM yyyy", inv)}" : $"{a.ToString("d MMM yyyy", inv)} – {b.ToString("d MMM yyyy", inv)}";
        }

        return key;
    }
}
