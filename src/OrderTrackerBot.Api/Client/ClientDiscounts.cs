using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>Discount codes for the Config page. Same rules as "create discount": percent 1-100, flat above zero, unique code.</summary>
public sealed class ClientDiscounts(AppDbContext db)
{
    private static readonly Regex CodePattern = new(@"^[A-Z0-9_-]{3,20}$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<DiscountDto>> ListAsync(int sellerId, CancellationToken ct) =>
        (await db.Discounts.AsNoTracking().Where(d => d.SellerId == sellerId).OrderByDescending(d => d.Id).ToListAsync(ct))
        .Select(ToDto).ToList();

    public async Task<(string? Problem, DiscountDto? Value)> CreateAsync(int sellerId, DiscountInput input, CancellationToken ct)
    {
        var code = input.Code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code) || !CodePattern.IsMatch(code)) return ("Code: 3-20 letters, digits, - or _.", null);
        if (!Enum.TryParse<DiscountType>(input.Type, ignoreCase: true, out var type) || !Enum.IsDefined(type)) return ("Choose Percent or Flat.", null);
        if (input.Value is not { } value) return ("Enter the discount value.", null);
        if (type == DiscountType.Percent && (value < 1 || value > 100)) return ("A percent discount must be between 1 and 100.", null);
        if (type == DiscountType.Flat && (value <= 0 || value > 10_000_000)) return ("A flat discount must be above 0.", null);

        DateTime? expiresAt = null;
        if (!string.IsNullOrWhiteSpace(input.ExpiresAt))
        {
            if (!DateOnly.TryParseExact(input.ExpiresAt, AdminDateRange.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return ("Expiry must be a date (yyyy-MM-dd).", null);
            if (day < AdminDateRange.Today()) return ("Expiry date is in the past.", null);
            // A code works through its last Pakistan day, so store the start of the next day.
            expiresAt = AdminDateRange.ToUtc(day.AddDays(1));
        }

        if (await db.Discounts.AnyAsync(d => d.SellerId == sellerId && d.Code.ToUpper() == code, ct))
            return ($"Code {code} already exists.", null);

        var discount = new Discount { SellerId = sellerId, Code = code, Type = type, Value = value, ExpiresAt = expiresAt, IsActive = input.IsActive ?? true };
        db.Discounts.Add(discount);
        await db.SaveChangesAsync(ct);
        return (null, ToDto(discount));
    }

    public async Task<DiscountDto?> SetActiveAsync(int sellerId, int id, bool isActive, CancellationToken ct)
    {
        var discount = await db.Discounts.FirstOrDefaultAsync(d => d.SellerId == sellerId && d.Id == id, ct);
        if (discount is null) return null;
        discount.IsActive = isActive;
        await db.SaveChangesAsync(ct);
        return ToDto(discount);
    }

    private static DiscountDto ToDto(Discount d) => new(
        d.Id, d.Code, d.Type.ToString(), d.Value,
        d.ExpiresAt is { } end ? AdminDateRange.DayOf(end.AddTicks(-1)).ToString(AdminDateRange.DateFormat) : null,
        d.IsActive);
}
