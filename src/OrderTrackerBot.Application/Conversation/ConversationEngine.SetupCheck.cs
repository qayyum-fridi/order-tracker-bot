using OrderTrackerBot.Application.Time;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

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
                MessageTemplates.Get("setup-check.001", Industry(seller).FullProduct), ct);
            return;
        }

        var noCost = products.Count(p => p.CostPrice is null);
        var noCategory = products.Count(p => string.IsNullOrWhiteSpace(p.Category));
        var noStock = products.Count(p => p.StockQty is null);
        var staleCutoff = SellerClock.DaysAgoUtc(StaleOrderDays, DateTime.UtcNow);
        var stale = await _db.Orders.AsNoTracking().CountAsync(o => o.SellerId == seller.Id && o.CreatedAt < staleCutoff
            && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.Shipped), ct);
        var orders = await _db.Orders.AsNoTracking().CountAsync(o => o.SellerId == seller.Id, ct);
        var monthStart = SellerClock.DaysAgoUtc(30, DateTime.UtcNow);
        var expenses = await _db.Expenses.AsNoTracking().CountAsync(e => e.SellerId == seller.Id && e.CreatedAt >= monthStart, ct);

        var rows = new List<(bool Ok, string Line)>
        {
            (noCost == 0, noCost == 0 ? MessageTemplates.Get("setup-check.006") : MessageTemplates.Get("setup-check.007", noCost, products.Count, Industry(seller).FullProduct)),
            (noCategory == 0, noCategory == 0 ? MessageTemplates.Get("setup-check.008") : MessageTemplates.Get("setup-check.009", noCategory, products.Count, Industry(seller).FullProduct)),
            (noStock == 0, noStock == 0 ? MessageTemplates.Get("setup-check.010") : MessageTemplates.Get("setup-check.011", noStock, products.Count, Industry(seller).SimpleProductName)),
            (stale == 0, stale == 0 ? MessageTemplates.Get("setup-check.013") : MessageTemplates.Get("setup-check.012", stale, StaleOrderDays)),
            (orders == 0 || expenses > 0, orders == 0 || expenses > 0 ? MessageTemplates.Get("setup-check.014") : MessageTemplates.Get("setup-check.015")),
        };
        if (string.IsNullOrWhiteSpace(seller.BusinessType))
            rows.Insert(0, (false, MessageTemplates.Get("setup-check.016")));
        var done = rows.Count(r => r.Ok);
        var lines = string.Join("\n", rows.Select(r => r.Line));
        var quality = await DataQualityIssuesAsync(seller, products, ct);
        var qualityText = quality.Count == 0 ? "" : "\n\n" + MessageTemplates.Get("setup-check.003") + "\n" + string.Join("\n", quality);
        var allGood = done == rows.Count && quality.Count == 0;
        var head = allGood ? MessageTemplates.Get("setup-check.004") : MessageTemplates.Get("setup-check.005", done, rows.Count, quality.Count);
        await ReplyAsync(seller, $"{head}\n\n{lines}{qualityText}", ct);
        SuggestNext(noCost > 0 ? "catalog" : "margin", noStock > 0 ? "stock" : "audit");
    }

    private static string Names(IEnumerable<string> names, int total)
    {
        var shown = names.Take(3).ToList();
        return string.Join(", ", shown) + (total > shown.Count ? $" …+{total - shown.Count}" : "");
    }

    private static string Last10(string? phone)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    /// <summary>Entries that exist but look wrong (not just absent): the numbers the reports would then calculate on are untrustworthy. Counts and a few names, never changed automatically.</summary>
    private async Task<List<string>> DataQualityIssuesAsync(Seller seller, List<Product> products, CancellationToken ct)
    {
        var issues = new List<string>();

        var loss = products.Where(p => p.CostPrice is { } c && p.Price > 0 && c >= p.Price).ToList();
        if (loss.Count > 0)
            issues.Add(MessageTemplates.Get("setup-check.017", loss.Count, Names(loss.Select(p => $"{p.Name} (cost {p.CostPrice:0.##} / price {p.Price:0.##})"), loss.Count)));

        var free = products.Where(p => p.Price <= 0).ToList();
        if (free.Count > 0)
            issues.Add(MessageTemplates.Get("setup-check.018", free.Count, Names(free.Select(p => p.Name), free.Count)));

        var negative = products.Where(p => p.StockQty < 0).ToList();
        if (negative.Count > 0)
            issues.Add(MessageTemplates.Get("setup-check.019", negative.Count, Names(negative.Select(p => $"{p.Name} ({p.StockQty})"), negative.Count), Industry(seller).SimpleProductName));

        static string Key(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var dupes = new List<string>();
        for (var i = 0; i < products.Count; i++)
            for (var j = i + 1; j < products.Count; j++)
            {
                var n1 = products[i].Name.Trim(); var n2 = products[j].Name.Trim();
                if (string.Equals(n1, n2, StringComparison.Ordinal)) continue; // same name, different pack/unit: a legitimate variant
                var a = Key(n1); var b = Key(n2);
                if (a.Length < 5) continue;
                var t1 = n1.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var t2 = n2.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // "Kurti S" / "Kurti M" differ in one short token: sizes and colours, not a typo.
                var sizeVariant = t1.Length == t2.Length && t1.Zip(t2).Count(x => x.First != x.Second) == 1
                    && t1.Zip(t2).First(x => x.First != x.Second) is var d && Math.Max(d.First.Length, d.Second.Length) <= 2;
                if (!sizeVariant && (a == b || TextDistance.Closest(a, new[] { b }, 1) is not null))
                    dupes.Add($"{n1} / {n2}");
            }
        if (dupes.Count > 0)
            issues.Add(MessageTemplates.Get("setup-check.020", dupes.Count, Names(dupes, dupes.Count)));

        var customers = await _db.Customers.AsNoTracking()
            .Where(c => c.SellerId == seller.Id && c.DeletedAt == null && c.Name != "Walk-in").ToListAsync(ct);
        var withOrders = (await _db.Orders.AsNoTracking().Where(o => o.SellerId == seller.Id).Select(o => o.CustomerId).Distinct().ToListAsync(ct)).ToHashSet();
        var active = customers.Where(c => withOrders.Contains(c.Id)).ToList();

        var badPhone = active.Where(c => !IsPlausiblePhone(c.Phone)).ToList();
        if (badPhone.Count > 0)
            issues.Add(MessageTemplates.Get("setup-check.021", badPhone.Count, Names(badPhone.Select(c => c.Name), badPhone.Count)));

        var sharedPhone = active.Where(c => IsPlausiblePhone(c.Phone)).GroupBy(c => Last10(c.Phone)).Where(g => g.Count() > 1).ToList();
        if (sharedPhone.Count > 0)
            issues.Add(MessageTemplates.Get("setup-check.022", sharedPhone.Count, Names(sharedPhone.Select(g => string.Join(" = ", g.Select(c => c.Name))), sharedPhone.Count)));

        var noCity = active.Where(c => string.IsNullOrWhiteSpace(c.City) && string.IsNullOrWhiteSpace(c.Address)).ToList();
        if (noCity.Count > 0)
            issues.Add(MessageTemplates.Get("setup-check.023", noCity.Count, Names(noCity.Select(c => c.Name), noCity.Count)));

        var since = SellerClock.DaysAgoUtc(90, DateTime.UtcNow);
        var noCourier = await _db.Orders.AsNoTracking().CountAsync(o => o.SellerId == seller.Id && o.CreatedAt >= since
            && (o.Status == OrderStatus.Shipped || o.Status == OrderStatus.Delivered) && o.OrderSource != "offline" && string.IsNullOrEmpty(o.TrackingCourier), ct);
        if (noCourier > 0)
            issues.Add(MessageTemplates.Get("setup-check.024", noCourier));

        return issues;
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
            MessageTemplates.Get("setup-check.002", noCost, Industry(seller).FullProduct), ct);
    }
}
