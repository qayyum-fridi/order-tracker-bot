using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Business-analysis questions (InsightParser picks the topic and period from the seller's own words): which product is dearest, average daily sales, which customers stopped
// coming, how much stock lasts, where the money goes... Every figure is plain SQL / arithmetic on saved data; nothing here asks the AI to compute or guess a number.
// This file: sales, products, margin and customers. ConversationEngine.Insights2.cs: payments, returns, delivery, stock and expenses.
public partial class ConversationEngine
{
    /// <summary>Below this margin a product is flagged for a price review; at or above <see cref="GoodMarginPercent"/> it is called healthy. A guide, said as such in the reply.</summary>
    private const decimal LowMarginPercent = 20m;
    private const decimal GoodMarginPercent = 30m;

    private async Task HandleInsightAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, CancellationToken ct)
    {
        var period = string.IsNullOrEmpty(cmd.Text2) ? null : cmd.Text2;
        var text = cmd.Text3 ?? "";
        switch (cmd.Text)
        {
            case "avg": await InsightAverageSaleAsync(seller, period, ct); break;
            case "pricey": await InsightPriceRankAsync(seller, descending: true, ct); break;
            case "cheap": await InsightPriceRankAsync(seller, descending: false, ct); break;
            case "priceup": await InsightPriceReviewAsync(seller, text, ct); break;
            case "whatif": await InsightDiscountWhatIfAsync(seller, text, cmd.Amount ?? 0, ct); break;
            case "discgiven": await InsightDiscountGivenAsync(seller, period, ct); break;
            case "courierfees": await InsightCourierFeesAsync(seller, period, ct); break;
            case "inactive": await InsightInactiveCustomersAsync(seller, ctx, (int)(cmd.Amount ?? 30), ct); break;
            case "topcustomers": await InsightTopCustomersAsync(seller, ctx, period, ct); break;
            case "newcustomers": await InsightNewCustomersAsync(seller, ctx, period, ct); break;
            case "repeat": await InsightRepeatCustomersAsync(seller, ctx, period, ct); break;
            case "balance": await InsightCustomerBalanceAsync(seller, text, ct); break;
            case "productcompare": await InsightProductCompareAsync(seller, text, ct); break;
            case "lastbought": await InsightLastBoughtAsync(seller, text, ct); break;
            case "paysplit": await InsightPaymentSplitAsync(seller, period, ct); break;
            case "area": await InsightAreaOrdersAsync(seller, text, period, ct); break;
            case "returns": await InsightReturnRateAsync(seller, period, ct); break;
            case "deliverytime": await InsightDeliveryTimeAsync(seller, text, period, ct); break;
            case "stockending": await InsightStockEndingAsync(seller, (int)(cmd.Amount ?? 7), ct); break;
            case "stocklevel": await InsightStockLevelAsync(seller, ctx, cmd, text, ct); break;
            case "vsprev": await InsightVersusPreviousAsync(seller, period ?? "month", ct); break;
            case "stockdays": await InsightStockDaysAsync(seller, text, ct); break;
            case "reorder": await InsightReorderAsync(seller, text, ctx.SupplierLeadDays, ct); break;
            case "leadtime": await InsightSetLeadTimeAsync(seller, ctx, (int)(cmd.Amount ?? 0), ct); break;
            case "catproducts": await InsightCategoryProductsAsync(seller, ct); break;
            case "categories": await InsightCategoriesAsync(seller, ct); break;
            case "untracked": await InsightUntrackedStockAsync(seller, ct); break;
            case "expcat": await InsightExpenseCategoryAsync(seller, text, period, ct); break;
            case "exptop": await InsightTopExpensesAsync(seller, period, ct); break;
            case "vsyesterday": await InsightTodayVsYesterdayAsync(seller, ct); break;
            case "pnl": await InsightProfitOrLossAsync(seller, period, ct); break;
            default:
                if (!await TryHandleAdvancedInsightAsync(seller, ctx, cmd, period, text, ct))
                    await ReplyAsync(seller, "Maaf kijiye, yeh sawal samajh nahi aaya. Madad ke liye \"menu\" likhein.", ct);
                break;
        }
    }

    // ---------- shared helpers

    /// <summary>The report window for a period key (or <paramref name="defaultKey"/>); "all" = since the beginning.</summary>
    private (DateTime Start, DateTime End, string Label) InsightWindow(Seller seller, string? period, string defaultKey)
    {
        var key = period ?? defaultKey;
        if (key == "all") return (DateTime.MinValue, DateTime.MaxValue, "ab tak");
        var (start, end) = ReportRange(seller, key);
        var label = Regex.Match(key, @"^(\d{1,4})d$") is { Success: true } days ? $"pichle {days.Groups[1].Value} din" : PeriodLabel(seller, key).ToLowerInvariant();
        return (start, end, label);
    }

    private IQueryable<Order> ActiveOrdersIn(Seller seller, DateTime start, DateTime end) =>
        _db.Orders.AsNoTracking().Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end
                                             && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned);

    private static DateTime SellerLocal(Seller seller, DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), SellerClock.Resolve(seller.TimeZoneId));

    private static string ShortDate(Seller seller, DateTime utc) => SellerLocal(seller, utc).ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    private static string Pct(decimal value) => Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Num(decimal value) => Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);

    private static bool HasWord(string text, string name) =>
        Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(name.Trim()) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Words of the questions themselves: never read as a misspelt product name ("stock" is one letter from "sock").
    private static readonly HashSet<string> GenericQuestionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "stock", "maal", "order", "orders", "sale", "sales", "product", "products", "item", "items", "price", "rate", "margin", "profit", "discount", "kharcha", "expense",
        "kitna", "kitne", "kitni", "bacha", "bachi", "bache", "batch", "naya", "report", "total", "track", "today", "month", "week", "last", "this", "customer", "customers",
        "bikta", "bikti", "bikte", "chalega", "chalegi", "roz", "daily", "average", "sasta", "mehnga", "mehngi", "zyada", "sakta", "hoon", "hain", "kaun", "kaisa", "kaisi"
    };

    /// <summary>Active catalog products named in the seller's sentence (whole words, longest name wins; a near miss of a one- or two-word chunk counts when nothing matches exactly).</summary>
    private async Task<List<Product>> ProductsInTextAsync(Seller seller, string text, CancellationToken ct)
    {
        var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive).OrderBy(p => p.Id).ToListAsync(ct);
        var hits = products.Where(p => p.Name.Trim().Length >= 2 && HasWord(text, p.Name)).ToList();
        hits = hits.Where(h => !hits.Any(o => o.Name.Length > h.Name.Length && o.Name.Contains(h.Name, StringComparison.OrdinalIgnoreCase))).ToList();

        // Words not already part of an exact match may still be a misspelt product ("dupatte" for "Dupatta").
        var covered = hits.SelectMany(h => h.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Select(w => w.ToLowerInvariant()).ToHashSet();
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('?', '.', ',', '!', '؟', '،')).Where(t => t.Length > 0).ToList();
        var free = tokens.Select(t => covered.Contains(t.ToLowerInvariant()) ? null : t).ToList();
        var chunks = new List<string>();
        for (var i = 0; i < free.Count; i++)
        {
            if (free[i] is null) continue;
            chunks.Add(free[i]!);
            if (i + 1 < free.Count && free[i + 1] is not null) chunks.Add(free[i] + " " + free[i + 1]);
        }
        var names = products.Select(p => p.Name.ToLowerInvariant()).Distinct().ToList();
        foreach (var chunk in chunks.Where(c => c.Length >= 4 && !GenericQuestionWords.Contains(c)))
            if (TextDistance.Nearest(chunk.ToLowerInvariant(), names, 2, 1).FirstOrDefault() is { } near)
                foreach (var p in products.Where(p => p.Name.Equals(near, StringComparison.OrdinalIgnoreCase) && hits.All(h => h.Id != p.Id)))
                    hits.Add(p);
        return hits;
    }

    /// <summary>Saved customers named in the sentence: the full name, or a first / last name of 3+ letters as a whole word.</summary>
    private async Task<List<Customer>> CustomersInTextAsync(Seller seller, string text, CancellationToken ct)
    {
        var customers = await _db.Customers.AsNoTracking().Where(c => c.SellerId == seller.Id && c.DeletedAt == null).ToListAsync(ct);
        var full = customers.Where(c => c.Name.Trim().Length >= 3 && HasWord(text, c.Name)).ToList();
        if (full.Count > 0) return full;
        return customers.Where(c => c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part => part.Length >= 3 && HasWord(text, part))).ToList();
    }

    private async Task<Dictionary<int, (int Total, int Last30)>> SoldUnitsAsync(Seller seller, IEnumerable<int> productIds, CancellationToken ct)
    {
        var ids = productIds.ToList();
        if (ids.Count == 0) return new Dictionary<int, (int, int)>();
        var since = SellerClock.LocalDayRangeUtc(seller.TimeZoneId, DateTime.UtcNow, -29).StartUtc;
        var rows = await _db.OrderItems.AsNoTracking()
            .Where(i => i.ProductId != null && ids.Contains(i.ProductId.Value) && i.Order!.SellerId == seller.Id
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { ProductId = i.ProductId!.Value, i.Quantity, i.Order!.CreatedAt })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => (g.Sum(r => r.Quantity), g.Where(r => r.CreatedAt >= since).Sum(r => r.Quantity)));
    }

    // ---------- sales and products

    private async Task InsightAverageSaleAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "30d");
        var orders = await ActiveOrdersIn(seller, start, end).Select(o => new { o.CreatedAt, o.Total }).ToListAsync(ct);
        if (orders.Count == 0)
        {
            await ReplyAsync(seller, $"📊 Average sale ({label}): is dauran koi order nahi.", ct);
            return;
        }

        var now = DateTime.UtcNow;
        var first = start == DateTime.MinValue ? orders.Min(o => o.CreatedAt) : start;
        var days = Math.Max(1, (int)Math.Ceiling(((end > now ? now : end) - first).TotalDays));
        var total = orders.Sum(o => o.Total);
        var perDay = orders.GroupBy(o => SellerLocal(seller, o.CreatedAt).Date).Select(g => (Day: g.Key, Sales: g.Sum(o => o.Total))).ToList();
        var best = perDay.OrderByDescending(d => d.Sales).First();
        var quiet = Math.Max(0, days - perDay.Count);

        await ReplyAsync(seller,
            $"📊 Average sale — {label}\n\n" +
            $"Total sale: {Formatters.Money(total)} ({orders.Count} orders, {days} din)\n" +
            $"Roz ki average: {Formatters.Money(Math.Round(total / days))}\n" +
            $"Har order ki average: {Formatters.Money(Math.Round(total / orders.Count))}\n" +
            $"Sab se achha din: {best.Day.ToString("d MMM", CultureInfo.InvariantCulture)} — {Formatters.Money(best.Sales)}\n" +
            $"Bina sale ke din: {quiet}\n\n" +
            "Sale mein delivery shamil hai; cancel aur return shamil nahi.", ct);
        SuggestNext("today's summary", "profit");
    }

    private async Task InsightPriceRankAsync(Seller seller, bool descending, CancellationToken ct)
    {
        var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive).ToListAsync(ct);
        if (products.Count == 0)
        {
            await ReplyAsync(seller, "Aapke catalog mein abhi koi product nahi. Pehle product likhein, jaise: Kurti - 1800", ct);
            return;
        }

        var ranked = (descending ? products.OrderByDescending(p => p.Price) : products.OrderBy(p => p.Price)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        var sold = await SoldUnitsAsync(seller, ranked.Select(p => p.Id), ct);
        var lines = ranked.Select((p, i) =>
        {
            var s = sold.GetValueOrDefault(p.Id);
            return $"{i + 1}. {Formatters.ProductLabel(p)} — {Formatters.Money(p.Price)} · " +
                   (s.Total == 0 ? "abhi tak bika nahi" : $"ab tak {s.Total} bika (pichle 30 din: {s.Last30})");
        });
        await ReplyAsync(seller, $"{(descending ? "💎 Sab se mehnge products" : "🏷️ Sab se saste products")}\n\n{string.Join("\n", lines)}", ct);
        SuggestNext("margin", "trending products");
    }

    /// <summary>"Dupatta ki qeemat barhani chahiye?": that product's margin against the guide levels, or every low-margin product when none is named.</summary>
    private async Task InsightPriceReviewAsync(Seller seller, string text, CancellationToken ct)
    {
        var named = await ProductsInTextAsync(seller, text, ct);
        if (named.Count > 0)
        {
            var sold = await SoldUnitsAsync(seller, named.Select(p => p.Id), ct);
            var blocks = named.Take(3).Select(p => PriceReviewBlock(p, sold.GetValueOrDefault(p.Id).Last30));
            await ReplyAsync(seller, "🏷️ Qeemat ka jaiza\n\n" + string.Join("\n\n", blocks) + "\n\nYeh sirf andaza hai: qeemat barhane se bikri par asar par sakta hai. Delivery aur kharcha shamil nahi.", ct);
            SuggestNext("margin", "profit");
            return;
        }

        var products = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive).ToListAsync(ct);
        var withCost = products.Where(p => p.CostPrice is not null && p.Price > 0).ToList();
        if (withCost.Count == 0)
        {
            await ReplyAsync(seller, "Kisi product ki cost save nahi, is liye margin ka hisaab nahi ho sakta. Cost ke saath likhein, jaise: \"Kurti - 1800, cost 1200\".", ct);
            return;
        }

        var low = withCost.Select(p => (Product: p, Margin: (p.Price - p.CostPrice!.Value) / p.Price * 100m)).Where(r => r.Margin < LowMarginPercent).OrderBy(r => r.Margin).Take(5).ToList();
        var noCost = products.Count - withCost.Count;
        var missing = noCost > 0 ? $"\n\nℹ️ {noCost} product(s) ki cost save nahi, wo is jaiza mein nahi." : "";
        if (low.Count == 0)
        {
            await ReplyAsync(seller, $"✅ Kisi product ka margin {Pct(LowMarginPercent)} se kam nahi — qeemat barhana zaroori nazar nahi aata.{missing}", ct);
            return;
        }
        var sold30 = await SoldUnitsAsync(seller, low.Select(r => r.Product.Id), ct);
        await ReplyAsync(seller,
            $"🏷️ In products ka margin {Pct(LowMarginPercent)} se kam hai — qeemat par ghor karein:\n\n" +
            string.Join("\n\n", low.Select(r => PriceReviewBlock(r.Product, sold30.GetValueOrDefault(r.Product.Id).Last30))) + missing, ct);
        SuggestNext("margin", "profit");
    }

    private static string PriceReviewBlock(Product p, int sold30)
    {
        if (p.CostPrice is null || p.Price <= 0)
            return $"• {Formatters.ProductLabel(p)}: cost save nahi. Likhein: \"{p.Name} - {Formatters.Money(p.Price).Replace("Rs.", "")}, cost 1200\" (apni cost ke saath).";
        var margin = (p.Price - p.CostPrice.Value) / p.Price * 100m;
        var verdict = margin <= 0 ? "⚠️ cost se kam ya barabar par bik raha hai — qeemat barhana zaroori hai"
            : margin < LowMarginPercent ? "⚠️ margin kam hai — qeemat barhane par ghor karein"
            : margin < GoodMarginPercent ? "theek hai — barhana zaroori nahi"
            : "✅ margin achha hai — zaroorat nahi";
        var target = Math.Ceiling(p.CostPrice.Value / (1m - GoodMarginPercent / 100m) / 10m) * 10m;
        var suggest = margin < GoodMarginPercent && target > p.Price
            ? $"\n   {Formatters.Money(target)} par margin {Pct(GoodMarginPercent)} hoga (badalne ke liye: edit product: {p.Name} - {target:0})"
            : "";
        return $"• {Formatters.ProductLabel(p)}: {Formatters.Money(p.Price)}, cost {Formatters.Money(p.CostPrice.Value)}, margin {Pct(margin)} · 30 din: {sold30} bike\n   {verdict}{suggest}";
    }

    /// <summary>"Lawn suit par 10% discount dun to margin kitna bachega": new price, margin and the extra sales needed to earn the same profit.</summary>
    private async Task InsightDiscountWhatIfAsync(Seller seller, string text, decimal percent, CancellationToken ct)
    {
        if (percent is <= 0 or >= 100)
        {
            await ReplyAsync(seller, "Maaf kijiye, discount 1 se 99 percent ke darmiyan likhein, jaise: margin Lawn Suit 10%", ct);
            return;
        }

        var named = await ProductsInTextAsync(seller, text, ct);
        if (named.Count == 0)
        {
            await ReplyAsync(seller, "Barae meherbani product ka naam bhi likhein, jaise: \"margin Lawn Suit 10%\". Products dekhne ke liye \"catalog\" likhein.", ct);
            return;
        }

        var lines = new List<string>();
        foreach (var p in named.Take(3))
        {
            if (p.CostPrice is null || p.Price <= 0)
            {
                lines.Add($"• {Formatters.ProductLabel(p)}: cost save nahi. Cost likhein, jaise: \"{p.Name} - {p.Price:0}, cost 1200\".");
                continue;
            }
            var newPrice = Math.Round(p.Price * (1m - percent / 100m), 2);
            var oldProfit = p.Price - p.CostPrice.Value;
            var newProfit = newPrice - p.CostPrice.Value;
            var oldMargin = oldProfit / p.Price * 100m;
            var newMargin = newPrice > 0 ? newProfit / newPrice * 100m : 0m;
            var head = $"• {Formatters.ProductLabel(p)}: {Formatters.Money(p.Price)} → {Formatters.Money(newPrice)} ({Pct(percent)} discount)\n   margin {Pct(oldMargin)} → {Pct(newMargin)}; fi piece munafa {Formatters.Money(oldProfit)} → {Formatters.Money(newProfit)}";
            if (newProfit <= 0)
                lines.Add(head + $"\n   ⚠️ is discount par cost bhi poori nahi hoti (zyada se zyada {Pct(oldMargin)} tak discount banta hai).");
            else
            {
                var factor = Math.Ceiling(oldProfit / newProfit * 100m) / 100m;
                lines.Add(head + $"\n   pehle jitna munafa lene ke liye taqreeban {Num(factor)} guna zyada bikri chahiye.");
            }
        }
        await ReplyAsync(seller, "🏷️ Discount ka asar\n\n" + string.Join("\n\n", lines) + "\n\nYeh sirf hisaab hai; delivery aur kharcha shamil nahi.", ct);
        SuggestNext("margin", "profit");
    }

    /// <summary>"Pichle mahine kitna discount diya": discount given, how many orders used it, and how those orders compare with the rest.</summary>
    private async Task InsightDiscountGivenAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "30d");
        var orders = await ActiveOrdersIn(seller, start, end).Select(o => new { o.Subtotal, o.DiscountAmount, o.Total, o.DiscountCode }).ToListAsync(ct);
        if (orders.Count == 0)
        {
            await ReplyAsync(seller, $"🎟️ Discount ({label}): is dauran koi order nahi.", ct);
            return;
        }

        var discounted = orders.Where(o => o.DiscountAmount > 0).ToList();
        if (discounted.Count == 0)
        {
            await ReplyAsync(seller, $"🎟️ Discount ({label}): kisi order par discount nahi diya gaya ({orders.Count} orders).", ct);
            return;
        }

        var plain = orders.Where(o => o.DiscountAmount <= 0).ToList();
        var given = discounted.Sum(o => o.DiscountAmount);
        var gross = orders.Sum(o => o.Subtotal);
        var compare = plain.Count > 0
            ? $"\nHar order ki average: discount wale {Formatters.Money(Math.Round(discounted.Average(o => o.Total)))}, baqi {Formatters.Money(Math.Round(plain.Average(o => o.Total)))}"
            : "";
        var top = discounted.Where(o => !string.IsNullOrWhiteSpace(o.DiscountCode))
            .GroupBy(o => o.DiscountCode!.ToUpperInvariant()).Select(g => (Code: g.Key, Given: g.Sum(o => o.DiscountAmount), Orders: g.Count()))
            .OrderByDescending(x => x.Given).Take(3).ToList();
        var codes = top.Count > 0 ? "\nZyada istemal: " + string.Join(", ", top.Select(t => $"{t.Code} ({t.Orders} orders, {Formatters.Money(t.Given)})")) : "";

        await ReplyAsync(seller,
            $"🎟️ Discount — {label}\n\n" +
            $"Diya gaya discount: {Formatters.Money(given)} ({(gross > 0 ? Pct(given / gross * 100m) : "—")} of sales)\n" +
            $"Discount wale orders: {discounted.Count} / {orders.Count}\n" +
            $"Un orders ki sale: {Formatters.Money(discounted.Sum(o => o.Total))}" + compare + codes +
            "\n\nYeh mawazna hai — isse yeh sabit nahi hota ke discount ki wajah se hi bikri barhi. Har code ki tafseel: \"discount report\".", ct);
        SuggestNext("discount report", "margin");
    }

    /// <summary>"Polo shirt ki sale is mahine behtar hai ya kam": the existing product report (this month against the same days of last month) for the product named in the sentence.</summary>
    private async Task InsightProductCompareAsync(Seller seller, string text, CancellationToken ct)
    {
        var named = await ProductsInTextAsync(seller, text, ct);
        if (named.Count == 0)
        {
            await ReplyAsync(seller, "Barae meherbani product ka naam likhein, jaise: \"Polo Shirt ka report\" — is mein is mahine ka pichle mahine se muqabla milta hai. Products dekhne ke liye \"catalog\" likhein.", ct);
            return;
        }
        await HandleProductReportAsync(seller, named[0].Name, ct);
    }

    /// <summary>"Naye customers kitne aaye": customers whose first order falls in the period, against those who came back.</summary>
    private async Task InsightNewCustomersAsync(Seller seller, SessionContextData ctx, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "30d");
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.Customer!.DeletedAt == null)
            .Select(o => new { o.CustomerId, o.Customer!.Name, o.CreatedAt, o.Total })
            .ToListAsync(ct);
        var perCustomer = rows.GroupBy(r => new { r.CustomerId, r.Name })
            .Select(g => (Id: g.Key.CustomerId, Name: g.Key.Name, First: g.Min(r => r.CreatedAt), InWindow: g.Where(r => r.CreatedAt >= start && r.CreatedAt < end).ToList()))
            .Where(c => c.InWindow.Count > 0).ToList();
        if (perCustomer.Count == 0)
        {
            await ReplyAsync(seller, $"👥 Naye customers ({label}): is dauran kisi customer ka order nahi.", ct);
            return;
        }

        var fresh = perCustomer.Where(c => c.First >= start).OrderByDescending(c => c.InWindow.Sum(r => r.Total)).ToList();
        var returning = perCustomer.Count - fresh.Count;
        ctx.LastListCustomerIds = fresh.Take(8).Select(c => c.Id).ToList();
        var lines = fresh.Take(8).Select((c, i) => $"{i + 1}. {c.Name} — {Formatters.Money(c.InWindow.Sum(r => r.Total))}");
        await ReplyAsync(seller,
            $"👥 Customers — {label}\n\nNaye customers (pehla order is dauran): {fresh.Count}\nPurane customers jo wapas aaye: {returning}\n" +
            (fresh.Count > 0 ? $"\n{string.Join("\n", lines)}" + (fresh.Count > 8 ? $"\n…aur {fresh.Count - 8}" : "") : ""), ct);
        SuggestNext("customers", "loyal customers");
    }

    /// <summary>"Is hafte ki sale pichle hafte se kaisi hai": the period so far against the same stretch of the previous one, and the previous one in full.</summary>
    private async Task InsightVersusPreviousAsync(Seller seller, string unit, CancellationToken ct)
    {
        var week = unit == "week";
        var now = DateTime.UtcNow;
        var current = ReportPeriods.Resolve(week ? "thisweek" : "thismonth", seller.TimeZoneId, now)!;
        var previous = ReportPeriods.Resolve(week ? "lastweek" : "lastmonth", seller.TimeZoneId, now)!;
        var elapsed = now - current.StartUtc;
        var sameEnd = previous.StartUtc + elapsed > previous.EndUtc ? previous.EndUtc : previous.StartUtc + elapsed;

        var now2 = await ActiveOrdersIn(seller, current.StartUtc, DateTime.MaxValue).Select(o => o.Total).ToListAsync(ct);
        var prevFull = await ActiveOrdersIn(seller, previous.StartUtc, previous.EndUtc).Select(o => new { o.CreatedAt, o.Total }).ToListAsync(ct);
        var prevSame = prevFull.Where(o => o.CreatedAt < sameEnd).ToList();

        var name = week ? "hafta" : "mahina";
        var nowTotal = now2.Sum();
        var sameTotal = prevSame.Sum(o => o.Total);
        var verdict = sameTotal == 0 ? (nowTotal == 0 ? "➖ barabar" : "📈 pichle " + name + " is waqt tak koi sale nahi thi")
            : nowTotal > sameTotal ? $"📈 {Pct((nowTotal - sameTotal) / sameTotal * 100m)} zyada"
            : nowTotal < sameTotal ? $"📉 {Pct((sameTotal - nowTotal) / sameTotal * 100m)} kam"
            : "➖ barabar";
        await ReplyAsync(seller,
            $"📊 Is {name} bamuqabla pichle {name}\n\n" +
            $"Is {name} ab tak: {Formatters.Money(nowTotal)} ({now2.Count} orders)\n" +
            $"Pichle {name} isi waqt tak: {Formatters.Money(sameTotal)} ({prevSame.Count} orders) → {verdict}\n" +
            $"Pichla {name} poora: {Formatters.Money(prevFull.Sum(o => o.Total))} ({prevFull.Count} orders)\n\n" +
            "Sale mein delivery shamil hai; cancel aur return shamil nahi.", ct);
        SuggestNext("aaj ka hisaab", "profit");
    }

    // ---------- customers

    /// <summary>Customers who ordered before but not in the last N days, biggest spenders first.</summary>
    private async Task InsightInactiveCustomersAsync(Seller seller, SessionContextData ctx, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 730);
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.Customer!.DeletedAt == null)
            .Select(o => new { o.CustomerId, o.Customer!.Name, o.CreatedAt, o.Total })
            .ToListAsync(ct);
        var inactive = rows.GroupBy(r => new { r.CustomerId, r.Name })
            .Select(g => (Id: g.Key.CustomerId, Name: g.Key.Name, Orders: g.Count(), Spent: g.Sum(r => r.Total), Last: g.Max(r => r.CreatedAt)))
            .Where(c => c.Last < cutoff)
            .OrderByDescending(c => c.Spent).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var everyone = rows.Select(r => r.CustomerId).Distinct().Count();

        if (inactive.Count == 0)
        {
            await ReplyAsync(seller, everyone == 0
                ? "Abhi koi customer data nahi hai."
                : $"✅ Pichle {days} din mein har purane customer ne order kiya hai — koi gayab nahi.", ct);
            return;
        }

        var top = inactive.Take(10).ToList();
        ctx.LastListCustomerIds = top.Select(c => c.Id).ToList();
        var lines = top.Select((c, i) => $"{i + 1}. {c.Name} — aakhri order {ShortDate(seller, c.Last)} ({(int)(DateTime.UtcNow - c.Last).TotalDays} din pehle) · {c.Orders} orders · {Formatters.Money(c.Spent)}");
        var more = inactive.Count > top.Count ? $"\n…aur {inactive.Count - top.Count} customers" : "";
        await ReplyAsync(seller,
            $"👥 {inactive.Count} customers (kul {everyone} mein se) ne pichle {days} din se order nahi kiya:\n\n{string.Join("\n", lines)}{more}\n\n" +
            "Sab se zyada kharidari karne wale pehle hain. \"customer 1\" likh kar detail dekhein.", ct);
        SuggestNext("loyal customers", "customers");
    }

    private async Task<List<(Customer Customer, int Orders, decimal Spent, DateTime Last)>> CustomerSpendAsync(Seller seller, DateTime start, DateTime end, CancellationToken ct)
    {
        var orders = await _db.Orders.AsNoTracking().Include(o => o.Customer)
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end
                        && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned && o.Customer != null && o.Customer.DeletedAt == null)
            .ToListAsync(ct);
        return orders.GroupBy(o => o.CustomerId)
            .Select(g => (g.First().Customer!, g.Count(), g.Sum(o => o.Total), g.Max(o => o.CreatedAt)))
            .OrderByDescending(x => x.Item3).ThenBy(x => x.Item1.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task InsightTopCustomersAsync(Seller seller, SessionContextData ctx, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "all");
        var ranked = await CustomerSpendAsync(seller, start, end, ct);
        if (ranked.Count == 0)
        {
            await ReplyAsync(seller, $"👥 Top customers ({label}): is dauran kisi customer ka order nahi.", ct);
            return;
        }
        var total = ranked.Sum(r => r.Spent);
        var top = ranked.Take(5).ToList();
        ctx.LastListCustomerIds = top.Select(r => r.Customer.Id).ToList();
        var lines = top.Select((r, i) => $"{i + 1}. {r.Customer.Name} — {Formatters.Money(r.Spent)} ({(total > 0 ? Pct(r.Spent / total * 100m) : "—")}) · {r.Orders} orders · aakhri {ShortDate(seller, r.Last)}");
        await ReplyAsync(seller, $"🏆 Sab se bade customers — {label}\n\n{string.Join("\n", lines)}\n\nKul {ranked.Count} customers, sale {Formatters.Money(total)}. \"customer 1\" likh kar detail dekhein.", ct);
        SuggestNext("loyal customers", "customers");
    }

    private async Task InsightRepeatCustomersAsync(Seller seller, SessionContextData ctx, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "all");
        var ranked = await CustomerSpendAsync(seller, start, end, ct);
        var repeaters = ranked.Where(r => r.Orders >= 2).OrderByDescending(r => r.Orders).ThenByDescending(r => r.Spent).ToList();
        if (ranked.Count == 0)
        {
            await ReplyAsync(seller, $"👥 Repeat customers ({label}): is dauran kisi customer ka order nahi.", ct);
            return;
        }
        if (repeaters.Count == 0)
        {
            await ReplyAsync(seller, $"👥 Repeat customers ({label}): abhi kisi ne do baar order nahi kiya ({ranked.Count} customers).", ct);
            return;
        }
        var top = repeaters.Take(10).ToList();
        ctx.LastListCustomerIds = top.Select(r => r.Customer.Id).ToList();
        var lines = top.Select((r, i) => $"{i + 1}. {r.Customer.Name} — {r.Orders} orders · {Formatters.Money(r.Spent)}");
        await ReplyAsync(seller,
            $"🔁 Bar bar aane wale customers — {label}\n\n{string.Join("\n", lines)}\n\n" +
            $"{repeaters.Count} / {ranked.Count} customers ({Pct((decimal)repeaters.Count / ranked.Count * 100m)}) ne do ya zyada baar order kiya.", ct);
        SuggestNext("loyal customers", "customers");
    }

    /// <summary>"Bilal ka kitna udhaar hai": the unpaid balance of the named customer, or the biggest balances when no one is named.</summary>
    private async Task InsightCustomerBalanceAsync(Seller seller, string text, CancellationToken ct)
    {
        var named = await CustomersInTextAsync(seller, text, ct);
        var ids = named.Select(c => c.Id).ToList();
        var orders = (await _db.Orders.AsNoTracking().Include(o => o.Customer)
                .Where(o => o.SellerId == seller.Id && o.PaymentStatus != PaymentStatus.Paid && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned
                            && o.Customer != null && o.Customer.DeletedAt == null && (ids.Count == 0 || ids.Contains(o.CustomerId)))
                .OrderBy(o => o.CreatedAt).ToListAsync(ct))
            .Where(o => OrderMoney.Balance(o) > 0).ToList();

        if (orders.Count == 0)
        {
            await ReplyAsync(seller, named.Count > 0
                ? $"✅ {string.Join(", ", named.Take(3).Select(c => c.Name))} ka koi balance baqi nahi."
                : "✅ Kisi customer ka balance baqi nahi.", ct);
            return;
        }

        var groups = orders.GroupBy(o => o.CustomerId).Select(g => (Name: g.First().Customer!.Name, Orders: g.ToList(), Balance: g.Sum(OrderMoney.Balance)))
            .OrderByDescending(g => g.Balance).ToList();
        if (named.Count > 0)
        {
            var blocks = groups.Take(3).Select(g =>
                $"👤 {g.Name} — baqi {Formatters.Money(g.Balance)}\n" +
                string.Join("\n", g.Orders.Take(8).Select(o => $"   #{o.Id} · {ShortDate(seller, o.CreatedAt)} · {Formatters.Money(OrderMoney.Balance(o))}")));
            await ReplyAsync(seller, "💳 Baqi raqam\n\n" + string.Join("\n\n", blocks) + "\n\nPayment mil jaye to: \"order 12 paid\" ya \"order 12 advance 500\".", ct);
        }
        else
        {
            var lines = groups.Take(10).Select((g, i) => $"{i + 1}. {g.Name} — {Formatters.Money(g.Balance)} ({g.Orders.Count} orders)");
            await ReplyAsync(seller, $"💳 Baqi raqam (sab customers: {Formatters.Money(groups.Sum(g => g.Balance))})\n\n{string.Join("\n", lines)}", ct);
        }
        SuggestNext("unpaid", "cod pending");
    }

    /// <summary>"Sara ne pichli baar kab aur kya kharida tha": the customer's latest order.</summary>
    private async Task InsightLastBoughtAsync(Seller seller, string text, CancellationToken ct)
    {
        var named = await CustomersInTextAsync(seller, text, ct);
        if (named.Count == 0)
        {
            await ReplyAsync(seller, "Maaf kijiye, customer ka naam nahi mila. Barae meherbani naam likhein, jaise: \"Sara ka order\". Customers dekhne ke liye \"customers\" likhein.", ct);
            return;
        }
        if (named.Count > 1)
        {
            await ReplyAsync(seller, "Kai customers milte hain: " + string.Join(", ", named.Take(5).Select(c => c.Name)) + ".\nBarae meherbani poora naam likhein.", ct);
            return;
        }

        var customer = named[0];
        var orders = await _db.Orders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.SellerId == seller.Id && o.CustomerId == customer.Id && o.Status != OrderStatus.Cancelled)
            .OrderByDescending(o => o.CreatedAt).ToListAsync(ct);
        if (orders.Count == 0)
        {
            await ReplyAsync(seller, $"{customer.Name} ka abhi koi order nahi.", ct);
            return;
        }
        var last = orders[0];
        await ReplyAsync(seller,
            $"🛍️ {customer.Name} ka aakhri order\n\n" +
            $"#{last.Id} · {ShortDate(seller, last.CreatedAt)} ({Formatters.DaysAgo(last.CreatedAt)})\n" +
            $"{Formatters.ItemsSummary(last)}\n" +
            $"Total: {Formatters.Money(last.Total)} · {Formatters.Status(last.Status)}\n\n" +
            $"Kul orders: {orders.Count}. Poori tafseel: \"order {last.Id}\".", ct);
        SuggestNext($"order for {customer.Name}", $"order {last.Id}");
    }
}
