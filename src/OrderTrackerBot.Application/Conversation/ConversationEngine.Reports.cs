using OrderTrackerBot.Application.Time;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Menu "📊 Reports": "[product] ka report" and "discount performance" — plain SQL aggregates, no AI.
public partial class ConversationEngine
{
    private async Task HandleProductReportAsync(Seller seller, string productName, CancellationToken ct)
    {
        var wanted = productName.Trim().ToLower();
        var products = await _db.Products.Where(p => p.SellerId == seller.Id && p.Name.ToLower().Contains(wanted)).ToListAsync(ct);
        var product = products.FirstOrDefault(p => p.Name.Equals(productName.Trim(), StringComparison.OrdinalIgnoreCase)) ?? products.FirstOrDefault();
        if (product is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("reports.001", productName), ct);
            return;
        }

        var items = await _db.OrderItems
            .Where(i => i.ProductId == product.Id && i.Order!.SellerId == seller.Id && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { i.Quantity, i.UnitPrice, i.Order!.CreatedAt, i.Order.CustomerId })
            .ToListAsync(ct);

        if (items.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("reports.002", Formatters.ProductLabel(product)), ct);
            return;
        }

        var since30 = SellerClock.DaysAgoUtc(30, DateTime.UtcNow);
        var last30 = items.Where(i => i.CreatedAt >= since30).ToList();
        var lastOrder = items.Max(i => i.CreatedAt);
        var daysAgo = (int)(DateTime.UtcNow - lastOrder).TotalDays;

        var lines = new List<string>
        {
            $"📦 {Formatters.ProductLabel(product)} — Report",
            "",
            $"Last 30 din: {last30.Count} orders, {last30.Sum(i => i.Quantity)} units, {Formatters.Money(last30.Sum(i => i.Quantity * i.UnitPrice))}",
            MonthComparisonLine(seller, items.Select(i => (i.CreatedAt, i.Quantity, i.UnitPrice))),
            MessageTemplates.Get("reports.005", items.Count, items.Sum(i => i.Quantity), Formatters.Money(items.Sum(i => i.Quantity * i.UnitPrice))),
            $"Customers: {items.Select(i => i.CustomerId).Distinct().Count()}",
            MessageTemplates.Get("reports.006", (daysAgo == 0 ? "aaj" : $"{daysAgo} din pehle"))
        };
        if (product.StockQty is { } stock) lines.Add($"Stock: {stock}");
        await ReplyAsync(seller, string.Join("\n", lines), ct);
    }

    /// <summary>
    /// This month so far against the same number of days of last month (a fair comparison part-way through a month), plus last month in full. Items are the product's
    /// non-cancelled, non-returned order lines. "kam" / "behtar" is said by units and by amount only when they agree.
    /// </summary>
    private static string MonthComparisonLine(Seller seller, IEnumerable<(DateTime CreatedAt, int Quantity, decimal UnitPrice)> items)
    {
        var now = DateTime.UtcNow;
        var thisMonth = SellerClock.CurrentMonthRangeUtc(seller.TimeZoneId, now);
        var lastMonth = SellerClock.PreviousMonthRangeUtc(seller.TimeZoneId, now);
        var days = Math.Max(1, (int)Math.Ceiling((now - thisMonth.StartUtc).TotalDays));
        var sameWindowEnd = lastMonth.StartUtc.AddDays(days) < lastMonth.EndUtc ? lastMonth.StartUtc.AddDays(days) : lastMonth.EndUtc;
        var list = items.ToList();

        (int Units, decimal Amount) Sum(DateTime from, DateTime to) =>
            list.Where(i => i.CreatedAt >= from && i.CreatedAt < to).Aggregate((Units: 0, Amount: 0m), (a, i) => (a.Units + i.Quantity, a.Amount + i.Quantity * i.UnitPrice));
        var now1 = Sum(thisMonth.StartUtc, thisMonth.EndUtc);
        var before = Sum(lastMonth.StartUtc, sameWindowEnd);
        var full = Sum(lastMonth.StartUtc, lastMonth.EndUtc);

        string verdict;
        if (before.Units == 0 && now1.Units == 0) verdict = "dono mahine mein koi sale nahi";
        else if (before.Units == 0) verdict = "pichle maah isi muddat mein koi sale nahi thi — tulna mumkin nahi.";
        else if (now1.Units > before.Units && now1.Amount >= before.Amount) verdict = $"behtar ✅ (+{(now1.Units - before.Units) * 100 / before.Units}% units)";
        else if (now1.Units < before.Units && now1.Amount <= before.Amount) verdict = $"kam ⚠️ ({(now1.Units - before.Units) * 100 / before.Units}% units)";
        else if (now1.Units == before.Units && now1.Amount == before.Amount) verdict = "barabar";
        else verdict = "milajula (units aur raqam alag disha mein)";

        return $"Is maah ({days} din): {now1.Units} units, {Formatters.Money(now1.Amount)}\n" +
               $"Pichla maah (pehle {days} din): {before.Units} units, {Formatters.Money(before.Amount)} → {verdict}\n" +
               $"Pichla maah (poora): {full.Units} units, {Formatters.Money(full.Amount)}";
    }

    private async Task HandleDiscountPerformanceAsync(Seller seller, string? period, CancellationToken ct)
    {
        // Optional period ("discounts last month", "discount report 1 May se 15 May"): only orders placed in it count.
        var window = period is null ? null : ReportPeriods.Resolve(period, seller.TimeZoneId, DateTime.UtcNow);
        var from = window?.StartUtc ?? DateTime.MinValue;
        var to = window?.EndUtc ?? DateTime.MaxValue;
        var inPeriod = window is null ? "" : $" — {PeriodLabel(seller, period)}";

        // Aggregated in memory: Sqlite can't SUM decimal columns server-side.
        var usage = (await _db.Orders
                .Where(o => o.SellerId == seller.Id && o.DiscountCode != null && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned
                            && o.CreatedAt >= from && o.CreatedAt < to)
                .Select(o => new { Code = o.DiscountCode!, o.DiscountAmount, o.Total })
                .ToListAsync(ct))
            .GroupBy(o => o.Code.ToUpperInvariant())
            .Select(g => new { Code = g.Key, Orders = g.Count(), Given = g.Sum(o => o.DiscountAmount), Revenue = g.Sum(o => o.Total) })
            .ToList();

        var codes = await _db.Discounts.Where(d => d.SellerId == seller.Id).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
        if (codes.Count == 0 && usage.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("reports.003"), ct);
            return;
        }

        var now = DateTime.UtcNow;
        var names = codes.Select(c => c.Code).Union(usage.Select(u => u.Code), StringComparer.OrdinalIgnoreCase).ToList();
        var rows = names
            .Select(code => (Code: code, Stats: usage.FirstOrDefault(u => u.Code.Equals(code, StringComparison.OrdinalIgnoreCase)),
                Discount: codes.FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(r => r.Stats?.Revenue ?? 0)
            .Select(r =>
            {
                var active = r.Discount is { IsActive: true } d && (d.ExpiresAt is null || d.ExpiresAt > now) ? "" : " (inactive)";
                return r.Stats is null
                    ? $"• {r.Code}{active} — {(window is null ? "abhi tak use nahi hua" : "is period mein use nahi hua")}"
                    : $"• {r.Code}{active} — {r.Stats.Orders} orders, sales {Formatters.Money(r.Stats.Revenue)}, discount diya {Formatters.Money(r.Stats.Given)}";
            });

        var totalRevenue = usage.Sum(u => u.Revenue);
        var totalGiven = usage.Sum(u => u.Given);
        await ReplyAsync(seller,
            MessageTemplates.Get("reports.004", inPeriod, string.Join("\n", rows), usage.Sum(u => u.Orders), Formatters.Money(totalRevenue), Formatters.Money(totalGiven)), ct);
    }
}
