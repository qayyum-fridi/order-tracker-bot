using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Profit and realised margin per product over any window ("profit per product", "product wise profit", "margin last month").
// Same rules as "profit": cancelled and returned orders are left out; an item with no cost is left out and counted, never guessed;
// an order's discount is shared across its items in proportion to their price. Delivery is not product margin, so it is left out.
public partial class ConversationEngine
{
    private const int ProductProfitRowsShown = 10;

    private sealed record ProductProfitRow(string Name, int Units, decimal Revenue, decimal Cost)
    {
        public decimal Profit => Revenue - Cost;
        public decimal Margin => Revenue > 0 ? Profit / Revenue * 100m : 0m;
    }

    /// <summary>Per-product sales, cost and profit of the active orders placed in [start, end), plus how many items had no cost.</summary>
    private async Task<(List<ProductProfitRow> Rows, int UncostedItems)> ComputeProductProfitAsync(Seller seller, DateTime start, DateTime end, CancellationToken ct)
    {
        var items = await _db.OrderItems.AsNoTracking()
            .Where(i => i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= start && i.Order.CreatedAt < end
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { i.ProductId, i.ProductNameSnapshot, i.Quantity, i.LineTotal, i.UnitCost, i.Order!.Subtotal, i.Order.DiscountAmount })
            .ToListAsync(ct);

        var productIds = items.Where(i => i.UnitCost is null && i.ProductId is not null).Select(i => i.ProductId!.Value).Distinct().ToList();
        var currentCost = productIds.Count == 0
            ? new Dictionary<int, decimal?>()
            : (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && productIds.Contains(p.Id)).Select(p => new { p.Id, p.CostPrice }).ToListAsync(ct))
                .ToDictionary(p => p.Id, p => p.CostPrice);

        var uncosted = 0;
        var rows = new List<ProductProfitRow>();
        foreach (var group in items.GroupBy(i => i.ProductNameSnapshot.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var costed = 0;
            decimal revenue = 0, cost = 0;
            foreach (var item in group)
            {
                var unitCost = item.UnitCost ?? (item.ProductId is { } id && currentCost.TryGetValue(id, out var current) ? current : null);
                if (unitCost is null) { uncosted++; continue; }
                costed += item.Quantity;
                var share = item.Subtotal > 0 ? Math.Max(0, item.Subtotal - item.DiscountAmount) / item.Subtotal : 0m;
                revenue += item.LineTotal * share;
                cost += unitCost.Value * item.Quantity;
            }
            // A product whose items all lack a cost is not listed: it would show zero sales and zero profit.
            if (costed > 0) rows.Add(new ProductProfitRow(group.Key, costed, revenue, cost));
        }
        return (rows, uncosted);
    }

    private static string ProductProfitLine(int rank, ProductProfitRow row) =>
        MessageTemplates.Get("productprofit.005", rank, row.Name, row.Units, Formatters.Money(row.Revenue), Formatters.Money(row.Profit),
            Math.Round(row.Margin, 0).ToString("0", CultureInfo.InvariantCulture));

    /// <summary>"Product ke hisab se profit": the most profitable products in the window (default last 30 days).</summary>
    private async Task InsightProductProfitAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "30d");
        var (rows, uncosted) = await ComputeProductProfitAsync(seller, start, end, ct);
        if (rows.Count == 0)
        {
            await ReplyAsync(seller, uncosted > 0
                ? MessageTemplates.Get("productprofit.002", label, Industry(seller).CostLine)
                : MessageTemplates.Get("productprofit.001", label), ct);
            return;
        }

        var lines = rows.OrderByDescending(r => r.Profit).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(ProductProfitRowsShown).Select((r, i) => ProductProfitLine(i + 1, r));
        var footer = uncosted > 0 ? MessageTemplates.Get("productprofit.004", uncosted) : "";
        await ReplyAsync(seller, MessageTemplates.Get("productprofit.003", label, string.Join("\n", lines), footer), ct);
        SuggestNext("margin", "profit");
    }

    /// <summary>"margin last month": the margin each product actually made in the period (the sales that happened, not today's prices).</summary>
    private async Task HandleMarginForPeriodAsync(Seller seller, string periodKey, bool lowestFirst, CancellationToken ct)
    {
        var resolved = ReportPeriods.Resolve(periodKey, seller.TimeZoneId, DateTime.UtcNow);
        if (resolved is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("productprofit.001", periodKey), ct);
            return;
        }

        var label = PeriodLabel(seller, periodKey).ToLowerInvariant();
        var (rows, uncosted) = await ComputeProductProfitAsync(seller, resolved.StartUtc, resolved.EndUtc, ct);
        rows = rows.Where(r => r.Revenue > 0).ToList();
        if (rows.Count == 0)
        {
            await ReplyAsync(seller, uncosted > 0
                ? MessageTemplates.Get("productprofit.002", label, Industry(seller).CostLine)
                : MessageTemplates.Get("productprofit.001", label), ct);
            return;
        }

        var ordered = (lowestFirst ? rows.OrderBy(r => r.Margin) : rows.OrderByDescending(r => r.Margin))
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Take(ProductProfitRowsShown).Select((r, i) => ProductProfitLine(i + 1, r));
        var footer = uncosted > 0 ? MessageTemplates.Get("productprofit.004", uncosted) : "";
        await ReplyAsync(seller, MessageTemplates.Get("productprofit.006", label, string.Join("\n", ordered), footer), ct);
        SuggestNext("profit", "margin");
    }

    /// <summary>The per-product part of the delivery-time report: the products named in the sentence, else the fastest ones first.</summary>
    private async Task<string> ProductDeliverySectionAsync(Seller seller, string text, DateTime start, DateTime end, CancellationToken ct)
    {
        var days = await ProductDeliveryDaysAsync(seller, start, end, ct);
        var named = await ProductsInTextAsync(seller, text, ct);
        var chosen = named.Count > 0
            ? days.Where(d => named.Any(n => string.Equals(n.Name, d.Name, StringComparison.OrdinalIgnoreCase))).ToList()
            : days;
        if (chosen.Count == 0) return "";
        var lines = chosen.Take(ProductProfitRowsShown).Select(d => MessageTemplates.Get("productprofit.008", d.Name, Num((decimal)d.Days), d.Orders));
        return MessageTemplates.Get("productprofit.007", string.Join("\n", lines));
    }

    /// <summary>Average shipped-to-delivered days per product (one count per order), fastest first. Used by the delivery-time report.</summary>
    private async Task<List<(string Name, int Orders, double Days)>> ProductDeliveryDaysAsync(Seller seller, DateTime start, DateTime end, CancellationToken ct)
    {
        var rows = await _db.OrderItems.AsNoTracking()
            .Where(i => i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= start && i.Order.CreatedAt < end && i.Order.DeliveredAt != null
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { i.OrderId, i.ProductNameSnapshot, ShippedAt = i.Order!.ShippedAt ?? i.Order.CreatedAt, DeliveredAt = i.Order.DeliveredAt!.Value })
            .ToListAsync(ct);

        return rows
            .DistinctBy(r => (r.OrderId, r.ProductNameSnapshot.Trim().ToLowerInvariant()))
            .GroupBy(r => r.ProductNameSnapshot.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Orders: g.Count(), Days: g.Average(r => Math.Max(0, (r.DeliveredAt - r.ShippedAt).TotalDays))))
            .OrderBy(x => x.Days).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
