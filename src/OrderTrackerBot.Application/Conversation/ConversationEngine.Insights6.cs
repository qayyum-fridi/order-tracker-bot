using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Hyper-complex business questions, part B: stock velocity and days on hand, cost spikes and break-even, delivery failures, cash flow, product life cycle, price mix, and the business audit.
// The bot never sees what buyers ask on WhatsApp and cannot know how buyers will react to a price: replies give the arithmetic or the history and say which it is.
public partial class ConversationEngine
{
    private async Task<bool> TryHandleHyperInsight2Async(Seller seller, SessionContextData ctx, ParsedCommand cmd, string? period, string text, CancellationToken ct)
    {
        switch (cmd.Text)
        {
            case "velocitydrop": await InsightVelocityDropAsync(seller, (int)(cmd.Amount ?? 15), PercentIn(text, "50"), ct); break;
            case "fastmoving": await InsightFastMovingAsync(seller, ctx.SupplierLeadDays, ct); break;
            case "daysonhand": await InsightDaysOnHandAsync(seller, ct); break;
            case "outofstockhot": await InsightOutOfStockDemandAsync(seller, ct); break;
            case "margindrop": await InsightMarginDropAsync(seller, ct); break;
            case "costshock": await InsightCostShockAsync(seller, text, cmd.Amount ?? 10, ct); break;
            case "costdrop": await InsightCostDropAsync(seller, ct); break;
            case "failcity": await InsightFailedDeliveryCitiesAsync(seller, period, ct); break;
            case "rtostock": await InsightReturnedStockAsync(seller, period, ct); break;
            case "chargeeffect": await InsightDeliveryChargeAsync(seller, text, cmd.Amount ?? 0, ct); break;
            case "cashflow": await InsightCashFlowAsync(seller, period, ct); break;
            case "cashplan": await InsightCashPlanAsync(seller, cmd.Amount, ct); break;
            case "eidstock": await InsightEidStockAsync(seller, text, ct); break;
            case "breakevendaily": await InsightDailySaleForFixedCostsAsync(seller, ct); break;
            case "declining": await InsightDecliningProductsAsync(seller, ct); break;
            case "pricemix": await InsightPriceMixAsync(seller, ct); break;
            case "audit": await InsightAuditAsync(seller, ct); break;
            default: return false;
        }
        return true;
    }

    /// <summary>Categories the seller named: a category word in the sentence, or a common synonym ("kapron" / "کپڑے" -> cloth categories, "گروسری" -> grocery).</summary>
    private async Task<List<string>> CategoriesInTextAsync(Seller seller, string text, CancellationToken ct)
    {
        var categories = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.Category != null).Select(p => p.Category!).Distinct().ToListAsync(ct);
        var lower = text.ToLowerInvariant();
        bool Mentions(params string[] words) => words.Any(w => lower.Contains(w, StringComparison.Ordinal));
        return categories.Where(c => HasWord(text, c)
            || (Mentions("grocery", "garocery", "groc", "گروسری", "راشن", "rashan") && Mentions2(c, "groc", "rash", "گروس", "راشن"))
            || (Mentions("kapron", "kapre", "kapda", "cloth", "کپڑ") && Mentions2(c, "kapr", "kapd", "cloth", "کپڑ"))).ToList();
    }

    private static bool Mentions2(string category, params string[] stems) => stems.Any(s => category.Contains(s, StringComparison.OrdinalIgnoreCase));

    // ---------- stock velocity

    /// <summary>Products whose units sold in the last D days fell by the given percent against the D days before.</summary>
    private async Task InsightVelocityDropAsync(Seller seller, int days, string percentText, CancellationToken ct)
    {
        days = Math.Clamp(days, 7, 90);
        var pct = decimal.TryParse(percentText, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? Math.Clamp(p, 10, 95) : 50m;
        var now = DateTime.UtcNow;
        var rows = await _db.OrderItems.AsNoTracking()
            .Where(i => i.ProductId != null && i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= now.AddDays(-2 * days)
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { ProductId = i.ProductId!.Value, i.Quantity, i.Order!.CreatedAt })
            .ToListAsync(ct);
        var cut = now.AddDays(-days);
        var changes = rows.GroupBy(r => r.ProductId)
            .Select(g => (Id: g.Key, Before: g.Where(r => r.CreatedAt < cut).Sum(r => r.Quantity), Now: g.Where(r => r.CreatedAt >= cut).Sum(r => r.Quantity)))
            .Where(c => c.Before >= 4 && c.Now <= c.Before * (1m - pct / 100m))
            .OrderByDescending(c => c.Before - c.Now).Take(8).ToList();
        if (changes.Count == 0)
        {
            await ReplyAsync(seller, $"✅ Kisi product ki bikri pichle {days} din mein {Pct(pct)} ya zyada nahi giri (kam az kam 4 piece pehle bikne wale dekhe gaye).", ct);
            return;
        }

        var ids = changes.Select(c => c.Id).ToList();
        var products = (await _db.Products.AsNoTracking().Where(x => x.SellerId == seller.Id && ids.Contains(x.Id)).ToListAsync(ct)).ToDictionary(x => x.Id);
        var lines = changes.Select((c, i) =>
        {
            var pr = products[c.Id];
            var margin = pr.CostPrice is { } cost && pr.Price > 0 ? $" · margin {Pct((pr.Price - cost) / pr.Price * 100m)}" : "";
            return $"{i + 1}. {Formatters.ProductLabel(pr)} — {c.Before} → {c.Now} piece ({Pct((decimal)(c.Before - c.Now) / c.Before * 100m)} kam) · price {Formatters.Money(pr.Price)}{margin}" + (pr.StockQty is { } st ? $" · stock {st}" : "");
        });
        await ReplyAsync(seller,
            $"📉 Bikri ki raftaar gir gayi — pichle {days} din bamuqabla us se pehle ke {days} din\n\n{string.Join("\n", lines)}\n\n" +
            "Qeemat kam karne se pehle margin dekh lein (jitna margin hai us se zyada chhoot nuqsan hai): \"margin\".", ct);
        SuggestNext("margin", "slow movers");
    }

    /// <summary>Fastest sellers by units a day, with the days of stock left and a re-order quantity covering the supplier's time plus a week.</summary>
    private async Task InsightFastMovingAsync(Seller seller, int? leadDays, CancellationToken ct)
    {
        var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive).ToListAsync(ct);
        if (products.Count == 0)
        {
            await ReplyAsync(seller, "Aapke catalog mein abhi koi product nahi.", ct);
            return;
        }
        var fast = (await StockRowsAsync(seller, products, ct)).Where(r => r.Sold >= 5).OrderByDescending(r => r.PerDay).Take(5).ToList();
        if (fast.Count == 0)
        {
            await ReplyAsync(seller, "Pichle 30 din mein kisi product ke 5 piece bhi nahi bike, is liye tez bikne wala maal nahi nikal sakta.", ct);
            return;
        }

        var lead = leadDays ?? 7;
        var lines = fast.Select((r, i) =>
        {
            var need = (int)Math.Ceiling(r.PerDay * (lead + 7));
            var stock = r.Product.StockQty is { } q ? $"stock {q}" + (r.DaysLeft is { } d ? $" (~{Num(d)} din)" : "") : "stock track nahi";
            var order = r.Product.StockQty is { } have ? Math.Max(0, need - have) : need;
            return $"{i + 1}. {Formatters.ProductLabel(r.Product)} — roz ~{Num(r.PerDay)} bikta hai · {stock}\n   {lead + 7} din ke liye ~{need} piece chahiye" + (r.Product.StockQty is null ? "" : $" → mangwayein ~{order}");
        });
        await ReplyAsync(seller,
            $"🚀 Tez bikne wala maal (pichle 30 din)\n\n{string.Join("\n", lines)}\n\nMiqdar = roz ki raftaar × (supplier ke {lead} din + 1 hafta). " +
            (leadDays is null ? "Supplier ka asal waqt batane ke liye: \"maal aane mein 5 din lagte hain\". " : "") + "Yeh andaza hai; maal aane par kitne din mein bika — yeh bot save nahi karta.", ct);
        SuggestNext("stock", "reorder");
    }

    /// <summary>All tracked stock against the combined daily pace: how many days until it is gone, by units and by cost.</summary>
    private async Task InsightDaysOnHandAsync(Seller seller, CancellationToken ct)
    {
        var tracked = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive && p.StockQty > 0).ToListAsync(ct);
        if (tracked.Count == 0)
        {
            await ReplyAsync(seller, "Abhi kisi product ka stock track nahi ho raha ya sab khatam hai. Shuru karne ke liye: \"stock Kurti 20\".", ct);
            return;
        }

        var rows = await StockRowsAsync(seller, tracked, ct);
        var moving = rows.Where(r => r.PerDay > 0).ToList();
        var stuck = rows.Where(r => r.PerDay <= 0).ToList();
        var units = moving.Sum(r => (decimal)r.Product.StockQty!.Value);
        var pace = moving.Sum(r => r.PerDay);
        var valued = moving.Where(r => r.Product.CostPrice != null).ToList();
        var worth = valued.Sum(r => r.Product.StockQty!.Value * r.Product.CostPrice!.Value);
        var worthPace = valued.Sum(r => r.PerDay * r.Product.CostPrice!.Value);
        var slowest = moving.OrderByDescending(r => r.DaysLeft ?? 0).Take(3).ToList();
        var stuckCapital = stuck.Where(r => r.Product.CostPrice != null).Sum(r => r.Product.StockQty!.Value * r.Product.CostPrice!.Value);

        await ReplyAsync(seller,
            "📦 Stock kitne din chalega (Inventory days on hand)\n\n" +
            (pace > 0 ? $"Bikne wale {moving.Count} products: {units:0} piece, roz ~{Num(pace)} bikte hain → sara maal ~{Num(units / pace)} din mein khatam\n" : "Pichle 30 din mein kuch nahi bika.\n") +
            (worthPace > 0 ? $"Cost ke hisab se: {Formatters.Money(worth)} ka maal, roz ~{Formatters.Money(Math.Round(worthPace))} ki cost bikti hai → ~{Num(worth / worthPace)} din\n" : "") +
            (stuck.Count > 0 ? $"Jo pichle 30 din nahi bika: {stuck.Count} products" + (stuckCapital > 0 ? $" ({Formatters.Money(stuckCapital)} ki cost)" : "") + " — in ka din ginne mein hisaab nahi\n" : "") +
            (slowest.Count > 0 ? "\nSab se der se khatam hone wale:\n" + string.Join("\n", slowest.Select(r => $"• {Formatters.ProductLabel(r.Product)} — ~{Num(r.DaysLeft ?? 0)} din")) : "") +
            "\n\nYeh pichle 30 din ki raftaar ka andaza hai, pakki peshgoi nahi.", ct);
        SuggestNext("overstocked products", "stock");
    }

    /// <summary>Out-of-stock products that were selling: where sales were probably lost. (The bot does not see buyers' WhatsApp chats, so it cannot count who asked.)</summary>
    private async Task InsightOutOfStockDemandAsync(Seller seller, CancellationToken ct)
    {
        var out0 = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive && p.StockQty <= 0).ToListAsync(ct);
        if (out0.Count == 0)
        {
            await ReplyAsync(seller, "✅ Abhi kisi track hone wale product ka stock khatam nahi.", ct);
            return;
        }
        var hot = (await StockRowsAsync(seller, out0, ct)).Where(r => r.Sold > 0).OrderByDescending(r => r.Sold).Take(8).ToList();
        if (hot.Count == 0)
        {
            await ReplyAsync(seller, $"{out0.Count} products ka stock khatam hai magar pichle 30 din mein koi bikta nahi tha — demand ka koi nishan nahi.\n(Bot aap ke customers ki WhatsApp chat nahi dekhta, is liye \"kis ne poocha\" nahi ginta.)", ct);
            return;
        }
        var lines = hot.Select((r, i) => $"{i + 1}. {Formatters.ProductLabel(r.Product)} — stock khatam; pichle 30 din {r.Sold} bika (~{Formatters.Money(r.Sold * r.Product.Price)})");
        await ReplyAsync(seller,
            $"⛔ Stock khatam magar bikta tha — yahan bikri chhoot rahi hogi\n\n{string.Join("\n", lines)}\n\n" +
            "Bot aap ke customers ki WhatsApp chat nahi dekhta, is liye yeh \"kitne logon ne poocha\" nahi, balkay \"bikne wala maal jo ab nahi hai\" hai.", ct);
        SuggestNext("reorder", "stock");
    }

    // ---------- cost and margin

    private sealed record CostedSale(int ProductId, decimal UnitPrice, decimal Cost, int Quantity, DateTime CreatedAt);
    private sealed record PricedSale(decimal UnitPrice, int Quantity, string Name, DateTime CreatedAt);

    /// <summary>Per product: average margin of the last 30 days against the 30 before (each order's saved cost), biggest fall first.</summary>
    private async Task InsightMarginDropAsync(Seller seller, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var cut = now.AddDays(-30);
        var items = await _db.OrderItems.AsNoTracking()
            .Where(i => i.ProductId != null && i.UnitCost != null && i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= now.AddDays(-60)
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new CostedSale(i.ProductId!.Value, i.UnitPrice, i.UnitCost!.Value, i.Quantity, i.Order!.CreatedAt))
            .ToListAsync(ct);
        static (decimal Price, decimal Cost, decimal Margin)? Window(IEnumerable<CostedSale> rows)
        {
            var list = rows.ToList();
            var qty = list.Sum(r => (decimal)r.Quantity);
            if (qty == 0) return null;
            var price = list.Sum(r => r.UnitPrice * r.Quantity) / qty;
            var cost = list.Sum(r => r.Cost * r.Quantity) / qty;
            return price > 0 ? (price, cost, (price - cost) / price * 100m) : null;
        }

        var drops = items.GroupBy(i => i.ProductId).Select(g =>
            {
                var before = Window(g.Where(i => i.CreatedAt < cut));
                var after = Window(g.Where(i => i.CreatedAt >= cut));
                return (Id: g.Key, Before: before, After: after);
            })
            .Where(d => d.Before is not null && d.After is not null && d.Before.Value.Margin - d.After.Value.Margin >= 1m)
            .OrderByDescending(d => d.Before!.Value.Margin - d.After!.Value.Margin).Take(5).ToList();
        if (drops.Count == 0)
        {
            await ReplyAsync(seller, "✅ Kisi product ka margin pichle 30 din mein pichle 30 din se 1 point ya zyada nahi gira (dono muddaton mein cost wale orders dekhe gaye).", ct);
            return;
        }

        var ids = drops.Select(d => d.Id).ToList();
        var names = (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && ids.Contains(p.Id)).ToListAsync(ct)).ToDictionary(p => p.Id, p => Formatters.ProductLabel(p));
        var lines = drops.Select((d, i) => $"{i + 1}. {names[d.Id]} — margin {Pct(d.Before!.Value.Margin)} → {Pct(d.After!.Value.Margin)}; cost {Formatters.Money(Math.Round(d.Before.Value.Cost))} → {Formatters.Money(Math.Round(d.After.Value.Cost))}, price {Formatters.Money(Math.Round(d.Before.Value.Price))} → {Formatters.Money(Math.Round(d.After.Value.Price))}");
        await ReplyAsync(seller, $"📉 Margin sab se zyada gira (pichle 30 din bamuqabla us se pehle ke 30 din)\n\n{string.Join("\n", lines)}\n\nHar order par save hui cost se; qeemat par ghor ke liye: \"price review\".", ct);
        SuggestNext("price review", "margin");
    }

    /// <summary>"Cost 15% barh jaye": margin and the monthly sale needed to cover the expenses, now and after the rise (optionally for one category).</summary>
    private async Task InsightCostShockAsync(Seller seller, string text, decimal rise, CancellationToken ct)
    {
        rise = Math.Clamp(rise, 1, 200);
        var now = DateTime.UtcNow;
        var start = now.AddDays(-30);
        var categories = await CategoriesInTextAsync(seller, text, ct);
        var items = await _db.OrderItems.AsNoTracking()
            .Where(i => i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= start && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { i.ProductId, i.UnitPrice, i.Quantity, i.UnitCost })
            .ToListAsync(ct);
        var ids = items.Where(i => i.ProductId != null).Select(i => i.ProductId!.Value).Distinct().ToList();
        var products = (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && ids.Contains(p.Id)).Select(p => new { p.Id, p.CostPrice, p.Category }).ToListAsync(ct)).ToDictionary(p => p.Id);
        var scope = categories.Count == 0 ? items : items.Where(i => i.ProductId is { } id && products.TryGetValue(id, out var pr) && pr.Category != null && categories.Contains(pr.Category, StringComparer.OrdinalIgnoreCase)).ToList();
        var costed = scope.Select(i => (Item: i, Cost: i.UnitCost ?? (i.ProductId is { } id && products.TryGetValue(id, out var pr) ? pr.CostPrice : null))).Where(x => x.Cost is not null).ToList();
        if (costed.Count == 0)
        {
            await ReplyAsync(seller, "Pichle 30 din ke orders mein cost wala koi item nahi, is liye hisaab nahi ho sakta. Cost likhein, jaise: \"Kurti - 1800, cost 1200\".", ct);
            return;
        }

        var revenue = costed.Sum(x => x.Item.UnitPrice * x.Item.Quantity);
        var cost = costed.Sum(x => x.Cost!.Value * x.Item.Quantity);
        var expenses = (await _db.Expenses.AsNoTracking().Where(e => e.SellerId == seller.Id && e.CreatedAt >= start).Select(e => e.Amount).ToListAsync(ct)).Sum();
        var margin = (revenue - cost) / revenue * 100m;
        var newCost = cost * (1m + rise / 100m);
        var newMargin = (revenue - newCost) / revenue * 100m;
        string Breakeven(decimal m) => m > 0 ? Formatters.Money(Math.Round(expenses / (m / 100m))) : "mumkin nahi (margin khatam)";
        var scopeName = categories.Count > 0 ? string.Join(", ", categories) : "sab products";
        var sameProfitRise = rise * cost / revenue;
        await ReplyAsync(seller,
            $"⚖️ Cost {Pct(rise)} barhi to? — {scopeName} (pichle 30 din ke orders)\n\n" +
            $"Abhi: sale {Formatters.Money(revenue)}, cost {Formatters.Money(cost)}, margin {Pct(margin)}\n" +
            $"Kharche (30 din): {Formatters.Money(expenses)} → abhi kharche nikalne ke liye mahine ki sale: {Breakeven(margin)}\n" +
            $"Cost {Pct(rise)} barhne ke baad: margin {Pct(newMargin)} → kharche nikalne ke liye sale: {Breakeven(newMargin)}\n" +
            $"Munafa {Formatters.Money(revenue - cost)} → {Formatters.Money(revenue - newCost)}\n\n" +
            $"Qeemat na barhain to yeh nuqsan. Fi piece munafa wohi rakhne ke liye qeemat taqreeban {Pct(sameProfitRise)} barhani paregi; margin% wohi rakhne ke liye {Pct(rise)}.\n" +
            "Break-even = mahine ke kharche ÷ margin (sirf aap ke saved kharche aur cost wale items).", ct);
        SuggestNext("price review", "monthly net");
    }

    /// <summary>Products whose current cost is below what recent orders paid, while the shelf price is unchanged: the margin rose on its own.</summary>
    private async Task InsightCostDropAsync(Seller seller, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-60);
        var items = await _db.OrderItems.AsNoTracking()
            .Where(i => i.ProductId != null && i.UnitCost != null && i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= since
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { ProductId = i.ProductId!.Value, i.UnitPrice, Cost = i.UnitCost!.Value, i.Quantity })
            .ToListAsync(ct);
        var products = (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive && p.CostPrice != null).ToListAsync(ct)).ToDictionary(p => p.Id);
        var found = items.GroupBy(i => i.ProductId).Where(g => products.ContainsKey(g.Key))
            .Select(g => (P: products[g.Key], OldCost: g.Sum(i => i.Cost * i.Quantity) / g.Sum(i => (decimal)i.Quantity), OldPrice: g.Sum(i => i.UnitPrice * i.Quantity) / g.Sum(i => (decimal)i.Quantity)))
            .Where(x => x.P.CostPrice!.Value <= x.OldCost * 0.98m && Math.Abs(x.P.Price - x.OldPrice) <= x.OldPrice * 0.02m && x.P.Price > 0)
            .OrderByDescending(x => x.OldCost - x.P.CostPrice!.Value).Take(5).ToList();
        if (found.Count == 0)
        {
            await ReplyAsync(seller, "Koi aisa product nahi mila jis ki cost pichle orders se kam ho gayi ho aur price wohi ho. (Cost ki tabdeeli tab nazar aati hai jab aap product par nayi cost likhte hain, jaise: \"Kurti - 1800, cost 1100\".)", ct);
            return;
        }
        var lines = found.Select((x, i) => $"{i + 1}. {Formatters.ProductLabel(x.P)} — cost {Formatters.Money(Math.Round(x.OldCost))} → {Formatters.Money(x.P.CostPrice!.Value)}, price wohi {Formatters.Money(x.P.Price)}\n   margin {Pct((x.OldPrice - x.OldCost) / x.OldPrice * 100m)} → {Pct((x.P.Price - x.P.CostPrice.Value) / x.P.Price * 100m)}");
        await ReplyAsync(seller, $"💡 Cost kam hui, price purani — margin khud barh gaya\n\n{string.Join("\n", lines)}\n\nChahein to price kam kar ke zyada bech sakte hain (\"margin\" se gunjaish dekhein).", ct);
        SuggestNext("margin", "price review");
    }

    // ---------- delivery

    private async Task InsightFailedDeliveryCitiesAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end && o.Customer != null && o.Customer.City != null
                        && (o.ShippedAt != null || o.Status == OrderStatus.Shipped || o.Status == OrderStatus.Delivered || o.Status == OrderStatus.Returned))
            .Select(o => new { City = o.Customer!.City!, o.Status })
            .ToListAsync(ct);
        var groups = rows.Where(r => !string.IsNullOrWhiteSpace(r.City)).GroupBy(r => r.City.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Shipped: g.Count(), Failed: g.Count(r => r.Status == OrderStatus.Returned)))
            .Where(g => g.Shipped >= 3 && g.Failed > 0).OrderByDescending(g => (decimal)g.Failed / g.Shipped).ThenByDescending(g => g.Failed).Take(6).ToList();
        if (groups.Count == 0)
        {
            await ReplyAsync(seller, $"✅ Failed delivery ({label}): kisi shehar mein (kam az kam 3 parcel bheje) koi parcel wapas nahi aaya, ya shehar save nahi.", ct);
            return;
        }
        var lines = groups.Select((g, i) => $"{i + 1}. {g.Name} — {g.Failed} / {g.Shipped} wapas ({Pct((decimal)g.Failed / g.Shipped * 100m)})");
        await ReplyAsync(seller, $"📍 Kahan parcel sab se zyada wapas aate hain — {label}\n\n{string.Join("\n", lines)}\n\nWapas aana = status \"returned\". In shehron ke gahakon se advance (Easypaisa / bank) mangna behtar rahega.", ct);
        SuggestNext("courier returns", "return rate");
    }

    /// <summary>Returned orders of the period: units that went back into tracked stock, and the losses logged for damaged / lost goods.</summary>
    private async Task InsightReturnedStockAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "30d");
        var returned = await _db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.Status == OrderStatus.Returned && (o.ReturnedAt ?? o.UpdatedAt) >= start && (o.ReturnedAt ?? o.UpdatedAt) < end)
            .ToListAsync(ct);
        var losses = await _db.Losses.AsNoTracking().Where(l => l.SellerId == seller.Id && l.CreatedAt >= start && l.CreatedAt < end).ToListAsync(ct);
        if (returned.Count == 0 && losses.Count == 0)
        {
            await ReplyAsync(seller, $"↩️ Return ka stock ({label}): koi return nahi aaya aur koi nuqsan darj nahi.", ct);
            return;
        }

        var ids = returned.SelectMany(o => o.Items).Where(i => i.ProductId != null).Select(i => i.ProductId!.Value).Distinct().ToList();
        var tracked = (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && ids.Contains(p.Id) && p.StockQty != null).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        var units = returned.SelectMany(o => o.Items).ToList();
        var backInStock = units.Where(i => i.ProductId is { } id && tracked.Contains(id)).Sum(i => i.Quantity);
        await ReplyAsync(seller,
            $"↩️ Return ka maal — {label}\n\n" +
            $"Wapas aaye orders: {returned.Count} ({units.Sum(i => i.Quantity)} piece)\n" +
            $"Stock mein khud wapas chadhe (track hone wale products): {backInStock} piece\n" +
            (units.Sum(i => i.Quantity) - backInStock > 0 ? $"Stock track na hone wale: {units.Sum(i => i.Quantity) - backInStock} piece\n" : "") +
            $"Kharab / gum likha gaya: {losses.Sum(l => l.Quantity)} piece, cost {Formatters.Money(losses.Sum(l => l.Quantity * l.UnitCost))}" +
            (losses.Count == 0 ? "\nRaaste mein kharab ya gum hone ka bot khud nahi jaanta — jab ho to likhein: \"loss Kurti 2 damaged\"." : "") +
            "\n\nBot return mark karte hi stock khud wapas chadha deta hai; parcel kharab nikla to us ka nuqsan alag se likhna hota hai.", ct);
        SuggestNext("stock", "return rate");
    }

    /// <summary>"Delivery charges 200 se 250": how orders at different charges fared so far (not a forecast), and what the extra Rs.50 would add if volume held.</summary>
    private async Task InsightDeliveryChargeAsync(Seller seller, string text, decimal newCharge, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, null, "180d");
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end)
            .Select(o => new { o.DeliveryCharge, o.Status, City = o.Customer!.City })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            await ReplyAsync(seller, "Abhi koi order nahi, is liye hisaab nahi ho sakta.", ct);
            return;
        }

        var groups = rows.GroupBy(r => r.DeliveryCharge).Select(g => (Charge: g.Key, Orders: g.Count(), Cancelled: g.Count(r => r.Status == OrderStatus.Cancelled))).Where(g => g.Orders >= 3)
            .OrderByDescending(g => g.Orders).Take(4).ToList();
        var lines = groups.Select(g => $"• Delivery {(g.Charge == 0 ? "muft" : Formatters.Money(g.Charge))}: {g.Orders} orders · cancel {g.Cancelled} ({Pct((decimal)g.Cancelled / g.Orders * 100m)})");
        var outside = !string.IsNullOrWhiteSpace(seller.City) ? rows.Count(r => !string.IsNullOrWhiteSpace(r.City) && !PlaceMatches(seller.City!, r.City)) : 0;
        await ReplyAsync(seller,
            $"🚚 Delivery charges ka asar — {label}\n\n" +
            (lines.Any() ? string.Join("\n", lines) : "Kisi ek charge par 3 se zyada orders nahi, is liye muqabla nahi ban saka.") + "\n\n" +
            (outside > 0 ? $"Dusre shehron ke {outside} orders par {Formatters.Money(newCharge)} charge hota to har order mein ek jaisi raqam barhti.\n" : "") +
            "Charge badalne se orders kam honge ya nahi — yeh bot nahi jaanta. Upar sirf purana record hai (zaroori nahi ke charge hi wajah ho); chhote qadam se aazma kar dekhein aur phir yehi sawal poochein.", ct);
        SuggestNext("delivery time", "city wise orders");
    }

    // ---------- cash

    /// <summary>Money received (orders marked paid) against expenses in a period; the cash-only line counts COD and counter sales.</summary>
    private async Task InsightCashFlowAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "thismonth");
        var paid = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.PaidAt != null && o.PaidAt >= start && o.PaidAt < end && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .Select(o => new { o.PaymentMethod, o.Total, o.AmountPaid, o.PaymentStatus })
            .ToListAsync(ct);
        var expenses = (await _db.Expenses.AsNoTracking().Where(e => e.SellerId == seller.Id && e.CreatedAt >= start && e.CreatedAt < end).Select(e => e.Amount).ToListAsync(ct)).Sum();
        var received = paid.Sum(o => o.PaymentStatus == PaymentStatus.Paid ? o.Total : Math.Min(o.AmountPaid, o.Total));
        var cash = paid.Where(o => o.PaymentMethod == OrderPaymentMethod.Cod).Sum(o => o.PaymentStatus == PaymentStatus.Paid ? o.Total : Math.Min(o.AmountPaid, o.Total));
        string Net(decimal v) => (v >= 0 ? "" : "-") + Formatters.Money(Math.Abs(v));
        await ReplyAsync(seller,
            $"💵 Cash flow — {label}\n\n" +
            $"Kul wasooli (paid orders): {Formatters.Money(received)}\n  us mein cash (COD / dukaan par): {Formatters.Money(cash)}\n" +
            $"Kharche: {Formatters.Money(expenses)}\n\n" +
            $"Net cash flow (kul wasooli − kharche): {Net(received - expenses)} → {(received - expenses >= 0 ? "✅ positive" : "⚠️ negative")}\n" +
            $"Sirf cash wasooli − kharche: {Net(cash - expenses)} → {(cash - expenses >= 0 ? "✅ positive" : "⚠️ negative")}\n\n" +
            "Wasooli tab ginti hai jab order \"paid\" mark ho; advance jis tareekh ko mila uski tafseel save nahi hoti.", ct);
        SuggestNext("expenses", "monthly net");
    }

    /// <summary>Expected cash in the open drawer, and how much to keep for tomorrow's payment vs deposit (the amount for the supplier is the seller's to say).</summary>
    private async Task InsightCashPlanAsync(Seller seller, decimal? supplierPayment, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var open = await OpenDrawerAsync(seller, ct);
        if (open is null)
        {
            await ReplyAsync(seller, "Abhi koi drawer khula nahi, is liye cash ka hisaab nahi ho sakta. Kholne ke liye: drawer open 5000", ct);
            return;
        }

        var summary = await SummariseCashAsync(seller, open, now, ct);
        var recentOut = await _db.CashMovements.AsNoTracking().Where(m => m.SellerId == seller.Id && m.Kind == CashMovementKind.Out && m.CreatedAt >= now.AddDays(-14)).Select(m => m.Amount).ToListAsync(ct);
        var recentExpenses = await _db.Expenses.AsNoTracking().Where(e => e.SellerId == seller.Id && e.CreatedAt >= now.AddDays(-14)).Select(e => e.Amount).ToListAsync(ct);
        var dailyOut = (recentOut.Sum() + recentExpenses.Sum()) / 14m;
        var line = supplierPayment is { } pay
            ? $"Kal supplier ko {Formatters.Money(pay)} dene hain + roz ke kharche ~{Formatters.Money(Math.Round(dailyOut))} (pichle 14 din ki average)\n" +
              (summary.Expected >= pay + dailyOut
                  ? $"→ Drawer mein rakhein ~{Formatters.Money(Math.Round(pay + dailyOut))}, baqi ~{Formatters.Money(Math.Round(summary.Expected - pay - dailyOut))} bank mein jama karwayein."
                  : $"⚠️ Drawer mein itna cash nahi: {Formatters.Money(summary.Expected)} hai, zaroorat ~{Formatters.Money(Math.Round(pay + dailyOut))} — kami ~{Formatters.Money(Math.Round(pay + dailyOut - summary.Expected))}.")
            : $"Roz ke kharche ~{Formatters.Money(Math.Round(dailyOut))} (pichle 14 din ki average).\nSupplier ko kal kitna dena hai yeh bata dein, jaise: \"kal supplier ko 20000 dene hain\" — phir bataunga kitna rakhna aur kitna jama karwana hai.";
        await ReplyAsync(seller, $"💵 Drawer band karne se pehle\n\nAb drawer mein hona chahiye: {Formatters.Money(summary.Expected)}\n{line}\n\nYeh drawer ke hisaab ka andaza hai; band karte waqt gine hue paise likhein: drawer close <paise>.", ct);
        SuggestNext("drawer", "expenses");
    }

    // ---------- seasons, fixed costs, life cycle, mix, audit

    /// <summary>"Eid par grocery ka stock kitna barhana chahiye": last Eid's sale in that category against the 30 days before, and which items would run short first at that pace.</summary>
    private async Task InsightEidStockAsync(Seller seller, string text, CancellationToken ct)
    {
        var adha = System.Text.RegularExpressions.Regex.IsMatch(text, @"adha|bakra|bakr|qurbani|قربانی|بکرا", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var now = DateTime.UtcNow;
        (DateTime Start, DateTime End)? Window(int year) => adha ? HijriWindow(12, 10, 10, 2, year) : HijriWindow(10, 1, 10, 2, year);
        var thisYear = Window(now.Year);
        var window = thisYear is { } t && t.End <= now ? thisYear : Window(now.Year - 1);
        if (window is not { } w)
        {
            await ReplyAsync(seller, "Maaf kijiye, Eid ki tareekh nahi nikal saka.", ct);
            return;
        }

        var categories = await CategoriesInTextAsync(seller, text, ct);
        var scopeName = categories.Count > 0 ? string.Join(", ", categories) : "sab products";

        var categoryOf = (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id).Select(p => new { p.Id, p.Category }).ToListAsync(ct)).ToDictionary(p => p.Id, p => p.Category);
        async Task<Dictionary<string, int>> UnitsAsync(DateTime from, DateTime to) =>
            (await _db.OrderItems.AsNoTracking()
                .Where(i => i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= from && i.Order.CreatedAt < to && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
                .Select(i => new { i.ProductNameSnapshot, i.Quantity, i.ProductId })
                .ToListAsync(ct))
            .Where(i => categories.Count == 0 || (i.ProductId is { } id && categoryOf.TryGetValue(id, out var c) && c != null && categories.Contains(c, StringComparer.OrdinalIgnoreCase)))
            .GroupBy(i => i.ProductNameSnapshot, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity), StringComparer.OrdinalIgnoreCase);

        var days = Math.Max(1, (int)(w.End - w.Start).TotalDays);
        var during = await UnitsAsync(w.Start, w.End);
        if (during.Count == 0)
        {
            await ReplyAsync(seller, $"📅 Eid ({w.Start:d MMM} – {w.End.AddDays(-1):d MMM yyyy}) par {scopeName} ka koi order darj nahi, is liye pichle saal se muqabla nahi ho sakta.\n(Aap ke orders itne purane honay chahiye.)", ct);
            return;
        }

        var before = await UnitsAsync(w.Start.AddDays(-30), w.Start);
        var perDayDuring = during.Values.Sum() / (decimal)days;
        var perDayBefore = before.Values.Sum() / 30m;
        var uplift = perDayBefore > 0 ? (perDayDuring / perDayBefore - 1m) * 100m : (decimal?)null;

        // Today's daily pace x the same uplift = a stock target; the items with the fewest days left at that pace go short first.
        var top = during.OrderByDescending(kv => kv.Value).Take(3).ToList();
        var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive).ToListAsync(ct);
        var tracked = products.Where(p => p.StockQty != null && top.Any(t2 => t2.Key.Equals(p.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        var rows = tracked.Count > 0 ? await StockRowsAsync(seller, tracked, ct) : new List<StockRow>();
        var factor = 1m + (uplift ?? 0m) / 100m;
        var short3 = rows.Where(r => r.PerDay > 0).Select(r => (r.Product, Days: (decimal)r.Product.StockQty!.Value / (r.PerDay * Math.Max(1m, factor)))).OrderBy(x => x.Days).Take(3).ToList();

        await ReplyAsync(seller,
            $"📅 Pichli Eid ({w.Start:d MMM} – {w.End.AddDays(-1):d MMM yyyy}) — {scopeName}\n\n" +
            $"Roz ke piece: {Num(perDayDuring)} (is se pehle ke 30 din: {Num(perDayBefore)})" + (uplift is { } u ? $" → {(u >= 0 ? "+" : "")}{Pct(u)}" : "") + "\n" +
            $"Sab se zyada bika: {string.Join(", ", top.Select(t2 => $"{t2.Key} ({t2.Value})"))}\n" +
            (uplift is { } up ? $"\nIs baar stock taqreeban {Pct(Math.Max(0, up))} barhana munasib hai (isi barhat ke hisab se).\n" : "\nPehle ke 30 din ka data nahi, is liye fisad nahi nikal saka.\n") +
            (short3.Count > 0 ? "Isi raftaar par pehle kam parne wale:\n" + string.Join("\n", short3.Select(x => $"• {Formatters.ProductLabel(x.Product)} — ~{Num(x.Days)} din ka stock")) + "\n" : "") +
            "\n(Eid ki tareekh taqreeban hai; yeh pichle saal ka dohrao hai, pakki peshgoi nahi.)", ct);
        SuggestNext("stock", "reorder");
    }

    /// <summary>"Kiraya aur bijli nikalne ke liye roz kam az kam kitni sale": monthly fixed costs (rent / electricity categories, else all expenses) over the margin, per day.</summary>
    private async Task InsightDailySaleForFixedCostsAsync(Seller seller, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var start = now.AddDays(-90);
        var expenses = await _db.Expenses.AsNoTracking().Where(e => e.SellerId == seller.Id && e.CreatedAt >= start).ToListAsync(ct);
        if (expenses.Count == 0)
        {
            await ReplyAsync(seller, "Pichle 3 mahine mein koi kharcha darj nahi, is liye hisaab nahi ho sakta. Likhein, jaise: \"expense 40000 kiraya\", \"expense 12000 bijli\".", ct);
            return;
        }

        var fixedOnes = expenses.Where(e => System.Text.RegularExpressions.Regex.IsMatch(e.Category + " " + e.Note, @"kiraya|rent|bijli|electric|bill|utilit|کرایہ|بجلی", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)).ToList();
        var basis = fixedOnes.Count > 0 ? fixedOnes : expenses;
        var monthly = basis.Sum(e => e.Amount) / 3m;
        var (_, counted, revenue, cost) = await ComputeProfitAsync(seller, start, DateTime.MaxValue, ct);
        var currentDaily = (await ActiveOrdersIn(seller, now.AddDays(-30), DateTime.MaxValue).Select(o => o.Total).ToListAsync(ct)).Sum() / 30m;
        if (counted == 0 || revenue <= 0 || revenue - cost <= 0)
        {
            await ReplyAsync(seller,
                $"Mahine ke {(fixedOnes.Count > 0 ? "kiraya/bijli jaise" : "kul")} kharche taqreeban {Formatters.Money(Math.Round(monthly))} hain (pichle 3 mahine ki average).\n" +
                "Roz ki sale ka hisaab margin chahiye — products ki cost likhein, jaise: \"Kurti - 1800, cost 1200\".", ct);
            return;
        }

        var margin = (revenue - cost) / revenue;
        var needMonthly = monthly / margin;
        await ReplyAsync(seller,
            "🏪 Kharche nikalne ke liye roz ki sale\n\n" +
            $"{(fixedOnes.Count > 0 ? "Kiraya / bijli / bill" : "Kul kharche")} (mahine ki average, pichle 3 mahine): {Formatters.Money(Math.Round(monthly))}\n" +
            $"Aap ka margin (pichle 3 mahine): {Pct(margin * 100m)}\n" +
            $"Kam az kam sale: {Formatters.Money(Math.Round(needMonthly))} mahina = {Formatters.Money(Math.Round(needMonthly / 30m))} roz\n" +
            $"Abhi ki roz ki average sale (30 din): {Formatters.Money(Math.Round(currentDaily))} → " + (currentDaily >= needMonthly / 30m ? "✅ kharche nikal rahe hain" : "⚠️ kam hai") + "\n\n" +
            "Sale = marginal munafa se kharche nikalne ki had (break-even); is mein dukaan ke dusre kharche shamil nahi.", ct);
        SuggestNext("monthly net", "profit");
    }

    /// <summary>Products whose units fell in each of the last three 30-day windows: nearing the end of their run.</summary>
    private async Task InsightDecliningProductsAsync(Seller seller, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await _db.OrderItems.AsNoTracking()
            .Where(i => i.ProductId != null && i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= now.AddDays(-90)
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { ProductId = i.ProductId!.Value, i.Quantity, i.Order!.CreatedAt })
            .ToListAsync(ct);
        var falling = rows.GroupBy(r => r.ProductId)
            .Select(g => (Id: g.Key,
                A: g.Where(r => r.CreatedAt < now.AddDays(-60)).Sum(r => r.Quantity),
                B: g.Where(r => r.CreatedAt >= now.AddDays(-60) && r.CreatedAt < now.AddDays(-30)).Sum(r => r.Quantity),
                C: g.Where(r => r.CreatedAt >= now.AddDays(-30)).Sum(r => r.Quantity)))
            .Where(x => x.A >= 6 && x.A > x.B && x.B > x.C).OrderByDescending(x => x.A - x.C).Take(6).ToList();
        if (falling.Count == 0)
        {
            await ReplyAsync(seller, "✅ Koi product aisa nahi mila jis ki bikri lagataar teen mahine se gir rahi ho (pehle mahine mein kam az kam 6 piece wale dekhe gaye).\nYeh 90 din ka data chahta hai.", ct);
            return;
        }
        var ids = falling.Select(f => f.Id).ToList();
        var products = (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && ids.Contains(p.Id)).ToListAsync(ct)).ToDictionary(p => p.Id);
        var lines = falling.Select((f, i) => $"{i + 1}. {Formatters.ProductLabel(products[f.Id])} — {f.A} → {f.B} → {f.C} piece (teen 30-din ke daur)" + (products[f.Id].StockQty is { } st ? $" · stock {st}" : ""));
        await ReplyAsync(seller,
            $"📉 Jin ki bikri lagataar gir rahi hai\n\n{string.Join("\n", lines)}\n\nIn ka naya order dene se pehle bacha hua stock aur bikri ki raftaar dekh lein (\"stock\", \"reorder\"). Yeh sirf bikri ka rujhan hai; wajah (mausam, qeemat, muqabla) bot nahi jaanta.", ct);
        SuggestNext("stock", "slow movers");
    }

    /// <summary>Premium (priced at or above the catalog median) against budget items: share of sales and units, this month against the one before.</summary>
    private async Task InsightPriceMixAsync(Seller seller, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive && p.Price > 0).ToListAsync(ct);
        if (products.Count < 2)
        {
            await ReplyAsync(seller, "Muqabla ke liye catalog mein kam az kam 2 products chahiye.", ct);
            return;
        }
        var sorted = products.Select(p => p.Price).OrderBy(v => v).ToList();
        var median = sorted[sorted.Count / 2];
        var items = await _db.OrderItems.AsNoTracking()
            .Where(i => i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= now.AddDays(-60) && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new PricedSale(i.UnitPrice, i.Quantity, i.ProductNameSnapshot, i.Order!.CreatedAt))
            .ToListAsync(ct);
        if (items.Count == 0)
        {
            await ReplyAsync(seller, "Pichle 60 din mein koi order nahi, is liye rujhan nahi nikal sakta.", ct);
            return;
        }

        (decimal PremiumSales, decimal BudgetSales, int PremiumUnits, int BudgetUnits) Split(IEnumerable<PricedSale> rows)
        {
            var list = rows.ToList();
            return (list.Where(i => i.UnitPrice >= median).Sum(i => i.UnitPrice * i.Quantity), list.Where(i => i.UnitPrice < median).Sum(i => i.UnitPrice * i.Quantity),
                list.Where(i => i.UnitPrice >= median).Sum(i => i.Quantity), list.Where(i => i.UnitPrice < median).Sum(i => i.Quantity));
        }
        var cut = now.AddDays(-30);
        var prev = Split(items.Where(i => i.CreatedAt < cut));
        var cur = Split(items.Where(i => i.CreatedAt >= cut));
        string Share(decimal part, decimal other) => part + other > 0 ? Pct(part / (part + other) * 100m) : "—";
        var trend = (cur.PremiumSales + cur.BudgetSales) > 0 && (prev.PremiumSales + prev.BudgetSales) > 0
            ? (cur.PremiumSales / (cur.PremiumSales + cur.BudgetSales) > prev.PremiumSales / (prev.PremiumSales + prev.BudgetSales) ? "Mehnge maal ki taraf rujhan hai." : "Sasta maal ka hissa barh raha hai.")
            : "Do muddaton ka data poora nahi.";
        var movers = items.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Change: g.Where(i => i.CreatedAt >= cut).Sum(i => i.Quantity) - g.Where(i => i.CreatedAt < cut).Sum(i => i.Quantity))).ToList();
        var up = movers.OrderByDescending(m => m.Change).FirstOrDefault();
        var down = movers.OrderBy(m => m.Change).FirstOrDefault();
        await ReplyAsync(seller,
            $"📊 Mehnge ya sasta maal? (qeemat {Formatters.Money(median)} ya zyada = mehnga)\n\n" +
            $"Pehle 30 din: mehnge maal ka hissa {Share(prev.PremiumSales, prev.BudgetSales)} sale ({prev.PremiumUnits} piece), sasta {prev.BudgetUnits} piece\n" +
            $"Pichle 30 din: mehnge maal ka hissa {Share(cur.PremiumSales, cur.BudgetSales)} sale ({cur.PremiumUnits} piece), sasta {cur.BudgetUnits} piece\n" +
            $"→ {trend}\n" +
            (up.Change > 0 ? $"Sab se zyada barha: {up.Name} (+{up.Change} piece)\n" : "") + (down.Change < 0 ? $"Sab se zyada ghata: {down.Name} ({down.Change} piece)\n" : "") +
            "\n\"Mehnga\" ka paimana catalog ki beech wali qeemat hai.", ct);
        SuggestNext("trending products", "sales by category");
    }

    /// <summary>The "Business Audit": one message of the numbers that most often hide a problem, each with where to look next.</summary>
    private async Task InsightAuditAsync(Seller seller, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var lines = new List<string>();
        string? firstFix = null, secondFix = null;
        void Flag(string text, string fix) { lines.Add("⚠️ " + text); if (firstFix is null) firstFix = fix; else secondFix ??= fix; }

        // Stock that has not sold for 90 days.
        var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive).ToListAsync(ct);
        var withStock = products.Where(p => p.StockQty > 0 && p.CreatedAt < now.AddDays(-90)).ToList();
        var soldIds = withStock.Count == 0 ? new HashSet<int>() : (await _db.OrderItems.AsNoTracking()
            .Where(i => i.ProductId != null && withStock.Select(p => p.Id).Contains(i.ProductId.Value) && i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= now.AddDays(-90)
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => i.ProductId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var locked = withStock.Where(p => !soldIds.Contains(p.Id) && p.CostPrice != null).ToList();
        if (locked.Count > 0) Flag($"Sarmaya phansa: {Formatters.Money(locked.Sum(p => p.StockQty!.Value * p.CostPrice!.Value))} ka maal 90 din se nahi bika ({locked.Count} products)", "overstocked products");
        else lines.Add("✅ 90 din se na bikne wale stock mein sarmaya phansa nahi");

        // Stock running out within a week.
        var tracked = products.Where(p => p.StockQty != null).ToList();
        if (tracked.Count > 0)
        {
            var due = (await StockRowsAsync(seller, tracked, ct)).Where(r => r.Product.StockQty <= 0 && r.Sold > 0 || r.DaysLeft is { } d && d > 0 && d <= 7).ToList();
            if (due.Count > 0) Flag($"{due.Count} product(s) ka stock 7 din ke andar khatam (ya khatam hai magar bikta tha)", "stock ending");
            else lines.Add("✅ Agle 7 din mein kisi tracked product ka stock khatam nahi");
        }

        // Money still owed.
        var owed = (await _db.Orders.AsNoTracking().Where(o => o.SellerId == seller.Id && o.PaymentStatus != PaymentStatus.Paid && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned).ToListAsync(ct))
            .Where(o => OrderMoney.Balance(o) > 0).ToList();
        if (owed.Count > 0)
        {
            var old = owed.Where(o => (now - o.CreatedAt).TotalDays > 30).ToList();
            var text = $"Bazar mein phansa paisa: {Formatters.Money(owed.Sum(OrderMoney.Balance))} ({owed.Count} orders)" + (old.Count > 0 ? $", us mein {Formatters.Money(old.Sum(OrderMoney.Balance))} 30 din se purana" : "");
            if (old.Count > 0) Flag(text, "paisa phansa hua"); else lines.Add("ℹ️ " + text);
        }
        else lines.Add("✅ Kisi order ka paisa baqi nahi");

        // Low margins.
        var thin = products.Where(p => p.CostPrice != null && p.Price > 0 && (p.Price - p.CostPrice!.Value) / p.Price * 100m < LowMarginPercent).ToList();
        if (thin.Count > 0) Flag($"{thin.Count} product(s) ka margin {Pct(LowMarginPercent)} se kam", "price review");
        else if (products.Any(p => p.CostPrice != null)) lines.Add("✅ Kisi product ka margin 20% se kam nahi");
        else lines.Add("ℹ️ Margin nahi dekh sakta — products ki cost save nahi");

        // Expenses against sales this month.
        var (ms, me) = SellerClock.CurrentMonthRangeUtc(seller.TimeZoneId, now);
        var sales = (await ActiveOrdersIn(seller, ms, me).Select(o => o.Total).ToListAsync(ct)).Sum();
        var exp = (await _db.Expenses.AsNoTracking().Where(e => e.SellerId == seller.Id && e.CreatedAt >= ms && e.CreatedAt < me).Select(e => e.Amount).ToListAsync(ct)).Sum();
        if (sales > 0) lines.Add($"ℹ️ Is mahine kharche = sale ka {Pct(exp / sales * 100m)} ({Formatters.Money(exp)} / {Formatters.Money(sales)})");

        // Customers who went quiet.
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.Customer!.DeletedAt == null)
            .Select(o => new { o.CustomerId, o.CreatedAt }).ToListAsync(ct);
        var quiet = rows.GroupBy(r => r.CustomerId).Select(g => g.Select(r => r.CreatedAt).OrderBy(d => d).ToList())
            .Count(d => d.Count >= 3 && (now - d[^1]).TotalDays >= 60 && (now - d[^1]).TotalDays >= 2 * d.Zip(d.Skip(1), (a, b) => (b - a).TotalDays).Average());
        if (quiet > 0) Flag($"{quiet} pakke customer 60+ din se khamosh", "customers at risk");
        else lines.Add("✅ Koi pakka customer khamosh nahi hua");

        if (products.Count == 0 && rows.Count == 0)
        {
            await ReplyAsync(seller, "Abhi catalog aur orders dono khali hain, is liye audit ke liye kuch nahi. Pehle products likhein aur orders save karein.", ct);
            return;
        }
        await ReplyAsync(seller, "🔍 Business audit\n\n" + string.Join("\n", lines) + "\n\n⚠️ wali line par neeche ka button dabayein. Har sawal ki tafseel ke liye us ka naam likhein, jaise: \"price review\", \"overstocked products\".", ct);
        if (firstFix is not null) SuggestNext(new[] { firstFix, secondFix ?? "monthly net" });
    }
}
