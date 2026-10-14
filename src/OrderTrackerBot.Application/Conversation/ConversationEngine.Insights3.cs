using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Harder business questions, part 1 (AdvancedInsightParser picks the topic): what is bought together, bundles, customers about to leave, buying pattern, and pricing what-ifs.
// All counting and arithmetic on saved orders; a suggestion is labelled as a guide and never claims to predict what buyers will do.
public partial class ConversationEngine
{
    private async Task<bool> TryHandleAdvancedInsightAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, string? period, string text, CancellationToken ct)
    {
        switch (cmd.Text)
        {
            case "pairs": await InsightPairsAsync(seller, text, period, ct); break;
            case "nosolo": await InsightNeverAloneAsync(seller, period, ct); break;
            case "bundle": await InsightBundleAsync(seller, period, ct); break;
            case "churnrisk": await InsightChurnRiskAsync(seller, ctx, (int)(cmd.Amount ?? 60), ct); break;
            case "aovdrop": await InsightOrderSizeDropAsync(seller, ctx, ct); break;
            case "winback": await InsightWinBackAsync(seller, cmd.Amount ?? 200, int.TryParse(cmd.Text3, out var n) ? n : null, ct); break;
            case "pattern": await InsightBuyingPatternAsync(seller, text, ct); break;
            case "costup": await InsightCostRiseAsync(seller, text, cmd.Amount ?? 0, ct); break;
            case "priceeffect": await InsightPriceChangeAsync(seller, text, cmd.Amount ?? 0, ct); break;
            case "costlylow": await InsightCostlyLowMarginAsync(seller, ct); break;
            case "suppliers": await InsightSuppliersAsync(seller, period, ct); break;
            default: return await TryHandleAdvancedInsight2Async(seller, ctx, cmd, period, text, ct);
        }
        return true;
    }

    // ---------- baskets

    private sealed record BasketLine(int OrderId, string Key, string Name, int Quantity, decimal Price, decimal? Cost, decimal OrderTotal);

    private async Task<List<BasketLine>> BasketLinesAsync(Seller seller, DateTime start, DateTime end, CancellationToken ct) =>
        (await _db.OrderItems.AsNoTracking()
            .Where(i => i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= start && i.Order.CreatedAt < end
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { i.OrderId, i.ProductId, i.ProductNameSnapshot, i.Quantity, i.UnitPrice, i.UnitCost, i.Order!.Total })
            .ToListAsync(ct))
        .Select(i => new BasketLine(i.OrderId, i.ProductId?.ToString(CultureInfo.InvariantCulture) ?? "n:" + i.ProductNameSnapshot.Trim().ToLowerInvariant(), i.ProductNameSnapshot, i.Quantity, i.UnitPrice, i.UnitCost, i.Total))
        .ToList();

    private sealed record Basket(int OrderId, decimal Total, HashSet<string> Keys);

    private static List<Basket> Baskets(List<BasketLine> lines) =>
        lines.GroupBy(l => l.OrderId).Select(g => new Basket(g.Key, g.First().OrderTotal, g.Select(l => l.Key).ToHashSet())).ToList();

    private static Dictionary<string, string> NamesOf(List<BasketLine> lines) =>
        lines.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.First().Name);

    private static List<(string A, string B, int Orders)> PairCounts(List<Basket> baskets) =>
        baskets.Where(b => b.Keys.Count >= 2)
            .SelectMany(b => b.Keys.OrderBy(k => k, StringComparer.Ordinal).SelectMany((a, i) => b.Keys.OrderBy(k => k, StringComparer.Ordinal).Skip(i + 1).Select(c => (A: a, B: c))))
            .GroupBy(p => p)
            .Select(g => (g.Key.A, g.Key.B, Orders: g.Count()))
            .OrderByDescending(p => p.Orders).ThenBy(p => p.A, StringComparer.Ordinal).ThenBy(p => p.B, StringComparer.Ordinal)
            .ToList();

    /// <summary>"Kurti ke saath kya lete hain": the products that most often share an order with the named one; two named products are compared; none = the best pairs overall.</summary>
    private async Task InsightPairsAsync(Seller seller, string text, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var lines = await BasketLinesAsync(seller, start, end, ct);
        var baskets = Baskets(lines);
        if (baskets.Count == 0)
        {
            await ReplyAsync(seller, $"🧺 Saath bikne wala maal ({label}): is dauran koi order nahi.", ct);
            return;
        }

        var names = NamesOf(lines);
        var named = (await ProductsInTextAsync(seller, text, ct)).Select(p => p.Id.ToString(CultureInfo.InvariantCulture)).Distinct().Where(names.ContainsKey).ToList();
        if (named.Count >= 2)
        {
            var a = named[0];
            var b = named[1];
            var both = baskets.Where(x => x.Keys.Contains(a) && x.Keys.Contains(b)).ToList();
            var onlyA = baskets.Where(x => x.Keys.Contains(a) && !x.Keys.Contains(b)).ToList();
            var onlyB = baskets.Where(x => x.Keys.Contains(b) && !x.Keys.Contains(a)).ToList();
            static string Avg(List<Basket> list) => list.Count == 0 ? "—" : Formatters.Money(Math.Round(list.Average(x => x.Total)));
            await ReplyAsync(seller,
                $"🧺 {names[a]} aur {names[b]} — {label}\n\n" +
                $"Dono ek saath: {both.Count} orders (har order ki average {Avg(both)})\n" +
                $"Sirf {names[a]} ({names[b]} ke baghair): {onlyA.Count} orders ({Avg(onlyA)})\n" +
                $"Sirf {names[b]} ({names[a]} ke baghair): {onlyB.Count} orders ({Avg(onlyB)})\n\n" +
                "Bot yeh nahi jaanta ke kis order mein aap ne bundle offer di thi — yeh sirf ek saath bikne ka hisaab hai.", ct);
            SuggestNext("bundle offer suggest", "margin");
            return;
        }

        if (named.Count == 1)
        {
            var key = named[0];
            var with = baskets.Where(x => x.Keys.Contains(key)).ToList();
            var partners = with.SelectMany(x => x.Keys.Where(k => k != key)).GroupBy(k => k).Select(g => (Key: g.Key, Orders: g.Count()))
                .OrderByDescending(p => p.Orders).ThenBy(p => names[p.Key], StringComparer.OrdinalIgnoreCase).Take(5).ToList();
            if (partners.Count == 0)
            {
                await ReplyAsync(seller, $"🧺 {names[key]} ({label}): {with.Count} orders mein bika, magar hamesha akela — kisi aur cheez ke saath nahi.", ct);
                return;
            }
            var rows = partners.Select((p, i) => $"{i + 1}. {names[p.Key]} — {p.Orders} orders ({Pct((decimal)p.Orders / with.Count * 100m)})");
            await ReplyAsync(seller,
                $"🧺 {names[key]} ke saath sab se zyada kya bikta hai — {label}\n\n{string.Join("\n", rows)}\n\n" +
                $"{names[key]} kul {with.Count} orders mein bika; {with.Count(x => x.Keys.Count == 1)} mein akela.", ct);
            SuggestNext("bundle offer suggest", "margin");
            return;
        }

        var pairs = PairCounts(baskets).Take(5).ToList();
        if (pairs.Count == 0)
        {
            await ReplyAsync(seller, $"🧺 Saath bikne wala maal ({label}): kisi order mein do alag cheezein nahi thin ({baskets.Count} orders).", ct);
            return;
        }
        var lineText = pairs.Select((p, i) => $"{i + 1}. {names[p.A]} + {names[p.B]} — {p.Orders} orders");
        await ReplyAsync(seller, $"🧺 Sab se zyada ek saath bikne wali jodiyan — {label}\n\n{string.Join("\n", lineText)}\n\n{baskets.Count} orders dekhe gaye. Kisi ek cheez ke liye likhein, jaise: \"Kurti ke saath kya bikta hai\".", ct);
        SuggestNext("bundle offer suggest", "trending products");
    }

    /// <summary>"Kaun se products akele nahi bikte": products that, in 80%+ of their orders, shared the order with something else.</summary>
    private async Task InsightNeverAloneAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var lines = await BasketLinesAsync(seller, start, end, ct);
        var baskets = Baskets(lines);
        var names = NamesOf(lines);
        var stats = names.Keys.Select(k =>
            {
                var with = baskets.Where(b => b.Keys.Contains(k)).ToList();
                var solo = with.Count(b => b.Keys.Count == 1);
                return (Key: k, Orders: with.Count, Solo: solo, Share: with.Count == 0 ? 0m : (decimal)(with.Count - solo) / with.Count);
            })
            .Where(s => s.Orders >= 3).OrderByDescending(s => s.Share).ThenByDescending(s => s.Orders).ThenBy(s => names[s.Key], StringComparer.OrdinalIgnoreCase).ToList();
        if (stats.Count == 0)
        {
            await ReplyAsync(seller, $"🧺 Akele na bikne wala maal ({label}): abhi kisi product ke kam az kam 3 orders nahi — hisaab ke liye data kam hai.", ct);
            return;
        }

        var always = stats.Where(s => s.Share >= 0.8m).Take(3).ToList();
        var shown = always.Count > 0 ? always : stats.Take(3).ToList();
        var rows = shown.Select((s, i) => $"{i + 1}. {names[s.Key]} — {s.Orders} orders mein se {s.Orders - s.Solo} mein doosri cheez ke saath ({Pct(s.Share * 100m)})");
        await ReplyAsync(seller,
            (always.Count > 0 ? $"🧺 Yeh products lagbhag hamesha kisi aur maal ke saath bikte hain — {label}\n\n" : $"🧺 Koi product hamesha ke liye saath nahi bikta; sab se zyada saath bikne wale — {label}\n\n") +
            string.Join("\n", rows) + "\n\nSirf un products ko ginta hai jin ke kam az kam 3 orders hon.", ct);
        SuggestNext("bundle offer suggest", "slow movers");
    }

    /// <summary>"Agle hafte ke liye bundle suggest karo": the most frequent pair, priced as a pack with 10% off against the cost (a worked example, not a promise).</summary>
    private async Task InsightBundleAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var lines = await BasketLinesAsync(seller, start, end, ct);
        var pairs = PairCounts(Baskets(lines)).Take(8).ToList();
        if (pairs.Count == 0)
        {
            await ReplyAsync(seller, $"🎁 Bundle ({label}): abhi koi do cheezein ek saath bikti nazar nahi aatin, is liye jodi suggest nahi kar sakta.", ct);
            return;
        }

        var names = NamesOf(lines);
        var unit = lines.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => (Price: g.Average(l => l.Price), Cost: g.Where(l => l.Cost != null).Select(l => l.Cost!.Value).DefaultIfEmpty(-1m).Average()));
        const decimal off = 10m;
        var scored = pairs.Select(p =>
            {
                var regular = unit[p.A].Price + unit[p.B].Price;
                var bundle = Math.Floor(regular * (1m - off / 100m) / 10m) * 10m;
                var costKnown = unit[p.A].Cost >= 0 && unit[p.B].Cost >= 0;
                var cost = costKnown ? unit[p.A].Cost + unit[p.B].Cost : (decimal?)null;
                return (Pair: p, Regular: regular, Bundle: bundle, Cost: cost, Margin: cost is { } c && bundle > 0 ? (bundle - c) / bundle * 100m : (decimal?)null);
            })
            .Where(s => s.Margin is null || s.Margin >= 15m)
            .OrderByDescending(s => s.Pair.Orders).ThenByDescending(s => s.Margin ?? 0m).Take(2).ToList();
        if (scored.Count == 0)
        {
            await ReplyAsync(seller, $"🎁 Bundle ({label}): jo jodiyan saath bikti hain un par {Pct(off)} chhoot ke baad margin 15% se kam reh jata hai, is liye bundle suggest nahi karta.", ct);
            return;
        }

        var rows = scored.Select((s, i) =>
            $"{i + 1}. {names[s.Pair.A]} + {names[s.Pair.B]} — {s.Pair.Orders} orders mein saath bike\n" +
            $"   alag alag: {Formatters.Money(Math.Round(s.Regular))} → pack {Formatters.Money(s.Bundle)} ({Pct(off)} chhoot)\n" +
            (s.Margin is { } m ? $"   is qeemat par margin {Pct(m)}" : "   margin maloom nahi (cost likhein, jaise: \"Kurti - 1800, cost 1200\")"));
        await ReplyAsync(seller, $"🎁 Bundle offer ki tajweez — {label}\n\n{string.Join("\n", rows)}\n\nYeh misal hai ({Pct(off)} chhoot ke saath); asal chhoot aap tay karein. Bot yeh andaza nahi lagata ke kitna zyada bikega.", ct);
        SuggestNext("margin", "create discount");
    }

    // ---------- customers about to leave

    /// <summary>Customers who used to order regularly (3+ orders) and have been silent for N days and at least twice their usual gap.</summary>
    private async Task InsightChurnRiskAsync(Seller seller, SessionContextData ctx, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 7, 730);
        var now = DateTime.UtcNow;
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.Customer!.DeletedAt == null)
            .Select(o => new { o.CustomerId, o.Customer!.Name, o.Customer.Phone, o.CreatedAt, o.Total })
            .ToListAsync(ct);
        var risky = rows.GroupBy(r => new { r.CustomerId, r.Name, r.Phone })
            .Select(g =>
            {
                var dates = g.Select(r => r.CreatedAt).OrderBy(d => d).ToList();
                var gaps = dates.Zip(dates.Skip(1), (a, b) => (b - a).TotalDays).ToList();
                return (Id: g.Key.CustomerId, Name: g.Key.Name, Phone: g.Key.Phone, Orders: dates.Count, Spent: g.Sum(r => r.Total), Last: dates[^1],
                    Gap: gaps.Count == 0 ? 0 : gaps.Average(), Silent: (now - dates[^1]).TotalDays);
            })
            .Where(c => c.Orders >= 3 && c.Silent >= days && c.Silent >= 2 * c.Gap)
            .OrderByDescending(c => c.Spent).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (risky.Count == 0)
        {
            await ReplyAsync(seller, $"✅ Koi aisa pakka customer nahi mila jo {days} din se khamosh ho aur apni aam wapsi ke waqt se kam az kam dugna waqt guzar chuka ho (kam az kam 3 orders wale dekhe gaye).", ct);
            return;
        }

        var top = risky.Take(5).ToList();
        ctx.LastListCustomerIds = top.Select(c => c.Id).ToList();
        var lines = top.Select((c, i) => $"{i + 1}. {c.Name} — pehle har ~{Math.Max(1, (int)Math.Round(c.Gap))} din mein order ({c.Orders} orders, {Formatters.Money(c.Spent)}), ab {(int)c.Silent} din se khamosh · aakhri {ShortDate(seller, c.Last)}");
        var chat = ChatLink(top[0].Phone);
        await ReplyAsync(seller,
            $"⚠️ Wapas na aane ka khatra — {risky.Count} customers (sab se bada kharch pehle)\n\n{string.Join("\n", lines)}\n\n" +
            "Yeh unhe ginta hai jo kam az kam 3 baar order kar chuke hain aur ab apni aam wapsi ke waqt se dugna zyada khamosh hain." +
            (chat is null ? "" : $"\n{top[0].Name} ko message: {chat}") + "\n\"customer 1\" likh kar detail dekhein.", ct);
        SuggestNext("customer 1", "customers");
    }

    /// <summary>Customers whose average order, over the last 30 days, is half (or less) of what it was the 30 days before.</summary>
    private async Task InsightOrderSizeDropAsync(Seller seller, SessionContextData ctx, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var currentStart = now.AddDays(-30);
        var previousStart = now.AddDays(-60);
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= previousStart && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.Customer!.DeletedAt == null)
            .Select(o => new { o.CustomerId, o.Customer!.Name, o.CreatedAt, o.Total })
            .ToListAsync(ct);
        var dropped = rows.GroupBy(r => new { r.CustomerId, r.Name })
            .Select(g =>
            {
                var cur = g.Where(r => r.CreatedAt >= currentStart).Select(r => r.Total).ToList();
                var prev = g.Where(r => r.CreatedAt < currentStart).Select(r => r.Total).ToList();
                return (Id: g.Key.CustomerId, Name: g.Key.Name, Now: cur.Count == 0 ? 0 : cur.Average(), Before: prev.Count == 0 ? 0 : prev.Average(), CurCount: cur.Count, PrevCount: prev.Count);
            })
            .Where(c => c.CurCount > 0 && c.PrevCount > 0 && c.Now <= c.Before / 2m)
            .OrderByDescending(c => c.Before - c.Now).Take(8).ToList();
        if (dropped.Count == 0)
        {
            await ReplyAsync(seller, "✅ Koi customer nahi mila jis ki average kharidari pichle 30 din mein pichle 30 din ke muqable aadhi ya us se kam rah gayi ho (dono muddaton mein order wale dekhe gaye).", ct);
            return;
        }
        ctx.LastListCustomerIds = dropped.Select(c => c.Id).ToList();
        var lines = dropped.Select((c, i) => $"{i + 1}. {c.Name} — har order ki average {Formatters.Money(Math.Round(c.Before))} → {Formatters.Money(Math.Round(c.Now))} ({Pct((c.Before - c.Now) / c.Before * 100m)} kam)");
        await ReplyAsync(seller, $"📉 Kharidari ghatne wale customers (pichle 30 din bamuqabla us se pehle ke 30 din)\n\n{string.Join("\n", lines)}\n\n\"customer 1\" likh kar detail dekhein.", ct);
        SuggestNext("customer 1", "customers");
    }

    /// <summary>"50 purane customers ko 200 ka discount dun to": the arithmetic of a win-back offer plus how often silent customers came back before — not a forecast.</summary>
    private async Task InsightWinBackAsync(Seller seller, decimal discount, int? customers, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        const int silentDays = 60;
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.Customer!.DeletedAt == null)
            .Select(o => new { o.CustomerId, o.CreatedAt, o.Total })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            await ReplyAsync(seller, "Abhi koi order data nahi, is liye hisaab nahi ho sakta.", ct);
            return;
        }

        var perCustomer = rows.GroupBy(r => r.CustomerId).Select(g => g.OrderBy(r => r.CreatedAt).ToList()).ToList();
        var silentNow = perCustomer.Where(c => (now - c[^1].CreatedAt).TotalDays >= silentDays).ToList();
        var returnedAfterGap = perCustomer.Sum(c => c.Zip(c.Skip(1), (a, b) => (b.CreatedAt - a.CreatedAt).TotalDays).Count(g => g >= silentDays));
        var attempts = returnedAfterGap + silentNow.Count;
        var count = customers ?? silentNow.Count;
        var avgOrder = silentNow.Count > 0 ? silentNow.SelectMany(c => c).Average(r => r.Total) : rows.Average(r => r.Total);

        var (_, counted, revenue, cost) = await ComputeProfitAsync(seller, now.AddDays(-90), DateTime.MaxValue, ct);
        var marginPct = counted > 0 && revenue > 0 ? (revenue - cost) / revenue * 100m : (decimal?)null;
        var profitPerOrder = marginPct is { } mp ? avgOrder * mp / 100m : (decimal?)null;

        var back = attempts == 0 ? "—" : $"{returnedAfterGap} / {attempts} ({Pct((decimal)returnedAfterGap / attempts * 100m)})";
        var net = profitPerOrder is { } p ? $"\nHar wapas aane wale order par munafa taqreeban {Formatters.Money(Math.Round(p))}; {Formatters.Money(discount)} chhoot ke baad {Formatters.Money(Math.Round(p - discount))}" +
                                          (p - discount <= 0 ? " ⚠️ yani is chhoot par har wapas aane wale se nuqsan hoga." : ".")
                                        : "\nMunafa maloom nahi (products ki cost likhein, jaise: \"Kurti - 1800, cost 1200\").";
        await ReplyAsync(seller,
            $"🎟️ Wapsi ki offer ka hisaab\n\n" +
            $"{count} customers ko {Formatters.Money(discount)} ki chhoot: chhoot sirf unhi par lagegi jo order karenge; agar sab {count} aa jayen to zyada se zyada {Formatters.Money(discount * count)}.\n" +
            $"{silentDays}+ din se khamosh customers abhi: {silentNow.Count}; unka pichla average order {Formatters.Money(Math.Round(avgOrder))}." + net + "\n" +
            $"Pichle data mein {silentDays}+ din ki khamoshi ke baad wapas aane wale (bina kisi offer ke): {back}.\n\n" +
            "Yeh hisaab hai, andaza nahi: bot nahi jaan sakta ke chhoot se kitne zyada log wapas aayenge.", ct);
        SuggestNext("inactive customers", "margin");
    }

    /// <summary>"Sara ki kharidari ka pattern": how often she orders, in which week of the month, her usual items and when she is due next.</summary>
    private async Task InsightBuyingPatternAsync(Seller seller, string text, CancellationToken ct)
    {
        var named = await CustomersInTextAsync(seller, text, ct);
        if (named.Count == 0)
        {
            await ReplyAsync(seller, "Barae meherbani customer ka naam likhein, jaise: \"Sara ki kharidari ka pattern\". Customers dekhne ke liye \"customers\" likhein.", ct);
            return;
        }
        if (named.Count > 1)
        {
            await ReplyAsync(seller, "Kai customers milte hain: " + string.Join(", ", named.Take(5).Select(c => c.Name)) + ".\nBarae meherbani poora naam likhein.", ct);
            return;
        }

        var customer = named[0];
        var orders = await _db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.CustomerId == customer.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .OrderBy(o => o.CreatedAt).ToListAsync(ct);
        if (orders.Count < 2)
        {
            await ReplyAsync(seller, $"{customer.Name} ke {orders.Count} order hain — pattern ke liye kam az kam 2 chahiye.", ct);
            return;
        }

        var dates = orders.Select(o => o.CreatedAt).ToList();
        var gaps = dates.Zip(dates.Skip(1), (a, b) => (b - a).TotalDays).ToList();
        var avgGap = gaps.Average();
        var weeks = orders.Select(o => (SellerLocal(seller, o.CreatedAt).Day - 1) / 7 + 1).GroupBy(w => w).Select(g => (Week: g.Key, Count: g.Count())).OrderByDescending(w => w.Count).ThenBy(w => w.Week).ToList();
        var items = orders.SelectMany(o => o.Items).GroupBy(i => i.ProductNameSnapshot, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Qty: g.Sum(i => i.Quantity))).OrderByDescending(x => x.Qty).Take(3).ToList();
        var next = dates[^1].AddDays(avgGap);
        var overdue = next < DateTime.UtcNow ? $" (is se {(int)(DateTime.UtcNow - next).TotalDays} din zyada ho chuke)" : "";
        await ReplyAsync(seller,
            $"🛍️ {customer.Name} ki kharidari ka pattern\n\n" +
            $"Orders: {orders.Count} · kul {Formatters.Money(orders.Sum(o => o.Total))} · har order ki average {Formatters.Money(Math.Round(orders.Average(o => o.Total)))}\n" +
            $"Aam taur par har ~{Math.Max(1, (int)Math.Round(avgGap))} din baad order\n" +
            $"Mahine ka sab se zyada hafta: {Formatters.Ordinal(weeks[0].Week)} ({weeks[0].Count} orders)" + (weeks.Count > 1 ? $", phir {Formatters.Ordinal(weeks[1].Week)} ({weeks[1].Count})" : "") + "\n" +
            $"Aksar leti hain: {string.Join(", ", items.Select(i => $"{i.Name} ({i.Qty})"))}\n" +
            $"Aakhri order: {ShortDate(seller, dates[^1])} → agla order ~{ShortDate(seller, next)} tak{overdue}\n\n" +
            "Yeh sirf pichle orders ka dohrao hai, pakki peshgoi nahi.", ct);
        SuggestNext($"order for {customer.Name}", "customers");
    }

    // ---------- pricing

    /// <summary>"Lawn suit ki cost 300 barh gayi, nayi price kya ho": the price that keeps the same margin %, and the one that keeps the same profit per piece.</summary>
    private async Task InsightCostRiseAsync(Seller seller, string text, decimal rise, CancellationToken ct)
    {
        var named = await ProductsInTextAsync(seller, text, ct);
        if (named.Count == 0)
        {
            await ReplyAsync(seller, "Barae meherbani product ka naam bhi likhein, jaise: \"lawn suit ki cost 300 barh gayi, nayi price kya ho\". Products dekhne ke liye \"catalog\" likhein.", ct);
            return;
        }
        var p = named[0];
        if (rise <= 0)
        {
            await ReplyAsync(seller, "Barae meherbani likhein cost kitni barhi, jaise: \"lawn suit ki cost 300 barh gayi\".", ct);
            return;
        }
        if (p.CostPrice is not { } cost || p.Price <= 0)
        {
            await ReplyAsync(seller, $"{Formatters.ProductLabel(p)} ki purani cost save nahi, is liye hisaab nahi ho sakta. Cost likhein, jaise: \"{p.Name} - {p.Price:0}, cost 1200\".", ct);
            return;
        }

        var newCost = cost + rise;
        var margin = (p.Price - cost) / p.Price;
        static decimal Up10(decimal v) => Math.Ceiling(v / 10m) * 10m;
        var sameMargin = margin < 1m ? Up10(newCost / (1m - margin)) : 0m;
        var sameProfit = Up10(p.Price + rise);
        await ReplyAsync(seller,
            $"🏷️ {Formatters.ProductLabel(p)} — nayi cost {Formatters.Money(newCost)} (pehle {Formatters.Money(cost)})\n\n" +
            $"Abhi ki price {Formatters.Money(p.Price)}, margin {Pct(margin * 100m)}, fi piece munafa {Formatters.Money(p.Price - cost)}\n" +
            $"• Margin {Pct(margin * 100m)} wohi rakhna ho: price {Formatters.Money(sameMargin)}\n" +
            $"• Fi piece munafa {Formatters.Money(p.Price - cost)} wohi rakhna ho: price {Formatters.Money(sameProfit)}\n" +
            $"• Price na badlein to munafa {Formatters.Money(p.Price - newCost)} reh jata hai" + (p.Price - newCost <= 0 ? " ⚠️ yani nuqsan" : "") +
            $"\n\nBadalne ke liye: edit product: {p.Name} - {sameMargin:0}, cost {newCost:0}", ct);
        SuggestNext("margin", "profit");
    }

    /// <summary>"Polo shirt 500 se 550 kar dun to": the profit per piece before and after, and how far sales can fall before profit does — buyers' reaction is not guessed.</summary>
    private async Task InsightPriceChangeAsync(Seller seller, string text, decimal newPrice, CancellationToken ct)
    {
        var named = await ProductsInTextAsync(seller, text, ct);
        if (named.Count == 0)
        {
            await ReplyAsync(seller, "Barae meherbani product ka naam bhi likhein, jaise: \"polo shirt 500 se 550 kar dun to kya asar parega\".", ct);
            return;
        }
        var p = named[0];
        var oldPrice = p.Price;
        if (newPrice <= 0 || oldPrice <= 0 || newPrice == oldPrice)
        {
            await ReplyAsync(seller, $"{Formatters.ProductLabel(p)} ki abhi price {Formatters.Money(oldPrice)} hai. Nayi price likhein, jaise: \"{p.Name} {oldPrice:0} se {oldPrice * 1.1m:0} kar dun to\".", ct);
            return;
        }
        if (p.CostPrice is not { } cost)
        {
            await ReplyAsync(seller, $"{Formatters.ProductLabel(p)}: {Formatters.Money(oldPrice)} → {Formatters.Money(newPrice)} ({(newPrice > oldPrice ? "+" : "-")}{Pct(Math.Abs(newPrice - oldPrice) / oldPrice * 100m)}).\nMunafa nikalne ke liye cost chahiye: \"{p.Name} - {oldPrice:0}, cost 300\" likhein.", ct);
            return;
        }

        var oldProfit = oldPrice - cost;
        var newProfit = newPrice - cost;
        var sold = (await SoldUnitsAsync(seller, new[] { p.Id }, ct)).GetValueOrDefault(p.Id).Last30;
        string tail;
        if (newProfit <= 0) tail = "⚠️ Nayi price par cost bhi poori nahi hoti.";
        else if (newPrice > oldPrice)
        {
            var allowedDrop = (1m - oldProfit / newProfit) * 100m;
            tail = oldProfit <= 0
                ? "Pehle nuqsan tha, ab munafa hoga."
                : $"Nayi price par bikri {Pct(allowedDrop)} tak gir jaye tab bhi kul munafa pehle jitna rahega" + (sold > 0 ? $" (pichle 30 din {sold} bike → {Math.Floor(sold * (1m - allowedDrop / 100m))} bikna kafi)." : ".");
        }
        else
        {
            var needed = oldProfit <= 0 ? 0m : (newProfit / oldProfit);
            tail = newProfit > 0 ? $"Pehle jitna kul munafa lene ke liye bikri {Num(oldProfit / newProfit)} guna chahiye" + (sold > 0 ? $" (pichle 30 din {sold} bike → {Math.Ceiling(sold * oldProfit / newProfit)})." : ".") : "";
            _ = needed;
        }
        await ReplyAsync(seller,
            $"🏷️ {Formatters.ProductLabel(p)}: {Formatters.Money(oldPrice)} → {Formatters.Money(newPrice)} ({(newPrice > oldPrice ? "+" : "-")}{Pct(Math.Abs(newPrice - oldPrice) / oldPrice * 100m)})\n\n" +
            $"Cost {Formatters.Money(cost)}\nFi piece munafa: {Formatters.Money(oldProfit)} → {Formatters.Money(newProfit)}\nMargin: {Pct(oldProfit / oldPrice * 100m)} → {Pct(newProfit / newPrice * 100m)}\n\n{tail}\n\n" +
            "Gahak kitna kam karenge — yeh bot nahi jaanta; sirf itna bata sakta hai ke kitni kami tak aap nuqsan mein nahi jate.", ct);
        SuggestNext("margin", "profit");
    }

    /// <summary>"Kaun sa product mehnga hai magar margin sab se kam": costly products ranked by thin margin.</summary>
    private async Task InsightCostlyLowMarginAsync(Seller seller, CancellationToken ct)
    {
        var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive && p.CostPrice != null && p.Price > 0).ToListAsync(ct);
        if (products.Count < 2)
        {
            await ReplyAsync(seller, "Muqabla ke liye kam az kam 2 products ki cost chahiye. Cost likhein, jaise: \"Kurti - 1800, cost 1200\".", ct);
            return;
        }
        var costs = products.Select(p => p.CostPrice!.Value).OrderBy(c => c).ToList();
        var medianCost = costs[costs.Count / 2];
        var rows = products.Where(p => p.CostPrice!.Value >= medianCost)
            .Select(p => (Product: p, Margin: (p.Price - p.CostPrice!.Value) / p.Price * 100m))
            .OrderBy(r => r.Margin).ThenByDescending(r => r.Product.CostPrice).Take(5).ToList();
        var lines = rows.Select((r, i) => $"{i + 1}. {Formatters.ProductLabel(r.Product)} — cost {Formatters.Money(r.Product.CostPrice!.Value)}, price {Formatters.Money(r.Product.Price)}, margin {Pct(r.Margin)}");
        await ReplyAsync(seller, $"💸 Mehngi cost magar kam margin wale products\n\n{string.Join("\n", lines)}\n\nYeh un products mein se hain jin ki cost beech wali cost ({Formatters.Money(medianCost)}) ya us se zyada hai; sab se patla margin pehle.", ct);
        SuggestNext("price review", "margin");
    }

    /// <summary>"Kis supplier ka maal zyada bika": products carry a "supplier: Name" attribute (saved with the product line); sales and profit are grouped by it.</summary>
    private async Task InsightSuppliersAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "90d");
        var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id).ToListAsync(ct);
        string? SupplierOf(Product p)
        {
            if (string.IsNullOrWhiteSpace(p.AttributesJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(p.AttributesJson);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (prop.Name.Equals("supplier", StringComparison.OrdinalIgnoreCase) || prop.Name.Equals("vendor", StringComparison.OrdinalIgnoreCase))
                        return prop.Value.ToString().Trim() is { Length: > 0 } v ? v : null;
            }
            catch (JsonException) { }
            return null;
        }
        var supplier = products.ToDictionary(p => p.Id, SupplierOf);
        if (supplier.Values.All(v => v is null))
        {
            await ReplyAsync(seller,
                "Abhi kisi product par supplier ka naam save nahi, is liye supplier ke hisab se hisaab nahi ho sakta.\n" +
                "Product ke saath likhein, jaise: \"Lawn Suit - 3500, cost 2500, supplier: Shah Alam\" — phir yeh sawal dobara poochein.", ct);
            return;
        }

        var items = await _db.OrderItems.AsNoTracking()
            .Where(i => i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= start && i.Order.CreatedAt < end && i.ProductId != null
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { ProductId = i.ProductId!.Value, i.Quantity, i.UnitPrice, i.UnitCost })
            .ToListAsync(ct);
        var costOf = products.ToDictionary(p => p.Id, p => p.CostPrice);
        var groups = items.GroupBy(i => supplier.GetValueOrDefault(i.ProductId) ?? "Supplier nahi likha")
            .Select(g =>
            {
                var known = g.Where(i => (i.UnitCost ?? costOf.GetValueOrDefault(i.ProductId)) != null).ToList();
                return (Name: g.Key, Units: g.Sum(i => i.Quantity), Sales: g.Sum(i => i.UnitPrice * i.Quantity),
                    Profit: known.Sum(i => (i.UnitPrice - (i.UnitCost ?? costOf[i.ProductId])!.Value) * i.Quantity), Known: known.Count, All: g.Count());
            })
            .OrderBy(g => g.Name == "Supplier nahi likha" ? 1 : 0).ThenByDescending(g => g.Units).ToList();
        if (groups.Count == 0)
        {
            await ReplyAsync(seller, $"🏭 Supplier ke hisab se ({label}): is dauran koi order nahi.", ct);
            return;
        }
        var lines = groups.Select((g, i) => $"{i + 1}. {g.Name} — {g.Units} piece bike · sale {Formatters.Money(g.Sales)} · " +
                                              (g.Known == 0 ? "munafa maloom nahi (cost nahi)" : $"munafa {Formatters.Money(g.Profit)}" + (g.Known < g.All ? " (sirf cost wale items)" : "")));
        await ReplyAsync(seller, $"🏭 Supplier ke hisab se — {label}\n\n{string.Join("\n", lines)}\n\nSale = price × quantity (discount aur delivery se pehle).", ct);
        SuggestNext("margin", "trending products");
    }
}
