using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Api.Admin;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Infrastructure.Persistence;

namespace OrderTrackerBot.Api.Client;

/// <summary>Expenses and wages: money the shop pays out. Dates are Pakistan days; the list is for one range and the total is for the same rows.</summary>
public sealed class ClientExpenses(AppDbContext db)
{
    public async Task<MoneyListDto<ExpenseDto>> ListAsync(int sellerId, AdminDateRange range, CancellationToken ct)
    {
        var rows = await db.Expenses.AsNoTracking()
            .Where(e => e.SellerId == sellerId && e.CreatedAt >= range.StartUtc && e.CreatedAt < range.EndUtc)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).ToListAsync(ct);
        return new MoneyListDto<ExpenseDto>(rows.Select(e => new ExpenseDto(e.Id, e.Amount, e.Category, e.Note, Utc(e.CreatedAt))).ToList(), rows.Sum(e => e.Amount));
    }

    public async Task<(string? Problem, ExpenseDto? Value)> CreateAsync(int sellerId, ExpenseInput input, CancellationToken ct)
    {
        if (input.Amount is not > 0 || input.Amount > 10_000_000) return ("Enter an amount above 0 (up to 10,000,000).", null);
        var category = input.Category?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(category)) return ("Enter a category, for example packaging or petrol.", null);
        if (category.Length > 40) return ("Keep the category to 40 characters.", null);
        if (input.Note?.Length > 200) return ("Keep the note to 200 characters.", null);
        var createdAt = DayTime(input.Date);
        if (createdAt is null) return ("Date must be yyyy-MM-dd and not in the future.", null);

        var expense = new Expense { SellerId = sellerId, Amount = input.Amount!.Value, Category = category, Note = Clean(input.Note), CreatedAt = createdAt.Value };
        db.Expenses.Add(expense);
        await db.SaveChangesAsync(ct);
        return (null, new ExpenseDto(expense.Id, expense.Amount, expense.Category, expense.Note, Utc(expense.CreatedAt)));
    }

    public async Task<bool> DeleteAsync(int sellerId, int id, CancellationToken ct)
    {
        var expense = await db.Expenses.FirstOrDefaultAsync(e => e.SellerId == sellerId && e.Id == id, ct);
        if (expense is null) return false;
        db.Expenses.Remove(expense);
        await db.SaveChangesAsync(ct);
        return true;
    }

    internal static DateTime? DayTime(string? date) => MoneyDate.ToUtc(date);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

public sealed class ClientWages(AppDbContext db)
{
    public async Task<MoneyListDto<WageDto>> ListAsync(int sellerId, AdminDateRange range, CancellationToken ct)
    {
        var rows = await db.WageEntries.AsNoTracking()
            .Where(w => w.SellerId == sellerId && w.CreatedAt >= range.StartUtc && w.CreatedAt < range.EndUtc)
            .OrderByDescending(w => w.CreatedAt).ThenByDescending(w => w.Id).ToListAsync(ct);
        return new MoneyListDto<WageDto>(rows.Select(w => new WageDto(w.Id, w.WorkerName, w.Amount, w.Days, DateTime.SpecifyKind(w.CreatedAt, DateTimeKind.Utc))).ToList(), rows.Sum(w => w.Amount));
    }

    public async Task<(string? Problem, WageDto? Value)> CreateAsync(int sellerId, WageInput input, CancellationToken ct)
    {
        var name = input.WorkerName?.Trim();
        if (string.IsNullOrEmpty(name)) return ("Enter the worker's name.", null);
        if (name.Length > 80) return ("Keep the name to 80 characters.", null);
        if (input.Amount is not > 0 || input.Amount > 10_000_000) return ("Enter an amount above 0 (up to 10,000,000).", null);
        if (input.Days is < 0 or > 366) return ("Days must be between 0 and 366.", null);
        var createdAt = MoneyDate.ToUtc(input.Date);
        if (createdAt is null) return ("Date must be yyyy-MM-dd and not in the future.", null);

        var wage = new WageEntry { SellerId = sellerId, WorkerName = name, Amount = input.Amount!.Value, Days = input.Days, CreatedAt = createdAt.Value };
        db.WageEntries.Add(wage);
        await db.SaveChangesAsync(ct);
        return (null, new WageDto(wage.Id, wage.WorkerName, wage.Amount, wage.Days, DateTime.SpecifyKind(wage.CreatedAt, DateTimeKind.Utc)));
    }

    public async Task<bool> DeleteAsync(int sellerId, int id, CancellationToken ct)
    {
        var wage = await db.WageEntries.FirstOrDefaultAsync(w => w.SellerId == sellerId && w.Id == id, ct);
        if (wage is null) return false;
        db.WageEntries.Remove(wage);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>Stock written off (damaged, expired, lost). Like the WhatsApp "loss" command, it takes the units out of tracked stock.</summary>
public sealed class ClientLosses(AppDbContext db)
{
    public const int MaxQuantity = 9_999;

    public async Task<MoneyListDto<LossDto>> ListAsync(int sellerId, AdminDateRange range, CancellationToken ct)
    {
        var rows = await db.Losses.AsNoTracking()
            .Where(l => l.SellerId == sellerId && l.CreatedAt >= range.StartUtc && l.CreatedAt < range.EndUtc)
            .OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id).ToListAsync(ct);
        return new MoneyListDto<LossDto>(
            rows.Select(l => new LossDto(l.Id, l.ProductId, l.ProductName, l.Quantity, l.UnitCost, l.Reason, DateTime.SpecifyKind(l.CreatedAt, DateTimeKind.Utc))).ToList(),
            rows.Sum(l => l.UnitCost * l.Quantity));
    }

    public async Task<(string? Problem, LossDto? Value)> CreateAsync(int sellerId, LossInput input, CancellationToken ct)
    {
        if (input.Quantity is not > 0 || input.Quantity > MaxQuantity) return ($"Enter a quantity from 1 to {MaxQuantity:N0}.", null);
        if (Clean(input.Reason)?.Length > 200) return ("Keep the reason to 200 characters.", null);

        Product? product = null;
        if (input.ProductId is { } productId)
        {
            product = await db.Products.FirstOrDefaultAsync(p => p.SellerId == sellerId && p.Id == productId, ct);
            if (product is null) return ("Product not found.", null);
        }
        var name = product?.Name ?? Clean(input.ProductName);
        if (name is null) return ("Choose a product.", null);

        var loss = new Loss
        {
            SellerId = sellerId, ProductId = product?.Id, ProductName = name, Quantity = input.Quantity!.Value,
            UnitCost = product?.CostPrice ?? 0, Reason = Clean(input.Reason),
        };
        db.Losses.Add(loss);
        if (product?.StockQty is not null) product.StockQty -= loss.Quantity;
        await db.SaveChangesAsync(ct);
        return (null, new LossDto(loss.Id, loss.ProductId, loss.ProductName, loss.Quantity, loss.UnitCost, loss.Reason, DateTime.SpecifyKind(loss.CreatedAt, DateTimeKind.Utc)));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Parses the yyyy-MM-dd date a dashboard form sends. Blank means now; a future day is refused.</summary>
internal static class MoneyDate
{
    public static DateTime? ToUtc(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return DateTime.UtcNow;
        if (!DateOnly.TryParseExact(date, AdminDateRange.DateFormat, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day)) return null;
        if (day > AdminDateRange.Today()) return null;
        // Keep the current time of day for today, midday for past days, so the day stays right in Pakistan time.
        var at = day == AdminDateRange.Today() ? DateTime.UtcNow : AdminDateRange.ToUtc(day).AddHours(12);
        return DateTime.SpecifyKind(at, DateTimeKind.Utc);
    }
}
