using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

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
            await ReplyAsync(seller, $"\"{productName}\" catalog mein nahi mila. \"catalog\" likh kar naam check karein.", ct);
            return;
        }

        var items = await _db.OrderItems
            .Where(i => i.ProductId == product.Id && i.Order!.SellerId == seller.Id && i.Order.Status != OrderStatus.Cancelled)
            .Select(i => new { i.Quantity, i.UnitPrice, i.Order!.CreatedAt, i.Order.CustomerId })
            .ToListAsync(ct);

        if (items.Count == 0)
        {
            await ReplyAsync(seller, $"📦 {Formatters.ProductLabel(product)} — abhi tak koi order nahi aaya.\n\nPromote karna ho to \"menu discounts\" dekhein.", ct);
            return;
        }

        var since30 = DateTime.UtcNow.AddDays(-30);
        var last30 = items.Where(i => i.CreatedAt >= since30).ToList();
        var lastOrder = items.Max(i => i.CreatedAt);
        var daysAgo = (int)(DateTime.UtcNow - lastOrder).TotalDays;

        var lines = new List<string>
        {
            $"📦 {Formatters.ProductLabel(product)} — Report",
            "",
            $"Last 30 din: {last30.Count} orders, {last30.Sum(i => i.Quantity)} units, {Formatters.Money(last30.Sum(i => i.Quantity * i.UnitPrice))}",
            $"Ab tak total: {items.Count} orders, {items.Sum(i => i.Quantity)} units, {Formatters.Money(items.Sum(i => i.Quantity * i.UnitPrice))}",
            $"Customers: {items.Select(i => i.CustomerId).Distinct().Count()}",
            $"Last order: {(daysAgo == 0 ? "aaj" : $"{daysAgo} din pehle")}"
        };
        if (product.StockQty is { } stock) lines.Add($"Stock: {stock}");
        await ReplyAsync(seller, string.Join("\n", lines), ct);
    }

    private async Task HandleDiscountPerformanceAsync(Seller seller, CancellationToken ct)
    {
        // Aggregated in memory: Sqlite can't SUM decimal columns server-side.
        var usage = (await _db.Orders
                .Where(o => o.SellerId == seller.Id && o.DiscountCode != null && o.Status != OrderStatus.Cancelled)
                .Select(o => new { Code = o.DiscountCode!, o.DiscountAmount, o.Total })
                .ToListAsync(ct))
            .GroupBy(o => o.Code.ToUpperInvariant())
            .Select(g => new { Code = g.Key, Orders = g.Count(), Given = g.Sum(o => o.DiscountAmount), Revenue = g.Sum(o => o.Total) })
            .ToList();

        var codes = await _db.Discounts.Where(d => d.SellerId == seller.Id).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
        if (codes.Count == 0 && usage.Count == 0)
        {
            await ReplyAsync(seller, "🎟️ Abhi koi discount code nahi bana.\n\"create discount: EID20, 20% off\" se banayein.", ct);
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
                    ? $"• {r.Code}{active} — abhi tak use nahi hua"
                    : $"• {r.Code}{active} — {r.Stats.Orders} orders, sales {Formatters.Money(r.Stats.Revenue)}, discount diya {Formatters.Money(r.Stats.Given)}";
            });

        var totalRevenue = usage.Sum(u => u.Revenue);
        var totalGiven = usage.Sum(u => u.Given);
        await ReplyAsync(seller,
            $"🎟️ Discount Performance:\n\n{string.Join("\n", rows)}\n\n" +
            $"Total: {usage.Sum(u => u.Orders)} orders, {Formatters.Money(totalRevenue)} sales, {Formatters.Money(totalGiven)} discount.", ct);
    }
}
