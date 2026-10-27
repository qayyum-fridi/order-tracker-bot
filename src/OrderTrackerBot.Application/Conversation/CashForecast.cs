using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>One week (or the last part of one) of a cash forecast. Cumulative is the running net from today up to ToDay.</summary>
public sealed record CashForecastStep(int ToDay, decimal In, decimal Out, decimal Cumulative);

/// <summary>
/// Expected cash over the next N days, from the seller's own recent pace: money received (orders marked paid) minus written-down expenses and wages.
/// A guide from history, not a promise; product purchase cost is only counted if the seller wrote it as an expense.
/// </summary>
public sealed record CashForecastResult(
    int Days, int HistoryDays, decimal DailyIn, decimal DailyOut, decimal ExpectedIn, decimal ExpectedOut, decimal Net,
    decimal Receivables, IReadOnlyList<CashForecastStep> Steps)
{
    public bool HasHistory => DailyIn > 0 || DailyOut > 0;
}

public static class CashForecaster
{
    public const int MaxDays = 365;
    private const int HistoryWindow = 30;
    private const int MinHistory = 7;

    public static async Task<CashForecastResult> ComputeAsync(IAppDbContext db, int sellerId, int days, DateTime utcNow, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, MaxDays);

        // Pace is measured over the last 30 days, or since the first order when the shop is newer (never under 7 days, so one big day does not set the pace).
        var firstOrderAt = await db.Orders.AsNoTracking().Where(o => o.SellerId == sellerId).OrderBy(o => o.CreatedAt).Select(o => (DateTime?)o.CreatedAt).FirstOrDefaultAsync(ct);
        var history = firstOrderAt is { } first ? Math.Clamp((int)Math.Ceiling((utcNow - first).TotalDays), MinHistory, HistoryWindow) : HistoryWindow;
        var since = utcNow.AddDays(-history);

        // Sqlite cannot SUM decimals, so rows are added up in memory.
        var paid = await db.Orders.AsNoTracking()
            .Where(o => o.SellerId == sellerId && o.PaidAt != null && o.PaidAt >= since && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .ToListAsync(ct);
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.SellerId == sellerId && e.CreatedAt >= since).Select(e => e.Amount).ToListAsync(ct);
        var wages = await db.WageEntries.AsNoTracking().Where(w => w.SellerId == sellerId && w.CreatedAt >= since).Select(w => w.Amount).ToListAsync(ct);
        var unpaid = await db.Orders.AsNoTracking()
            .Where(o => o.SellerId == sellerId && o.PaymentStatus != PaymentStatus.Paid && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .ToListAsync(ct);

        var dailyIn = paid.Sum(o => OrderMoney.Received(o)) / history;
        var dailyOut = (expenses.Sum() + wages.Sum()) / history;

        var steps = new List<CashForecastStep>();
        for (var from = 0; from < days; from += 7)
        {
            var to = Math.Min(from + 7, days);
            steps.Add(new CashForecastStep(to, Math.Round(dailyIn * (to - from)), Math.Round(dailyOut * (to - from)), Math.Round((dailyIn - dailyOut) * to)));
        }

        return new CashForecastResult(days, history, Math.Round(dailyIn), Math.Round(dailyOut), Math.Round(dailyIn * days), Math.Round(dailyOut * days),
            Math.Round((dailyIn - dailyOut) * days), unpaid.Sum(o => OrderMoney.Balance(o)), steps);
    }
}
