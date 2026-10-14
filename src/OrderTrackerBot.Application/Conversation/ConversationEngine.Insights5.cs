using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Hyper-complex business questions, part A: what a customer is really worth (profit after discounts and returns), retention, the VIP share of sales,
// and what discount codes did. Counting and arithmetic on saved orders; where the honest answer needs a "what if buyers had..." the reply says it is an estimate.
public partial class ConversationEngine
{
    private async Task<bool> TryHandleHyperInsightAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, string? period, string text, CancellationToken ct)
    {
        switch (cmd.Text)
        {
            case "custprofit": await InsightCustomerContributionAsync(seller, ctx, text, period, ct); break;
            case "retention": await InsightRetentionAsync(seller, period, ct); break;
            case "vipshare": await InsightVipShareAsync(seller, period, ct); break;
            case "promoroi": await InsightPromoReturnAsync(seller, text, period, ct); break;
            case "promonew": await InsightPromoNewCustomersAsync(seller, period, ct); break;
            case "promoloyalty": await InsightPromoLoyaltyAsync(seller, ct); break;
            default: return await TryHandleHyperInsight2Async(seller, ctx, cmd, period, text, ct);
        }
        return true;
    }

    private static string PercentIn(string text, string fallback) =>
        Regex.Match(text, @"(?<n>\d{1,3}(?:\.\d+)?)\s*(?:%|percent|fisad|فیصد)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) is { Success: true } m ? m.Groups["n"].Value : fallback;

    /// <summary>One saved order with the money that matters for profit; <c>Cost</c> is null when any item has no known cost (the order is then left out of profit, never guessed).</summary>
    private sealed record OrderEconomics(int OrderId, int CustomerId, string CustomerName, DateTime At, OrderStatus Status, decimal Total, decimal Revenue, decimal? Cost, decimal Discount, decimal Delivery, string? DiscountCode);

    private async Task<List<OrderEconomics>> EconomicsAsync(Seller seller, DateTime start, DateTime end, CancellationToken ct)
    {
        var orders = await _db.Orders.AsNoTracking().Include(o => o.Items).Include(o => o.Customer)
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end && o.Customer != null && o.Customer.DeletedAt == null)
            .ToListAsync(ct);
        var ids = orders.SelectMany(o => o.Items).Where(i => i.UnitCost is null && i.ProductId is not null).Select(i => i.ProductId!.Value).Distinct().ToList();
        var current = ids.Count == 0 ? new Dictionary<int, decimal?>()
            : (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && ids.Contains(p.Id)).Select(p => new { p.Id, p.CostPrice }).ToListAsync(ct)).ToDictionary(p => p.Id, p => p.CostPrice);
        decimal? CostOf(OrderItem i) => i.UnitCost ?? (i.ProductId is { } id && current.TryGetValue(id, out var c) ? c : null);

        return orders.Select(o =>
        {
            var costs = o.Items.Select(i => (Item: i, Cost: CostOf(i))).ToList();
            decimal? cost = costs.Count == 0 || costs.Any(c => c.Cost is null) ? null : costs.Sum(c => c.Cost!.Value * c.Item.Quantity);
            return new OrderEconomics(o.Id, o.CustomerId, o.Customer!.Name, o.CreatedAt, o.Status, o.Total, Math.Max(0, o.Subtotal - o.DiscountAmount), cost, o.DiscountAmount, o.DeliveryCharge, o.DiscountCode);
        }).ToList();
    }

    private static bool IsActive(OrderEconomics o) => o.Status is not (OrderStatus.Cancelled or OrderStatus.Returned);

    /// <summary>Net contribution of a customer = product profit (after discount) minus the delivery charges lost on parcels that came back. One customer named = their cumulative figure; none = the big spenders who leave the least.</summary>
    private async Task InsightCustomerContributionAsync(Seller seller, SessionContextData ctx, string text, string? period, CancellationToken ct)
    {
        var named = await CustomersInTextAsync(seller, text, ct);
        var (start, end, label) = InsightWindow(seller, period, named.Count > 0 ? "365d" : "180d");
        var all = await EconomicsAsync(seller, start, end, ct);
        if (all.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights5.001", label), ct);
            return;
        }

        (string Name, int Id, int Orders, decimal Sales, decimal Profit, int Uncosted, decimal Discount, int Returns, decimal ReturnValue, decimal ReturnDelivery) Summarise(IGrouping<int, OrderEconomics> g)
        {
            var active = g.Where(IsActive).ToList();
            var costed = active.Where(o => o.Cost is not null).ToList();
            var returned = g.Where(o => o.Status == OrderStatus.Returned).ToList();
            return (g.First().CustomerName, g.Key, active.Count, active.Sum(o => o.Total), costed.Sum(o => o.Revenue - o.Cost!.Value), active.Count - costed.Count,
                active.Sum(o => o.Discount), returned.Count, returned.Sum(o => o.Total), returned.Sum(o => o.Delivery));
        }

        var rows = all.GroupBy(o => o.CustomerId).Select(Summarise).ToList();
        if (named.Count > 0)
        {
            var wanted = named.Select(c => c.Id).ToHashSet();
            var mine = rows.Where(r => wanted.Contains(r.Id)).Take(3).ToList();
            if (mine.Count == 0)
            {
                await ReplyAsync(seller, MessageTemplates.Get("insights5.002", named[0].Name, label), ct);
                return;
            }
            var blocks = mine.Select(r =>
                $"👤 {r.Name} — {label}\n" +
                $"Orders: {r.Orders} · sale {Formatters.Money(r.Sales)}\n" +
                (r.Orders == r.Uncosted ? "Munafa: maloom nahi (products ki cost save nahi)\n" : $"Munafa (discount ke baad): {Formatters.Money(r.Profit)}" + (r.Uncosted > 0 ? $" ({r.Uncosted} order(s) cost ke baghair shamil nahi)" : "") + "\n") +
                $"Diya gaya discount: {Formatters.Money(r.Discount)}\n" +
                (r.Returns > 0 ? $"Return: {r.Returns} orders ({Formatters.Money(r.ReturnValue)}), un par delivery charges {Formatters.Money(r.ReturnDelivery)}\n" : "Return: nahi\n") +
                (r.Orders == r.Uncosted ? "" : $"Khalis fayda: {Formatters.Money(r.Profit - r.ReturnDelivery)}"));
            await ReplyAsync(seller, string.Join("\n\n", blocks), ct);
            SuggestNext("customers", "profit");
            return;
        }

        var costedRows = rows.Where(r => r.Orders > r.Uncosted).ToList();
        if (costedRows.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights5.003"), ct);
            return;
        }
        var spend = rows.Select(r => r.Sales).OrderBy(v => v).ToList();
        var median = spend[spend.Count / 2];
        var big = costedRows.Where(r => r.Sales >= median && r.Orders >= 2).OrderBy(r => r.Profit - r.ReturnDelivery).ThenByDescending(r => r.Sales).Take(5).ToList();
        if (big.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights5.004", label), ct);
            return;
        }
        ctx.LastListCustomerIds = big.Select(r => r.Id).ToList();
        var lines = big.Select((r, i) => $"{i + 1}. {r.Name} — sale {Formatters.Money(r.Sales)} · discount {Formatters.Money(r.Discount)} · return {r.Returns} · khalis fayda {Formatters.Money(r.Profit - r.ReturnDelivery)} ({(r.Sales > 0 ? Pct((r.Profit - r.ReturnDelivery) / r.Sales * 100m) : "—")} of sale)");
        await ReplyAsync(seller,
            MessageTemplates.Get("insights5.005", label, string.Join("\n", lines)), ct);
        SuggestNext("customer 1", "customers");
    }

    /// <summary>Of the customers whose first order fell in the period, how many ordered again.</summary>
    private async Task InsightRetentionAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.Customer!.DeletedAt == null)
            .Select(o => new { o.CustomerId, o.CreatedAt })
            .ToListAsync(ct);
        var customers = rows.GroupBy(r => r.CustomerId).Select(g => (First: g.Min(r => r.CreatedAt), Count: g.Count())).ToList();
        var cohort = customers.Where(c => c.First >= start && c.First < end).ToList();
        if (cohort.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights5.006", label), ct);
            return;
        }

        var again = cohort.Count(c => c.Count >= 2);
        var mature = cohort.Where(c => c.First <= DateTime.UtcNow.AddDays(-30)).ToList();
        await ReplyAsync(seller,
            MessageTemplates.Get("insights5.007", label, cohort.Count, again, Pct((decimal)again / cohort.Count * 100m), (mature.Count > 0 && mature.Count < cohort.Count ? $"Sirf jin ko 30+ din ho gaye ({mature.Count}): {mature.Count(c => c.Count >= 2)} ({Pct((decimal)mature.Count(c => c.Count >= 2) / mature.Count * 100m)})\n" : "")), ct);
        SuggestNext("repeat customers", "inactive customers");
    }

    /// <summary>The top 10% of customers by spend and their share of sales (plus how few customers make 80%).</summary>
    private async Task InsightVipShareAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "all");
        var ranked = await CustomerSpendAsync(seller, start, end, ct);
        if (ranked.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights5.008", label), ct);
            return;
        }
        var total = ranked.Sum(r => r.Spent);
        var topCount = Math.Max(1, (int)Math.Ceiling(ranked.Count * 0.1));
        var top = ranked.Take(topCount).ToList();
        var running = 0m;
        var toEighty = 0;
        foreach (var r in ranked)
        {
            running += r.Spent;
            toEighty++;
            if (running >= total * 0.8m) break;
        }
        await ReplyAsync(seller,
            MessageTemplates.Get("insights5.009", label, ranked.Count, Formatters.Money(total), topCount, Formatters.Money(top.Sum(r => r.Spent)), Pct(total > 0 ? top.Sum(r => r.Spent) / total * 100m : 0), toEighty, Pct((decimal)toEighty / ranked.Count * 100m), string.Join("\n", top.Take(5).Select((r, i) => $"{i + 1}. {r.Customer.Name} — {Formatters.Money(r.Spent)}"))), ct);
        SuggestNext("top customers", "loyal customers");
    }

    // ---------- discount codes

    private async Task<Discount?> CodeInTextAsync(Seller seller, string text, CancellationToken ct)
    {
        var codes = await _db.Discounts.AsNoTracking().Where(d => d.SellerId == seller.Id).ToListAsync(ct);
        return codes.Where(d => HasWord(text, d.Code)).OrderByDescending(d => d.Code.Length).FirstOrDefault();
    }

    /// <summary>What a code did: orders, discount given, profit, and how many of those orders came from customers who had bought in the previous 60 days (who may have bought anyway).</summary>
    private async Task InsightPromoReturnAsync(Seller seller, string text, string? period, CancellationToken ct)
    {
        var code = await CodeInTextAsync(seller, text, ct);
        var (start, end, label) = InsightWindow(seller, period, "90d");
        var all = await EconomicsAsync(seller, DateTime.MinValue, DateTime.MaxValue, ct);
        var usedCode = code?.Code ?? all.Where(o => o.DiscountCode != null && IsActive(o) && o.At >= start && o.At < end).GroupBy(o => o.DiscountCode!.ToUpperInvariant()).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        if (usedCode is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights5.010"), ct);
            return;
        }

        bool WithCode(OrderEconomics o) => string.Equals(o.DiscountCode, usedCode, StringComparison.OrdinalIgnoreCase);
        var withCode = all.Where(o => WithCode(o) && IsActive(o) && o.At >= start && o.At < end).ToList();
        if (withCode.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights5.011", usedCode, label), ct);
            return;
        }

        var others = all.Where(o => !WithCode(o) && IsActive(o) && o.At >= start && o.At < end).ToList();
        var existing = withCode.Where(o => all.Any(p => p.CustomerId == o.CustomerId && IsActive(p) && p.At < o.At && p.At >= o.At.AddDays(-60))).ToList();
        var firstEver = withCode.Where(o => !all.Any(p => p.CustomerId == o.CustomerId && IsActive(p) && p.At < o.At)).ToList();
        var costed = withCode.Where(o => o.Cost is not null).ToList();
        string Avg(IEnumerable<OrderEconomics> list) => list.Any() ? Formatters.Money(Math.Round(list.Average(o => o.Total))) : "—";

        await ReplyAsync(seller,
            MessageTemplates.Get("insights5.012", usedCode, label, withCode.Count, Formatters.Money(withCode.Sum(o => o.Total)), Formatters.Money(withCode.Sum(o => o.Discount)), (costed.Count > 0 ? $"Munafa in orders par: {Formatters.Money(costed.Sum(o => o.Revenue - o.Cost!.Value))}" + (costed.Count < withCode.Count ? $" ({withCode.Count - costed.Count} order(s) cost ke baghair shamil nahi)" : "") + "\n" : "Munafa: maloom nahi (cost save nahi)\n"), Avg(withCode), Avg(others), firstEver.Count, existing.Count, Formatters.Money(existing.Sum(o => o.Discount))), ct);
        SuggestNext("discount report", "margin");
    }

    /// <summary>Which discount type (and code) brought the most customers whose very first order used it.</summary>
    private async Task InsightPromoNewCustomersAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var all = await EconomicsAsync(seller, DateTime.MinValue, DateTime.MaxValue, ct);
        var codes = (await _db.Discounts.AsNoTracking().Where(d => d.SellerId == seller.Id).ToListAsync(ct)).ToDictionary(d => d.Code.ToUpperInvariant(), d => d.Type);
        var firsts = all.Where(IsActive).GroupBy(o => o.CustomerId).Select(g => g.OrderBy(o => o.At).First())
            .Where(o => o.At >= start && o.At < end && o.DiscountCode != null).ToList();
        if (firsts.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights5.013", label), ct);
            return;
        }

        string Kind(string code) => codes.TryGetValue(code.ToUpperInvariant(), out var t) ? (t == DiscountType.Percent ? "Percent discount" : "Flat discount") : "Code (qisam maloom nahi)";
        var byKind = firsts.GroupBy(o => Kind(o.DiscountCode!)).Select(g => (Name: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToList();
        var byCode = firsts.GroupBy(o => o.DiscountCode!.ToUpperInvariant()).Select(g => (Name: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).Take(5).ToList();
        await ReplyAsync(seller,
            MessageTemplates.Get("insights5.014", label, string.Join("\n", byKind.Select(k => $"• {k.Name}: {k.Count} naye customers")), string.Join("\n", byCode.Select((c, i) => $"{i + 1}. {c.Name} ({Kind(c.Name)}) — {c.Count}"))), ct);
        SuggestNext("discount report", "discount list");
    }

    /// <summary>Customers whose first order used a code: how many later bought at full price.</summary>
    private async Task InsightPromoLoyaltyAsync(Seller seller, CancellationToken ct)
    {
        var all = (await EconomicsAsync(seller, DateTime.MinValue, DateTime.MaxValue, ct)).Where(IsActive).ToList();
        var starters = all.GroupBy(o => o.CustomerId).Select(g => g.OrderBy(o => o.At).ToList()).Where(l => l[0].DiscountCode != null).ToList();
        if (starters.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights5.015"), ct);
            return;
        }

        var later = starters.Where(l => l.Count > 1).ToList();
        var fullPrice = later.Count(l => l.Skip(1).Any(o => o.DiscountCode == null));
        var onlyOffers = later.Count - fullPrice;
        var never = starters.Count - later.Count;
        await ReplyAsync(seller,
            MessageTemplates.Get("insights5.016", starters.Count, fullPrice, Pct((decimal)fullPrice / starters.Count * 100m), onlyOffers, Pct((decimal)onlyOffers / starters.Count * 100m), never, Pct((decimal)never / starters.Count * 100m)), ct);
        SuggestNext("discount report", "repeat customers");
    }
}
