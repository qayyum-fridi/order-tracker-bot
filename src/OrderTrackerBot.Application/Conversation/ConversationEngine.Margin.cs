using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// "margin" / "discount room": for every product whose cost is saved, the margin on its price and how much discount it can take.
//   margin % = (price - cost) / price      break-even discount = the same % (selling at cost)      suggested discount = half of it
// Plain arithmetic on saved prices and costs (no AI): the "suggested" half-margin rule is a guide, said as such in the reply. The last 30 days' units sold are shown
// beside each product (cancelled / returned orders do not count) so the seller sees which ones sell.
public partial class ConversationEngine
{
    private const int MarginRowsShown = 10;

    private async Task HandleMarginAsync(Seller seller, CancellationToken ct, bool lowestFirst = false)
    {
        var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive).OrderBy(p => p.Id).ToListAsync(ct);
        if (products.Count == 0)
        {
            await ReplyAsync(seller, "📊 Margin: aapke catalog mein abhi koi product nahi. Pehle product likhein, jaise: Kurti - 1800, cost 1200", ct);
            return;
        }

        var withCost = products.Where(p => p.CostPrice is not null && p.Price > 0).ToList();
        var noCost = products.Count - withCost.Count;
        if (withCost.Count == 0)
        {
            await ReplyAsync(seller,
                "📊 Margin: kisi product ki cost save nahi, is liye hisaab nahi ho sakta.\n" +
                "Cost ke saath likhein, jaise: \"Kurti - 1800, cost 1200\" (cost sirf aap ko nazar aati hai, receipt par nahi).", ct);
            return;
        }

        var since = SellerClock.LocalDayRangeUtc(seller.TimeZoneId, DateTime.UtcNow, -29).StartUtc;
        var ids = withCost.Select(p => p.Id).ToList();
        var items = await _db.OrderItems.AsNoTracking()
            .Where(i => i.ProductId != null && ids.Contains(i.ProductId.Value)
                        && i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= since
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { ProductId = i.ProductId!.Value, i.Quantity })
            .ToListAsync(ct);
        var sold = items.GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

        var rows = withCost
            .Select(p => (Product: p, Margin: (p.Price - p.CostPrice!.Value) / p.Price * 100m))
            .ToList();
        rows = (lowestFirst ? rows.OrderBy(r => r.Margin) : rows.OrderByDescending(r => r.Margin)).ThenBy(r => r.Product.Name, StringComparer.OrdinalIgnoreCase).ToList();

        static string Pct(decimal v) => Math.Floor(v).ToString("0", CultureInfo.InvariantCulture) + "%";
        var lines = rows.Take(MarginRowsShown).Select((r, i) =>
        {
            var units = sold.TryGetValue(r.Product.Id, out var u) ? u : 0;
            var head = $"{i + 1}. {Formatters.ProductLabel(r.Product)} — {Formatters.Money(r.Product.Price)}, cost {Formatters.Money(r.Product.CostPrice!.Value)}";
            return r.Margin <= 0
                ? $"{head}\n   ⚠️ margin nahi (cost price se kam ya barabar) · 30 din: {units} bike"
                : $"{head}\n   margin {Pct(r.Margin)} · munasib discount ~{Pct(r.Margin / 2)} · is se zyada nahi {Pct(r.Margin)} · 30 din: {units} bike";
        });
        var more = rows.Count > MarginRowsShown ? $"\n…aur {rows.Count - MarginRowsShown} products" : "";
        var missing = noCost > 0 ? $"\n\nℹ️ {noCost} product(s) ki cost save nahi, wo is list mein nahi. Cost likhein: \"Kurti - 1800, cost 1200\"." : "";

        await ReplyAsync(seller,
            (lowestFirst ? "📊 Sab se kam margin wale products\n\n" : "📊 Margin aur discount ki gunjaish\n\n") + string.Join("\n", lines) + more + "\n\n" +
            "Munasib discount = margin ka aadha (is par bhi aadha munafa bachta hai). \"Is se zyada nahi\" par bechne se kuch nahi bachta (cost ke barabar). " +
            "Yeh sirf andaza hai; delivery aur kharcha shamil nahi." + missing, ct);
        SuggestNext("profit", "catalog");
    }
}
