using System.Globalization;

namespace OrderTrackerBot.Api.Admin;

/// <summary>
/// A date range for the admin API, in Pakistan days (Asia/Karachi). <see cref="StartUtc"/> and <see cref="EndUtc"/>
/// are the matching UTC bounds, end exclusive, for filtering stored timestamps.
/// </summary>
public readonly record struct AdminDateRange(DateOnly FromDay, DateOnly ToDay, DateTime StartUtc, DateTime EndUtc)
{
    public const string DateFormat = "yyyy-MM-dd";
    public const int MaxDays = 366;
    public static readonly TimeZoneInfo SellerZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi");

    /// <summary>from/to are required-or-defaulted: a missing side defaults to <paramref name="defaultDays"/> days ending on <c>to</c> (or today).</summary>
    public static bool TryResolve(string? from, string? to, int defaultDays, out AdminDateRange range, out string? error)
    {
        var today = Today();
        range = default;
        error = null;

        if (!TryReadDay(to, today, out var toDay, out error)) return false;
        if (!TryReadDay(from, toDay.AddDays(-(defaultDays - 1)), out var fromDay, out error)) return false;
        return Build(fromDay, toDay, out range, out error);
    }

    /// <summary>
    /// Optional range for list endpoints: send both from and to, or neither (no date filter). Sending only one is an error.
    /// </summary>
    public static bool TryResolveOptional(string? from, string? to, out AdminDateRange? range, out string? error)
    {
        range = null;
        error = null;

        var hasFrom = !string.IsNullOrWhiteSpace(from);
        var hasTo = !string.IsNullOrWhiteSpace(to);
        if (!hasFrom && !hasTo) return true;
        if (hasFrom != hasTo)
        {
            error = "Send both from and to, or neither.";
            return false;
        }

        if (!TryReadDay(from, default, out var fromDay, out error)) return false;
        if (!TryReadDay(to, default, out var toDay, out error)) return false;
        if (!Build(fromDay, toDay, out var resolved, out error)) return false;
        range = resolved;
        return true;
    }

    public static DateTime ToUtc(DateOnly sellerDay) =>
        TimeZoneInfo.ConvertTimeToUtc(sellerDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), SellerZone);

    public static DateOnly DayOf(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), SellerZone));

    public static DateOnly Today() => DayOf(DateTime.UtcNow);

    /// <summary>Reads a yyyy-MM-dd value, or returns <paramref name="fallback"/> when it is blank.</summary>
    private static bool TryReadDay(string? value, DateOnly fallback, out DateOnly day, out string? error)
    {
        error = null;
        day = fallback;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) return true;
        error = $"Dates must be in yyyy-MM-dd format, got '{value}'.";
        return false;
    }

    private static bool Build(DateOnly fromDay, DateOnly toDay, out AdminDateRange range, out string? error)
    {
        range = default;
        error = null;
        if (fromDay > toDay)
        {
            error = "from must be on or before to.";
            return false;
        }
        if (toDay.DayNumber - fromDay.DayNumber + 1 > MaxDays)
        {
            error = $"The range is limited to {MaxDays} days.";
            return false;
        }
        range = new AdminDateRange(fromDay, toDay, ToUtc(fromDay), ToUtc(toDay.AddDays(1)));
        return true;
    }
}
