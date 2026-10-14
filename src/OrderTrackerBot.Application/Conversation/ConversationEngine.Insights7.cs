using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Strategic questions, part A: when and how buyers pay, what holding stock costs, dead-stock liquidity, and what each supplier is worth.
// Plain counting and arithmetic on saved data; anything that would need a forecast of buyer behaviour is given as a worked example and labelled so.
public partial class ConversationEngine
{
    private async Task<bool> TryHandleStrategicInsightAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, string? period, string text, CancellationToken ct)
    {
        switch (cmd.Text)
        {
            case "salarycycle": await InsightSalaryCycleAsync(seller, period, ct); break;
            case "promocohort": await InsightPromoCohortAsync(seller, period, ct); break;
            case "peaktime": await InsightPeakTimeAsync(seller, period, ct); break;
            case "paypattern": await InsightPaymentHabitAsync(seller, ct); break;
            case "holdingcost": await InsightHoldingCostAsync(seller, (int)(cmd.Amount ?? 60), ct); break;
            case "turnoverstar": await InsightTurnoverStarAsync(seller, ct); break;
            case "liquidate": await InsightLiquidateAsync(seller, cmd.Amount ?? 10, text, ct); break;
            case "suppliercompare": await InsightSupplierCompareAsync(seller, ctx.SupplierLeadDays, text, ct); break;
            case "supplierdefects": await InsightSupplierDefectsAsync(seller, (int)(cmd.Amount ?? 180), ct); break;
            case "suppliershock": await InsightSupplierShockAsync(seller, cmd.Amount ?? 10, text, ct); break;
            default: return await TryHandleStrategicInsight2Async(seller, cmd, period, text, ct);
        }
        return true;
    }

    /// <summary>Monthly rent / electricity / bills (expense categories or notes that look like them, else all expenses), averaged over the last 90 days.</summary>
    private async Task<(decimal Monthly, bool Specific)> FixedMonthlyCostsAsync(Seller seller, CancellationToken ct)
    {
        var start = DateTime.UtcNow.AddDays(-90);
        var expenses = await _db.Expenses.AsNoTracking().Where(e => e.SellerId == seller.Id && e.CreatedAt >= start).ToListAsync(ct);
        var fixedOnes = expenses.Where(e => Regex.IsMatch(e.Category + " " + e.Note, @"kiraya|rent|bijli|electric|bill|utilit|کرایہ|بجلی", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        var basis = fixedOnes.Count > 0 ? fixedOnes : expenses;
        return (basis.Sum(e => e.Amount) / 3m, fixedOnes.Count > 0);
    }

    /// <summary>Product id -> supplier name, from the "supplier" attribute saved on each product.</summary>
    private async Task<Dictionary<int, string>> SupplierByProductAsync(Seller seller, CancellationToken ct) =>
        (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id).ToListAsync(ct))
            .Select(p => (p.Id, Supplier: SupplierOf(p))).Where(x => x.Supplier is not null).ToDictionary(x => x.Id, x => x.Supplier!);

    private static readonly string[] DayNames = { "Sunday (Itwar)", "Monday (Peer)", "Tuesday (Mangal)", "Wednesday (Budh)", "Thursday (Jumeraat)", "Friday (Juma)", "Saturday (Hafta)" };

    private static string HourBand(int hour) => hour switch
    {
        >= 6 and < 12 => "subah (6–12)",
        >= 12 and < 16 => "dopahar (12–4)",
        >= 16 and < 20 => "shaam (4–8)",
        >= 20 => "raat (8–12)",
        _ => "raat gaye (12–6)"
    };

    // ---------- when and how buyers pay

    /// <summary>The last days of the month (25th on) against the rest: order size, cash share, counter against online.</summary>
    private async Task InsightSalaryCycleAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var orders = await ActiveOrdersIn(seller, start, end).Select(o => new { o.CreatedAt, o.Total, o.PaymentMethod, o.OrderSource }).ToListAsync(ct);
        if (orders.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.001", label), ct);
            return;
        }

        var rows = orders.Select(o => (Late: SellerLocal(seller, o.CreatedAt).Day >= 25, Walk: string.Equals(o.OrderSource, "offline", StringComparison.OrdinalIgnoreCase), o.Total, Cod: o.PaymentMethod == OrderPaymentMethod.Cod)).ToList();
        string Part(string name, List<(bool Late, bool Walk, decimal Total, bool Cod)> g) =>
            g.Count == 0 ? $"• {name}: koi order nahi" : $"• {name}: {g.Count} orders · har order ki average {Formatters.Money(Math.Round(g.Average(x => x.Total)))} · cash {Pct((decimal)g.Count(x => x.Cod) / g.Count * 100m)}";
        await ReplyAsync(seller,
            MessageTemplates.Get("insights7.002", label, Part("Dukaan par (walk-in)", rows.Where(r => r.Late && r.Walk).ToList()), Part("Online / WhatsApp", rows.Where(r => r.Late && !r.Walk).ToList()), Part("Dukaan par (walk-in)", rows.Where(r => !r.Late && r.Walk).ToList()), Part("Online / WhatsApp", rows.Where(r => !r.Late && !r.Walk).ToList())), ct);
        SuggestNext("payment split", "average sale");
    }

    /// <summary>Customers whose first order used a discount code against those who paid full price: what each group spent since.</summary>
    private async Task InsightPromoCohortAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "365d");
        var all = (await EconomicsAsync(seller, DateTime.MinValue, DateTime.MaxValue, ct)).Where(IsActive).ToList();
        var now = DateTime.UtcNow;
        var cohort = all.GroupBy(o => o.CustomerId).Select(g => g.OrderBy(o => o.At).ToList()).Where(l => l[0].At >= start && l[0].At < end).ToList();
        if (cohort.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.003", label), ct);
            return;
        }

        (int Customers, decimal AvgSpend, decimal AvgOrders, decimal AvgMonths) Summ(List<List<OrderEconomics>> g) =>
            g.Count == 0 ? (0, 0, 0, 0) : (g.Count, g.Average(l => l.Sum(o => o.Total)), (decimal)g.Average(l => l.Count), (decimal)g.Average(l => Math.Max(1, (now - l[0].At).TotalDays / 30)));
        var promo = Summ(cohort.Where(l => l[0].DiscountCode != null).ToList());
        var full = Summ(cohort.Where(l => l[0].DiscountCode == null).ToList());
        if (promo.Customers == 0 || full.Customers == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.004", label, promo.Customers, full.Customers), ct);
            return;
        }

        var diff = full.AvgSpend == 0 ? 0 : (promo.AvgSpend - full.AvgSpend) / full.AvgSpend * 100m;
        await ReplyAsync(seller,
            MessageTemplates.Get("insights7.005", label, promo.Customers, Formatters.Money(Math.Round(promo.AvgSpend)), Num(promo.AvgOrders), full.Customers, Formatters.Money(Math.Round(full.AvgSpend)), Num(full.AvgOrders), (diff >= 0 ? "zyada" : "kam"), Pct(Math.Abs(diff)), Num(promo.AvgMonths), Num(full.AvgMonths)), ct);
        SuggestNext("discount report", "repeat customers");
    }

    /// <summary>The busiest weekdays and parts of the day by orders written down.</summary>
    private async Task InsightPeakTimeAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var orders = (await ActiveOrdersIn(seller, start, end).Select(o => o.CreatedAt).ToListAsync(ct)).Select(d => SellerLocal(seller, d)).ToList();
        if (orders.Count < 5)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.006", label, orders.Count), ct);
            return;
        }

        var byDay = orders.GroupBy(d => d.DayOfWeek).Select(g => (Day: DayNames[(int)g.Key], Count: g.Count())).OrderByDescending(x => x.Count).ToList();
        var byBand = orders.GroupBy(d => HourBand(d.Hour)).Select(g => (Band: g.Key, Count: g.Count())).OrderByDescending(x => x.Count).ToList();
        var combo = orders.GroupBy(d => (DayNames[(int)d.DayOfWeek], HourBand(d.Hour))).Select(g => (Day: g.Key.Item1, Band: g.Key.Item2, Count: g.Count())).OrderByDescending(x => x.Count).Take(3).ToList();
        await ReplyAsync(seller,
            MessageTemplates.Get("insights7.007", label, orders.Count, byDay[0].Day, byDay[0].Count, (byDay.Count > 1 ? $", phir {byDay[1].Day} ({byDay[1].Count})" : ""), byBand[0].Band, byBand[0].Count, (byBand.Count > 1 ? $", phir {byBand[1].Band} ({byBand[1].Count})" : ""), string.Join("; ", combo.Select(c => $"{c.Day} {c.Band} ({c.Count})"))), ct);
        SuggestNext("average sale", "orders today");
    }

    /// <summary>Customers who pay by wallet/bank for the dear orders but ask for COD on the cheap ones.</summary>
    private async Task InsightPaymentHabitAsync(Seller seller, CancellationToken ct)
    {
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= DateTime.UtcNow.AddDays(-365) && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.Customer!.DeletedAt == null)
            .Select(o => new { o.CustomerId, o.Customer!.Name, o.Total, o.PaymentMethod })
            .ToListAsync(ct);
        if (rows.Count < 6)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.008"), ct);
            return;
        }

        var sorted = rows.Select(r => r.Total).OrderBy(v => v).ToList();
        var median = sorted[sorted.Count / 2];
        var flagged = rows.GroupBy(r => new { r.CustomerId, r.Name })
            .Select(g =>
            {
                var dear = g.Where(r => r.Total >= median).ToList();
                var cheap = g.Where(r => r.Total < median).ToList();
                return (Name: g.Key.Name, Orders: g.Count(), Dear: dear.Count, Cheap: cheap.Count,
                    DearAdvance: dear.Count == 0 ? 0m : (decimal)dear.Count(r => r.PaymentMethod != OrderPaymentMethod.Cod) / dear.Count,
                    CheapCod: cheap.Count == 0 ? 0m : (decimal)cheap.Count(r => r.PaymentMethod == OrderPaymentMethod.Cod) / cheap.Count);
            })
            .Where(c => c.Orders >= 3 && c.Dear >= 2 && c.Cheap >= 2 && c.DearAdvance >= 0.67m && c.CheapCod >= 0.67m)
            .OrderByDescending(c => c.Orders).Take(8).ToList();
        if (flagged.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.009", Formatters.Money(median)), ct);
            return;
        }
        var lines = flagged.Select((c, i) => $"{i + 1}. {c.Name} — bade {c.Dear} orders mein {Pct(c.DearAdvance * 100m)} advance/wallet; chhote {c.Cheap} mein {Pct(c.CheapCod * 100m)} COD");
        await ReplyAsync(seller, MessageTemplates.Get("insights7.010", Formatters.Money(median), string.Join("\n", lines)), ct);
        SuggestNext("payment split", "customers");
    }

    // ---------- stock capital

    /// <summary>What sitting idle stock costs: a share of the monthly rent and bills (by stock value) over the days it has not sold, as a drop in margin.</summary>
    private async Task InsightHoldingCostAsync(Seller seller, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 14, 365);
        var (monthly, specific) = await FixedMonthlyCostsAsync(seller, ct);
        if (monthly <= 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.011"), ct);
            return;
        }

        var now = DateTime.UtcNow;
        var tracked = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive && p.StockQty > 0).ToListAsync(ct);
        if (tracked.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights4.020"), ct);
            return;
        }
        var ids = tracked.Select(p => p.Id).ToList();
        var sold = (await _db.OrderItems.AsNoTracking()
            .Where(i => i.ProductId != null && ids.Contains(i.ProductId.Value) && i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= now.AddDays(-days) && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => i.ProductId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        decimal ValueOf(Product p) => p.StockQty!.Value * (p.CostPrice ?? p.Price);
        var totalValue = tracked.Sum(ValueOf);
        var idle = tracked.Where(p => p.CreatedAt < now.AddDays(-days) && !sold.Contains(p.Id)).OrderByDescending(ValueOf).Take(5).ToList();
        if (idle.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.012", days), ct);
            return;
        }

        var lines = idle.Select((p, i) =>
        {
            var holding = monthly * days / 30m * ValueOf(p) / totalValue;
            var perUnit = holding / p.StockQty!.Value;
            var drop = p.Price > 0 ? perUnit / p.Price * 100m : 0m;
            var marginText = p.CostPrice is { } c && p.Price > 0 ? $"; margin {Pct((p.Price - c) / p.Price * 100m)} → {Pct((p.Price - c - perUnit) / p.Price * 100m)}" : "";
            return $"{i + 1}. {Formatters.ProductLabel(p)} — {p.StockQty} piece, holding cost {Formatters.Money(Math.Round(holding))} ({Formatters.Money(Math.Round(perUnit))} fi piece = qeemat ka {Pct(drop)}){marginText}";
        });
        await ReplyAsync(seller,
            MessageTemplates.Get("insights7.013", days, string.Join("\n", lines), (specific ? "kiraya/bijli/bill" : "kul kharche"), Formatters.Money(Math.Round(monthly)), days), ct);
        SuggestNext("overstocked products", "locked capital");
    }

    /// <summary>Products with a below-median margin that still earned the most total profit in the last 30 days.</summary>
    private async Task InsightTurnoverStarAsync(Seller seller, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-30);
        var items = await _db.OrderItems.AsNoTracking()
            .Where(i => i.ProductId != null && i.UnitCost != null && i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= since && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { ProductId = i.ProductId!.Value, i.UnitPrice, Cost = i.UnitCost!.Value, i.Quantity })
            .ToListAsync(ct);
        var perProduct = items.GroupBy(i => i.ProductId)
            .Select(g => (Id: g.Key, Units: g.Sum(i => i.Quantity), Sales: g.Sum(i => i.UnitPrice * i.Quantity), Profit: g.Sum(i => (i.UnitPrice - i.Cost) * i.Quantity)))
            .Where(x => x.Sales > 0).Select(x => (x.Id, x.Units, x.Sales, x.Profit, Margin: x.Profit / x.Sales * 100m)).ToList();
        if (perProduct.Count < 3)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.014"), ct);
            return;
        }

        var margins = perProduct.Select(x => x.Margin).OrderBy(v => v).ToList();
        var median = margins[margins.Count / 2];
        var stars = perProduct.Where(x => x.Margin < median).OrderByDescending(x => x.Profit).Take(3).ToList();
        if (stars.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.015"), ct);
            return;
        }
        var ids = stars.Select(s => s.Id).ToList();
        var names = (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && ids.Contains(p.Id)).ToListAsync(ct)).ToDictionary(p => p.Id, p => Formatters.ProductLabel(p));
        var rank = perProduct.OrderByDescending(x => x.Profit).Select((x, i) => (x.Id, Rank: i + 1)).ToDictionary(x => x.Id, x => x.Rank);
        var lines = stars.Select((s, i) => $"{i + 1}. {names[s.Id]} — margin {Pct(s.Margin)} (beech wala {Pct(median)}), roz ~{Num(s.Units / 30m)} bikta hai, 30 din ka munafa {Formatters.Money(s.Profit)} (munafe mein number {rank[s.Id]} / {perProduct.Count})");
        await ReplyAsync(seller, MessageTemplates.Get("insights7.016", string.Join("\n", lines)), ct);
        SuggestNext("margin", "trending products");
    }

    /// <summary>Dead stock sold some percent below cost: the cash it releases and the loss against its cost.</summary>
    private async Task InsightLiquidateAsync(Seller seller, decimal below, string text, CancellationToken ct)
    {
        below = Math.Clamp(below, 1, 90);
        var days = Regex.Match(text, @"(\d{2,3})\s*(?:days?|din|دن)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var d) ? Math.Clamp(d, 14, 365) : 90;
        var now = DateTime.UtcNow;
        var tracked = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive && p.StockQty > 0 && p.CreatedAt < now.AddDays(-days)).ToListAsync(ct);
        var ids = tracked.Select(p => p.Id).ToList();
        var sold = ids.Count == 0 ? new HashSet<int>() : (await _db.OrderItems.AsNoTracking()
            .Where(i => i.ProductId != null && ids.Contains(i.ProductId.Value) && i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= now.AddDays(-days) && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => i.ProductId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var dead = tracked.Where(p => !sold.Contains(p.Id)).ToList();
        if (dead.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.017", days), ct);
            return;
        }

        var costed = dead.Where(p => p.CostPrice != null).OrderByDescending(p => p.StockQty!.Value * p.CostPrice!.Value).ToList();
        var uncosted = dead.Count - costed.Count;
        if (costed.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.018", dead.Count, days), ct);
            return;
        }
        var atCost = costed.Sum(p => p.StockQty!.Value * p.CostPrice!.Value);
        var release = atCost * (1m - below / 100m);
        var lines = costed.Take(5).Select((p, i) => $"{i + 1}. {Formatters.ProductLabel(p)} — {p.StockQty} piece × {Formatters.Money(Math.Round(p.CostPrice!.Value * (1m - below / 100m)))} = {Formatters.Money(Math.Round(p.StockQty!.Value * p.CostPrice.Value * (1m - below / 100m)))}");
        await ReplyAsync(seller,
            MessageTemplates.Get("insights7.019", days, Pct(below), costed.Count, Formatters.Money(Math.Round(atCost)), Formatters.Money(Math.Round(release)), Formatters.Money(Math.Round(atCost - release)), string.Join("\n", lines), (uncosted > 0 ? $"\n\nℹ️ {uncosted} aur product(s) bhi nahi bike magar cost save nahi, wo is raqam mein nahi." : "")), ct);
        SuggestNext("overstocked products", "margin");
    }

    // ---------- suppliers

    /// <summary>Margin per supplier (saved "supplier:" attribute) for the suppliers named, or all; the supplier's delivery time is not kept per supplier.</summary>
    private async Task InsightSupplierCompareAsync(Seller seller, int? leadDays, string text, CancellationToken ct)
    {
        var map = await SupplierByProductAsync(seller, ct);
        if (map.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.020"), ct);
            return;
        }

        var since = DateTime.UtcNow.AddDays(-180);
        var products = (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id).ToListAsync(ct)).ToDictionary(p => p.Id);
        var items = await _db.OrderItems.AsNoTracking()
            .Where(i => i.ProductId != null && i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= since && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { ProductId = i.ProductId!.Value, i.UnitPrice, i.UnitCost, i.Quantity })
            .ToListAsync(ct);
        var named = map.Values.Distinct(StringComparer.OrdinalIgnoreCase).Where(n => HasWord(text, n)).ToList();
        var groups = items.Where(i => map.ContainsKey(i.ProductId)).GroupBy(i => map[i.ProductId], StringComparer.OrdinalIgnoreCase)
            .Where(g => named.Count == 0 || named.Contains(g.Key, StringComparer.OrdinalIgnoreCase))
            .Select(g =>
            {
                var known = g.Where(i => (i.UnitCost ?? products[i.ProductId].CostPrice) != null).ToList();
                var sales = known.Sum(i => i.UnitPrice * i.Quantity);
                var profit = known.Sum(i => (i.UnitPrice - (i.UnitCost ?? products[i.ProductId].CostPrice)!.Value) * i.Quantity);
                return (Name: g.Key, Units: g.Sum(i => i.Quantity), Sales: sales, Profit: profit, Margin: sales > 0 ? profit / sales * 100m : (decimal?)null);
            })
            .OrderByDescending(g => g.Margin ?? -1m).ToList();
        if (groups.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.021"), ct);
            return;
        }
        var lines = groups.Select((g, i) => $"{i + 1}. {g.Name} — {g.Units} piece bike · " + (g.Margin is { } m ? $"margin {Pct(m)} (munafa {Formatters.Money(g.Profit)} / sale {Formatters.Money(g.Sales)})" : "margin maloom nahi (cost save nahi)"));
        await ReplyAsync(seller,
            MessageTemplates.Get("insights7.022", string.Join("\n", lines), (leadDays is { } d ? $"Maal aane ka waqt: aap ne sab suppliers ke liye {d} din bataya hai — har supplier ka alag waqt bot save nahi karta."
                : "Maal aane ka waqt (lead time) har supplier ka alag save nahi hota; sab ke liye ek waqt batane ke liye: \"maal aane mein 5 din lagte hain\".")), ct);
        SuggestNext("suppliers", "margin");
    }

    /// <summary>Per supplier: units returned or written off against units sold (6 months).</summary>
    private async Task InsightSupplierDefectsAsync(Seller seller, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 30, 730);
        var map = await SupplierByProductAsync(seller, ct);
        if (map.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.020"), ct);
            return;
        }

        var since = DateTime.UtcNow.AddDays(-days);
        var items = await _db.OrderItems.AsNoTracking()
            .Where(i => i.ProductId != null && i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= since)
            .Select(i => new { ProductId = i.ProductId!.Value, i.Quantity, i.Order!.Status })
            .ToListAsync(ct);
        var losses = await _db.Losses.AsNoTracking().Where(l => l.SellerId == seller.Id && l.CreatedAt >= since && l.ProductId != null).Select(l => new { ProductId = l.ProductId!.Value, l.Quantity }).ToListAsync(ct);
        var rows = map.Values.Distinct(StringComparer.OrdinalIgnoreCase).Select(name =>
        {
            var ids = map.Where(kv => kv.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToHashSet();
            var sold = items.Where(i => ids.Contains(i.ProductId) && i.Status != OrderStatus.Cancelled && i.Status != OrderStatus.Returned).Sum(i => i.Quantity);
            var back = items.Where(i => ids.Contains(i.ProductId) && i.Status == OrderStatus.Returned).Sum(i => i.Quantity);
            var bad = losses.Where(l => ids.Contains(l.ProductId)).Sum(l => l.Quantity);
            return (Name: name, Sold: sold, Returned: back, Lost: bad, Rate: sold + back == 0 ? 0m : (decimal)(back + bad) / (sold + back) * 100m);
        }).Where(r => r.Sold + r.Returned > 0).OrderByDescending(r => r.Rate).ToList();
        if (rows.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.023", days), ct);
            return;
        }
        var lines = rows.Select((r, i) => $"{i + 1}. {r.Name} — {Pct(r.Rate)} ({r.Returned} wapas + {r.Lost} kharab/gum, {r.Sold + r.Returned} piece mein se)");
        await ReplyAsync(seller,
            MessageTemplates.Get("insights7.024", days, string.Join("\n", lines)), ct);
        SuggestNext("suppliers", "return rate");
    }

    /// <summary>Wholesale rate up N%: the price rise that keeps the margin percent, and the one that keeps the profit per piece.</summary>
    private async Task InsightSupplierShockAsync(Seller seller, decimal rise, string text, CancellationToken ct)
    {
        rise = Math.Clamp(rise, 1, 200);
        var map = await SupplierByProductAsync(seller, ct);
        var named = map.Values.Distinct(StringComparer.OrdinalIgnoreCase).Where(n => HasWord(text, n)).ToList();
        var products = (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive && p.CostPrice != null && p.Price > 0).ToListAsync(ct))
            .Where(p => named.Count == 0 || (map.TryGetValue(p.Id, out var s) && named.Contains(s, StringComparer.OrdinalIgnoreCase))).ToList();
        if (products.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights7.025"), ct);
            return;
        }

        var scope = named.Count > 0 ? string.Join(", ", named) : "sab products";
        var lines = products.OrderByDescending(p => p.Price).Take(5).Select((p, i) =>
        {
            var newCost = p.CostPrice!.Value * (1m + rise / 100m);
            var sameMargin = Math.Ceiling(p.Price * (1m + rise / 100m) / 10m) * 10m;
            var sameProfit = Math.Ceiling((p.Price + p.CostPrice.Value * rise / 100m) / 10m) * 10m;
            return $"{i + 1}. {Formatters.ProductLabel(p)} — cost {Formatters.Money(p.CostPrice.Value)} → {Formatters.Money(Math.Round(newCost))}; margin {Pct((p.Price - p.CostPrice.Value) / p.Price * 100m)} rakhne ke liye price {Formatters.Money(sameMargin)} (+{Pct(rise)}), fi piece munafa wohi rakhne ke liye {Formatters.Money(sameProfit)} (+{Pct((sameProfit - p.Price) / p.Price * 100m)})";
        });
        var avgSameProfit = products.Average(p => p.CostPrice!.Value * rise / 100m / p.Price * 100m);
        await ReplyAsync(seller,
            MessageTemplates.Get("insights7.026", Pct(rise), scope, string.Join("\n", lines), Pct(rise), Pct(avgSameProfit), products.Count), ct);
        SuggestNext("price review", "margin");
    }
}
