using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// "profit" / "profit today|yesterday|week|month|last month": sales minus product cost for active orders.
// Cost comes from OrderItem.UnitCost (copied from Product.CostPrice when the order was saved); items without it fall back to the
// product's current cost. An order with any item still lacking a cost is left out and counted, so profit is never overstated.
// Delivery is excluded from both sides (it is a pass-through, not product margin).
public partial class ConversationEngine
{
    private async Task HandleProfitAsync(Seller seller, string? period, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Anything beyond the original today / yesterday / last month / 7 / 30 days (this week, last quarter, a date range...) comes from ReportPeriods.
        var custom = period is null or "today" or "yesterday" or "lastmonth" or "7d" or "30d" ? null : ReportPeriods.Resolve(period, seller.TimeZoneId, now);
        var (start, end, label) = custom is not null ? (custom.StartUtc, custom.EndUtc, PeriodLabel(seller, period).ToLowerInvariant()) : period switch
        {
            "today" => (SellerClock.StartOfLocalDayUtc(seller.TimeZoneId, now), DateTime.MaxValue, "aaj"),
            "yesterday" => (SellerClock.LocalDayRangeUtc(seller.TimeZoneId, now, -1).StartUtc, SellerClock.LocalDayRangeUtc(seller.TimeZoneId, now, -1).EndUtc, "kal"),
            "lastmonth" => (SellerClock.PreviousMonthRangeUtc(seller.TimeZoneId, now).StartUtc, SellerClock.PreviousMonthRangeUtc(seller.TimeZoneId, now).EndUtc, "pichla maah"),
            "7d" => (SellerClock.LocalDayRangeUtc(seller.TimeZoneId, now, -6).StartUtc, DateTime.MaxValue, "pichle 7 din"),
            _ => (SellerClock.LocalDayRangeUtc(seller.TimeZoneId, now, -29).StartUtc, DateTime.MaxValue, "pichle 30 din")
        };

        var orders = await _db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end
                        && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .ToListAsync(ct);

        var productIds = orders.SelectMany(o => o.Items).Where(i => i.UnitCost is null && i.ProductId is not null).Select(i => i.ProductId!.Value).Distinct().ToList();
        var currentCost = productIds.Count == 0
            ? new Dictionary<int, decimal?>()
            : (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && productIds.Contains(p.Id)).Select(p => new { p.Id, p.CostPrice }).ToListAsync(ct))
                .ToDictionary(p => p.Id, p => p.CostPrice);

        decimal? CostOf(OrderItem item) => item.UnitCost ?? (item.ProductId is { } id && currentCost.TryGetValue(id, out var c) ? c : null);

        decimal revenue = 0, cost = 0;
        var counted = 0;
        foreach (var order in orders)
        {
            var costs = order.Items.Select(i => (Item: i, Cost: CostOf(i))).ToList();
            if (costs.Count == 0 || costs.Any(c => c.Cost is null)) continue;
            revenue += Math.Max(0, order.Subtotal - order.DiscountAmount);
            cost += costs.Sum(c => c.Cost!.Value * c.Item.Quantity);
            counted++;
        }

        if (orders.Count == 0)
        {
            await ReplyAsync(seller, $"💰 Profit ({label}): is dauran koi order nahi hai.", ct);
            return;
        }

        if (counted == 0)
        {
            await ReplyAsync(seller,
                $"💰 Profit ({label}): abhi hisaab nahi ho sakta — kisi product ki cost save nahi hai.\n" +
                "Product ke saath cost likhein, jaise: \"Kurti - 1800, cost 1200\".", ct);
            return;
        }

        var skipped = orders.Count - counted;
        var skippedNote = skipped == 0 ? "" :
            $"\n⚠️ {skipped} order(s) shamil nahi — cost maloom nahi. Cost likhein, jaise: \"Kurti - 1800, cost 1200\".";

        var profit = revenue - cost;
        var margin = revenue > 0 ? Math.Round(profit / revenue * 100m, 1) : 0m;
        await ReplyAsync(seller,
            $"💰 Profit — {label}\n\n" +
            $"Orders: {counted}\n" +
            $"Sales (delivery ke baghair): {Formatters.Money(revenue)}\n" +
            $"Cost: {Formatters.Money(cost)}\n" +
            $"Profit: {Formatters.Money(profit)} ({margin}%)" + skippedNote, ct);
    }
}
