using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

public sealed partial class ConversationEngine
{
    private const int StaleOrderDays = 7;

    /// <summary>
    /// <c>data check</c>: what the reports need that the seller has not entered yet, each with the exact line to type. Counts only (plain SQL), nothing is changed
    /// and nothing is blocked: the seller can ignore it, and the reports say how many items they left out.
    /// </summary>
    private async Task HandleSetupCheckAsync(Seller seller, CancellationToken ct)
    {
        var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive).ToListAsync(ct);
        if (products.Count == 0)
        {
            await ReplyAsync(seller,
                "ℹ️ Catalog abhi khali hai. Pehle products likhein, ek line mein:\n\"Kurti - 1800, cost 1100, stock 20, category kapray\"\n(ya \"import\" se Excel file bhej dein).", ct);
            return;
        }

        var noCost = products.Count(p => p.CostPrice is null);
        var noCategory = products.Count(p => string.IsNullOrWhiteSpace(p.Category));
        var noStock = products.Count(p => p.StockQty is null);
        var staleCutoff = DateTime.UtcNow.AddDays(-StaleOrderDays);
        var stale = await _db.Orders.AsNoTracking().CountAsync(o => o.SellerId == seller.Id && o.CreatedAt < staleCutoff
            && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Shipped), ct);
        var orders = await _db.Orders.AsNoTracking().CountAsync(o => o.SellerId == seller.Id, ct);
        var monthStart = DateTime.UtcNow.AddDays(-30);
        var expenses = await _db.Expenses.AsNoTracking().CountAsync(e => e.SellerId == seller.Id && e.CreatedAt >= monthStart, ct);

        var rows = new List<(bool Ok, string Line)>
        {
            (noCost == 0, noCost == 0 ? "Cost price: sab products mein" : $"Cost price nahi: {noCost}/{products.Count} products — profit/margin adhoora. Likhein: \"Kurti - 1800, cost 1100\""),
            (noCategory == 0, noCategory == 0 ? "Category: sab products mein" : $"Category nahi: {noCategory}/{products.Count} — category reports adhoori. Likhein: \"Kurti - 1800, category kapray\""),
            (noStock == 0, noStock == 0 ? "Stock: sab products ka track hota hai" : $"Stock track nahi: {noStock}/{products.Count} — reorder/dead stock nahi nikal sakta. Likhein: \"stock Kurti 20\""),
            (stale == 0, stale == 0 ? "Purane orders: koi atka hua nahi" : $"{stale} orders {StaleOrderDays}+ din se Pending/Shipped hain — status update karein: \"mark 12 delivered\""),
            (orders == 0 || expenses > 0, orders == 0 || expenses > 0 ? "Kharcha: pichle 30 din ka likha hua hai" : "30 din mein koi kharcha nahi likha — net/P&L adhoora. Likhein: \"expense 5000 rent\""),
        };
        var done = rows.Count(r => r.Ok);
        var lines = string.Join("\n", rows.Select(r => (r.Ok ? "✅ " : "⚠️ ") + r.Line));
        var head = done == rows.Count ? "✅ Aapka data mukammal hai — reports poori tarah kaam karengi." : $"📋 Data check: {done}/{rows.Count} theek";
        await ReplyAsync(seller, $"{head}\n\n{lines}", ct);
        SuggestNext(noCost > 0 ? "catalog" : "margin", noStock > 0 ? "stock" : "audit");
    }

    /// <summary>One gentle reminder per day, after a saved order, while products lack a cost price (flag <c>Features:SetupNudges</c>).</summary>
    private async Task MaybeSendSetupNudgeAsync(Seller seller, SessionContextData ctx, CancellationToken ct)
    {
        if (!_features.SetupNudges) return;
        var day = SellerLocal(seller, DateTime.UtcNow).ToString("yyyy-MM-dd");
        if (ctx.SetupNudgeDay == day) return;
        var noCost = await _db.Products.AsNoTracking().CountAsync(p => p.SellerId == seller.Id && p.IsActive && p.CostPrice == null, ct);
        if (noCost == 0) return;
        ctx.SetupNudgeDay = day;
        await ReplyAsync(seller,
            $"ℹ️ {noCost} products ki cost price nahi likhi, is liye profit sahi nahi aayega. Likhein, jaise: \"Kurti - 1800, cost 1100\". Poori list ke liye \"data check\" likhein.", ct);
    }
}
