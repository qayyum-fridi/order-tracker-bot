using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Business-analysis questions, part 2 (see ConversationEngine.Insights.cs): payments, areas, returns, delivery time, stock run-rate, expenses, today vs yesterday, profit or loss.
public partial class ConversationEngine
{
    // ---------- payments, areas, returns, delivery

    private async Task InsightPaymentSplitAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "30d");
        var orders = await ActiveOrdersIn(seller, start, end).Select(o => new { o.PaymentMethod, o.Total }).ToListAsync(ct);
        if (orders.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.001", label), ct);
            return;
        }

        static string Name(OrderPaymentMethod m) => m switch
        {
            OrderPaymentMethod.Cod => "Cash / COD",
            OrderPaymentMethod.Manual => "Bank / wallet (Easypaisa, JazzCash, transfer)",
            OrderPaymentMethod.Gateway => "Online payment",
            _ => "Tareeqa likha nahi"
        };
        var total = orders.Sum(o => o.Total);
        var groups = orders.GroupBy(o => o.PaymentMethod).Select(g => (Name: Name(g.Key), Orders: g.Count(), Amount: g.Sum(o => o.Total)))
            .OrderByDescending(g => g.Orders).ThenByDescending(g => g.Amount).ToList();
        var lines = groups.Select(g => $"• {g.Name}: {g.Orders} orders ({Pct((decimal)g.Orders / orders.Count * 100m)}) · {Formatters.Money(g.Amount)}" + (total > 0 ? $" ({Pct(g.Amount / total * 100m)})" : ""));
        await ReplyAsync(seller,
            MessageTemplates.Get("insights2.002", label, string.Join("\n", lines), groups[0].Name), ct);
        SuggestNext("unpaid", "cod pending");
    }

    /// <summary>"Model Town se kitne orders aaye": orders whose customer's city or address has the place; with no place, orders per city.</summary>
    private async Task InsightAreaOrdersAsync(Seller seller, string place, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "all");
        var rows = await ActiveOrdersIn(seller, start, end)
            .Select(o => new { o.Total, City = o.Customer!.City, Address = o.Customer.Address, o.CustomerId })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.003", label), ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(place))
        {
            var groups = rows.GroupBy(r => string.IsNullOrWhiteSpace(r.City) ? "Shehar nahi likha" : r.City!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => (Name: g.Key, Orders: g.Count(), Sales: g.Sum(r => r.Total), Customers: g.Select(r => r.CustomerId).Distinct().Count()))
                .OrderBy(g => g.Name == "Shehar nahi likha" ? 1 : 0).ThenByDescending(g => g.Orders).ToList();
            var lines = groups.Take(10).Select((g, i) => $"{i + 1}. {g.Name} — {g.Orders} orders · {g.Customers} customers · {Formatters.Money(g.Sales)}");
            await ReplyAsync(seller, MessageTemplates.Get("insights2.004", label, string.Join("\n", lines)), ct);
            return;
        }

        var hits = rows.Where(r => PlaceMatches(place, r.City) || PlaceMatches(place, r.Address)).ToList();
        if (hits.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.005", place, label), ct);
            return;
        }
        await ReplyAsync(seller,
            MessageTemplates.Get("insights2.006", place, label, hits.Count, hits.Select(h => h.CustomerId).Distinct().Count(), Formatters.Money(hits.Sum(h => h.Total))), ct);
        SuggestNext("customers", "city wise orders");
    }

    /// <summary>
    /// Does the saved city / address name the place? Spaces, dashes and case are ignored ("Model Town" = "model-town" = "modeltown"), and one letter off still counts for a
    /// place of 6+ letters ("Modal Town"), two for 10+. Shorter names must match exactly so "Ali" never matches "Alif".
    /// </summary>
    internal static bool PlaceMatches(string place, string? saved)
    {
        if (string.IsNullOrWhiteSpace(saved)) return false;
        static string Norm(string v) => new string(v.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var wanted = Norm(place);
        var text = Norm(saved);
        if (wanted.Length == 0) return false;
        var words = System.Text.RegularExpressions.Regex.Split(saved, @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).Select(Norm).ToList();
        for (var i = 0; i < words.Count; i++)
        {
            var joined = "";
            for (var j = i; j < words.Count && joined.Length < wanted.Length; j++)
            {
                joined += words[j];
                if (joined == wanted) return true;   // whole words: "Ali" is not "Alif"
            }
        }
        if (wanted.Length >= 6 && text.Contains(wanted, StringComparison.Ordinal)) return true;
        var allowed = wanted.Length >= 10 ? 2 : wanted.Length >= 6 ? 1 : 0;
        if (allowed == 0) return false;
        for (var length = wanted.Length - allowed; length <= wanted.Length + allowed; length++)
            for (var start = 0; start + length <= text.Length; start++)
                if (TextDistance.Levenshtein(wanted, text.Substring(start, length)) <= allowed) return true;
        return false;
    }

    private async Task InsightReturnRateAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "30d");
        var orders = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end)
            .Select(o => new { o.Status, o.Total, o.ShippedAt })
            .ToListAsync(ct);
        if (orders.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.007", label), ct);
            return;
        }

        var cancelled = orders.Where(o => o.Status == OrderStatus.Cancelled).ToList();
        var returned = orders.Where(o => o.Status == OrderStatus.Returned).ToList();
        var shippedEver = orders.Count(o => o.ShippedAt != null || o.Status is OrderStatus.Shipped or OrderStatus.Delivered or OrderStatus.Returned);
        var cancelledAfterShip = cancelled.Count(o => o.ShippedAt != null);
        var lost = cancelled.Sum(o => o.Total) + returned.Sum(o => o.Total);

        await ReplyAsync(seller,
            MessageTemplates.Get("insights2.008", label, orders.Count, cancelled.Count, Pct((decimal)cancelled.Count / orders.Count * 100m), (cancelledAfterShip > 0 ? $" — {cancelledAfterShip} bhejne ke baad" : ""), returned.Count, (shippedEver > 0 ? $" ({Pct((decimal)returned.Count / shippedEver * 100m)} bheje gaye {shippedEver} orders mein se)" : ""), Formatters.Money(lost)), ct);
        SuggestNext("orders today", "profit");
    }

    /// <summary>"TCS ka average delivery time": days from "shipped" to "delivered" per courier, for orders the seller marked both ways.</summary>
    private async Task InsightDeliveryTimeAsync(Seller seller, string text, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "90d");
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end && o.DeliveredAt != null
                        && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .Select(o => new { o.TrackingCourier, ShippedAt = o.ShippedAt ?? o.CreatedAt, FromOrder = o.ShippedAt == null, o.DeliveredAt })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            await ReplyAsync(seller,
                MessageTemplates.Get("insights2.009", label), ct);
            return;
        }

        var couriers = rows.Select(r => r.TrackingCourier).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var wanted = couriers.Where(c => HasWord(text, c)).ToList();
        var scope = wanted.Count > 0 ? rows.Where(r => wanted.Contains((r.TrackingCourier ?? "").Trim(), StringComparer.OrdinalIgnoreCase)).ToList() : rows;
        if (scope.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.010", label), ct);
            return;
        }

        static double Days(DateTime from, DateTime to) => Math.Max(0, (to - from).TotalDays);
        var fromOrder = scope.Count(r => r.FromOrder);
        var groups = scope.GroupBy(r => string.IsNullOrWhiteSpace(r.TrackingCourier) ? "Courier likha nahi" : r.TrackingCourier!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Orders: g.Count(), Avg: g.Average(r => Days(r.ShippedAt, r.DeliveredAt!.Value)), Max: g.Max(r => Days(r.ShippedAt, r.DeliveredAt!.Value))))
            .OrderBy(g => g.Avg).ToList();
        var lines = groups.Select(g => $"• {g.Name}: average {Num((decimal)g.Avg)} din ({g.Orders} orders, sab se der {Num((decimal)g.Max)} din)");
        await ReplyAsync(seller,
            MessageTemplates.Get("insights2.011", label, string.Join("\n", lines), (fromOrder > 0 ? $"\nℹ️ {fromOrder} order(s) mein \"shipped\" nahi likha tha, un ka waqt order banne se ginta hai (zyada aa sakta hai)." : "")), ct);
        SuggestNext("orders today", "cod pending");
    }

    // ---------- stock run-rate

    private sealed record StockRow(Product Product, int Sold, int WindowDays, decimal PerDay, decimal? DaysLeft);

    /// <summary>Units sold per day over the last 30 days (or since the product was added, at least a week) and, for tracked products, how many days the stock lasts at that pace.</summary>
    private async Task<List<StockRow>> StockRowsAsync(Seller seller, IReadOnlyCollection<Product> products, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var since = now.AddDays(-30);
        var ids = products.Select(p => p.Id).ToList();
        var rows = await _db.OrderItems.AsNoTracking()
            .Where(i => i.ProductId != null && ids.Contains(i.ProductId.Value) && i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= since
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { ProductId = i.ProductId!.Value, i.Quantity })
            .ToListAsync(ct);
        var sold = rows.GroupBy(r => r.ProductId).ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));
        return products.Select(p =>
        {
            var window = Math.Clamp((int)Math.Ceiling((now - p.CreatedAt).TotalDays), 7, 30);
            var s = sold.GetValueOrDefault(p.Id);
            var perDay = (decimal)s / window;
            decimal? left = p.StockQty is { } stock ? (stock <= 0 ? 0 : perDay > 0 ? stock / perDay : null) : null;
            return new StockRow(p, s, window, perDay, left);
        }).ToList();
    }

    private static string StockRowDetail(StockRow r)
    {
        var p = r.Product;
        var rate = r.Sold == 0 ? $"pichle {r.WindowDays} din mein bika nahi" : $"pichle {r.WindowDays} din mein {r.Sold} bika (roz ~{Num(r.PerDay)})";
        if (p.StockQty is null)
            return $"📦 {Formatters.ProductLabel(p)}\n• {rate}\n• Stock track nahi hota — shuru karne ke liye: stock {p.Name} 20";
        var left = p.StockQty <= 0 ? "stock khatam hai"
            : r.DaysLeft is null ? "bikri nahi, is liye kitne din chalega yeh andaza nahi"
            : $"taqreeban {Num(r.DaysLeft.Value)} din chalega";
        return $"📦 {Formatters.ProductLabel(p)}\n• Stock: {p.StockQty}\n• {rate}\n• {left}";
    }

    private async Task InsightStockEndingAsync(Seller seller, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 365);
        var all = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive).ToListAsync(ct);
        var tracked = all.Where(p => p.StockQty is not null).ToList();
        if (tracked.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.012"), ct);
            return;
        }

        var due = (await StockRowsAsync(seller, tracked, ct)).Where(r => r.Product.StockQty <= 0 || (r.DaysLeft is { } d && d <= days))
            .OrderBy(r => r.DaysLeft ?? 0).ThenBy(r => r.Product.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var untracked = all.Count - tracked.Count;
        var note = untracked > 0 ? $"\n\nℹ️ {untracked} product(s) ka stock track nahi hota, wo is hisaab mein nahi (\"untracked stock\" likhein)." : "";
        if (due.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.013", days, note), ct);
            return;
        }
        var lines = due.Take(10).Select((r, i) => r.Product.StockQty <= 0
            ? $"{i + 1}. {Formatters.ProductLabel(r.Product)} — ⛔ stock khatam"
            : $"{i + 1}. {Formatters.ProductLabel(r.Product)} — {r.Product.StockQty} bacha, ~{Num(r.DaysLeft!.Value)} din (roz ~{Num(r.PerDay)} bik raha)");
        await ReplyAsync(seller, MessageTemplates.Get("insights2.014", days, string.Join("\n", lines), (due[0].WindowDays), note), ct);
        SuggestNext("stock", "catalog");
    }

    /// <summary>"Polo shirt ka stock kitna bacha hai": that product's stock and pace; with no product named, the usual stock list.</summary>
    private async Task InsightStockLevelAsync(Seller seller, SessionContextData ctx, ParsedCommand cmd, string text, CancellationToken ct)
    {
        var named = await ProductsInTextAsync(seller, text, ct);
        if (named.Count == 0)
        {
            await HandleStockAsync(seller, new ParsedCommand { Kind = CommandKind.Stock }, ct);
            return;
        }
        var rows = await StockRowsAsync(seller, named.Take(3).ToList(), ct);
        await ReplyAsync(seller, string.Join("\n\n", rows.Select(StockRowDetail)), ct);
        SuggestNext("stock", "margin");
    }

    private async Task InsightStockDaysAsync(Seller seller, string text, CancellationToken ct)
    {
        var named = await ProductsInTextAsync(seller, text, ct);
        if (named.Count == 0)
        {
            await InsightStockEndingAsync(seller, 14, ct);
            return;
        }
        var rows = await StockRowsAsync(seller, named.Take(3).ToList(), ct);
        await ReplyAsync(seller, MessageTemplates.Get("insights2.015", string.Join("\n\n", rows.Select(StockRowDetail))), ct);
        SuggestNext("stock", "margin");
    }

    /// <summary>"Lawn suit ka naya batch mangwana chahiye": the product's stock days against 7 / 14 days — the supplier's delivery time is the seller's to weigh.</summary>
    private async Task InsightReorderAsync(Seller seller, string text, int? leadDays, CancellationToken ct)
    {
        var named = await ProductsInTextAsync(seller, text, ct);
        if (named.Count == 0)
        {
            await InsightStockEndingAsync(seller, leadDays ?? 7, ct);
            return;
        }
        var rows = await StockRowsAsync(seller, named.Take(3).ToList(), ct);
        var blocks = rows.Select(r =>
        {
            if (leadDays is { } lead && r.Product.StockQty > 0 && r.DaysLeft is { } left)
                return StockRowDetail(r) + "\n→ " + (left <= lead ? $"⚠️ Haan, abhi order karein — stock {Num(left)} din ka hai aur supplier ko {lead} din lagte hain."
                    : left <= lead * 2 ? $"Order ka waqt qareeb hai — stock {Num(left)} din ka hai, supplier ko {lead} din lagte hain."
                    : $"Abhi zaroorat nahi — stock {Num(left)} din ka hai, supplier ko {lead} din lagte hain.");
            var verdict = r.Product.StockQty is null ? "Stock track nahi hota, is liye mashwara nahi de sakta."
                : r.Product.StockQty <= 0 ? "⛔ Stock khatam hai — jald order karein."
                : r.DaysLeft is null ? "Pichle din bikri nahi hui — abhi order ki zaroorat nazar nahi aati."
                : r.DaysLeft <= 7 ? "⚠️ Haan, jald order karein — stock ek hafte se kam ka hai."
                : r.DaysLeft <= 14 ? "Supplier se maal aane mein jitna waqt lagta hai wo dekh kar jald order ka sochein (stock do hafte se kam ka hai)."
                : "Abhi zaroorat nahi — stock do hafte se zyada chalega.";
            return StockRowDetail(r) + "\n→ " + verdict;
        });
        await ReplyAsync(seller, MessageTemplates.Get("insights2.016", string.Join("\n\n", blocks), (leadDays is null ? " Supplier ka waqt batane ke liye likhein: \"maal aane mein 5 din lagte hain\" — phir mashwara us ke hisab se hoga." : "")), ct);
        SuggestNext("stock", "catalog");
    }

    /// <summary>"Maal aane mein 5 din lagte hain": remembered, and used by the re-order advice and the "stock khatam hone wala" list.</summary>
    private async Task InsightSetLeadTimeAsync(Seller seller, SessionContextData ctx, int days, CancellationToken ct)
    {
        if (days is < 1 or > 120)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.017"), ct);
            return;
        }
        ctx.SupplierLeadDays = days;
        await ReplyAsync(seller, MessageTemplates.Get("insights2.018", days), ct);
        SuggestNext("stock", "catalog");
    }

    private async Task InsightCategoriesAsync(Seller seller, CancellationToken ct)
    {
        var all = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive).ToListAsync(ct);
        if (all.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.019"), ct);
            return;
        }
        var groups = all.Where(p => !string.IsNullOrWhiteSpace(p.Category))
            .GroupBy(p => p.Category!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        var without = all.Count(p => string.IsNullOrWhiteSpace(p.Category));
        if (groups.Count == 0)
        {
            await ReplyAsync(seller,
                MessageTemplates.Get("insights2.020", all.Count, Industry(seller).FullProduct), ct);
            return;
        }
        var lines = string.Join("\n", groups.Take(15).Select(g => $"• {g.Key}: {g.Count()} products"));
        var more = groups.Count > 15 ? $"\n…aur {groups.Count - 15}" : "";
        var rest = without > 0 ? $"\n\n{without} products ki category set nahi." : "";
        await ReplyAsync(seller, MessageTemplates.Get("insights2.021", groups.Count, all.Count, lines, more, rest), ct);
        SuggestNext("sales by category", "catalog");
    }

    private async Task InsightCategoryProductsAsync(Seller seller, CancellationToken ct)
    {
        var all = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive).OrderBy(p => p.Name).ToListAsync(ct);
        if (all.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.019"), ct);
            return;
        }
        var sections = all.GroupBy(p => string.IsNullOrWhiteSpace(p.Category) ? "Bina category" : p.Category!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key == "Bina category" ? 1 : 0).ThenByDescending(g => g.Count()).ThenBy(g => g.Key).ToList();
        var sb = new System.Text.StringBuilder($"📂 Category ke hisaab se products ({all.Count}):");
        var shown = 0;
        foreach (var g in sections)
        {
            sb.Append($"\n\n*{g.Key}* ({g.Count()})");
            foreach (var p in g.Take(8)) { sb.Append($"\n• {Formatters.ProductLabel(p)}"); shown++; }
            if (g.Count() > 8) sb.Append($"\n…aur {g.Count() - 8}");
            if (sb.Length > 3000) { sb.Append("\n\n…baqi categories chhoti list ke liye \"catalog\" likhein."); break; }
        }
        if (all.All(p => string.IsNullOrWhiteSpace(p.Category)))
            sb.Append($"\n\nCategory set karne ke liye likhein, jaise: \"{Industry(seller).FullProduct}\".");
        await ReplyAsync(seller, sb.ToString(), ct);
        SuggestNext("sales by category", "catalog");
    }

    private async Task InsightUntrackedStockAsync(Seller seller, CancellationToken ct)
    {
        var all = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive).OrderBy(p => p.Name).ToListAsync(ct);
        if (all.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.019"), ct);
            return;
        }
        var untracked = all.Where(p => p.StockQty is null).ToList();
        if (untracked.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.022", all.Count), ct);
            return;
        }
        var names = string.Join("\n", untracked.Take(10).Select(p => $"• {Formatters.ProductLabel(p)}"));
        var more = untracked.Count > 10 ? $"\n…aur {untracked.Count - 10}" : "";
        await ReplyAsync(seller,
            MessageTemplates.Get("insights2.023", all.Count, untracked.Count, names, more), ct);
        SuggestNext("stock", "catalog");
    }

    // ---------- expenses

    private async Task InsightExpenseCategoryAsync(Seller seller, string category, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "thismonth");
        var expenses = await _db.Expenses.AsNoTracking().Where(e => e.SellerId == seller.Id && e.CreatedAt >= start && e.CreatedAt < end).OrderByDescending(e => e.CreatedAt).ToListAsync(ct);
        var hits = expenses.Where(e => e.Category.Contains(category, StringComparison.OrdinalIgnoreCase) || (e.Note ?? "").Contains(category, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0)
        {
            var known = expenses.Select(e => e.Category).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
            await ReplyAsync(seller, MessageTemplates.Get("insights2.024", category, label, (known.Count > 0 ? $"\nIs dauran ke kharche: {string.Join(", ", known)}." : "")), ct);
            return;
        }
        var lines = hits.Take(3).Select(e => $"• {ShortDate(seller, e.CreatedAt)} — {Formatters.Money(e.Amount)}" + (string.IsNullOrWhiteSpace(e.Note) ? "" : $" ({e.Note})"));
        await ReplyAsync(seller,
            MessageTemplates.Get("insights2.025", category, label, Formatters.Money(hits.Sum(e => e.Amount)), hits.Count, string.Join("\n", lines)), ct);
        SuggestNext("expenses", "monthly net");
    }

    private async Task InsightTopExpensesAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "thismonth");
        var expenses = await _db.Expenses.AsNoTracking().Where(e => e.SellerId == seller.Id && e.CreatedAt >= start && e.CreatedAt < end).ToListAsync(ct);
        if (expenses.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.026", label), ct);
            return;
        }
        var total = expenses.Sum(e => e.Amount);
        var groups = expenses.GroupBy(e => e.Category.Trim().ToLowerInvariant()).Select(g => (Name: g.Key, Amount: g.Sum(e => e.Amount), Count: g.Count()))
            .OrderByDescending(g => g.Amount).ThenBy(g => g.Name, StringComparer.Ordinal).ToList();
        var lines = groups.Take(5).Select((g, i) => $"{i + 1}. {g.Name} — {Formatters.Money(g.Amount)} ({Pct(total > 0 ? g.Amount / total * 100m : 0)}, {g.Count} entries)");
        await ReplyAsync(seller, MessageTemplates.Get("insights2.027", label, string.Join("\n", lines), Formatters.Money(total), groups[0].Name), ct);
        SuggestNext("expenses", "monthly net");
    }

    // ---------- today vs yesterday, profit or loss

    private async Task InsightTodayVsYesterdayAsync(Seller seller, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var todayStart = SellerClock.StartOfLocalDayUtc(seller.TimeZoneId, now);
        var (yStart, yEnd) = SellerClock.LocalDayRangeUtc(seller.TimeZoneId, now, -1);
        var sameTimeEnd = yStart + (now - todayStart);
        if (sameTimeEnd > yEnd) sameTimeEnd = yEnd;

        var today = await ActiveOrdersIn(seller, todayStart, DateTime.MaxValue).Select(o => o.Total).ToListAsync(ct);
        var yFull = await ActiveOrdersIn(seller, yStart, yEnd).Select(o => new { o.CreatedAt, o.Total }).ToListAsync(ct);
        var ySame = yFull.Where(o => o.CreatedAt < sameTimeEnd).ToList();

        static string Change(decimal now, decimal before) =>
            before == 0 ? (now == 0 ? "➖ barabar" : "📈 kal isi waqt tak koi sale nahi thi")
            : now > before ? $"📈 {Pct((now - before) / before * 100m)} zyada"
            : now < before ? $"📉 {Pct((before - now) / before * 100m)} kam"
            : "➖ barabar";

        var todayTotal = today.Sum();
        await ReplyAsync(seller,
            MessageTemplates.Get("insights2.028", Formatters.Money(todayTotal), today.Count, Formatters.Money(ySame.Sum(o => o.Total)), ySame.Count, Change(todayTotal, ySame.Sum(o => o.Total)), Formatters.Money(yFull.Sum(o => o.Total)), yFull.Count), ct);
        SuggestNext("aaj ka hisaab", "orders today");
    }

    /// <summary>"Kya main faide mein hoon ya nuqsan mein": product profit minus the expenses of the same period (this month by default).</summary>
    private async Task InsightProfitOrLossAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "thismonth");
        var (orderCount, counted, revenue, cost) = await ComputeProfitAsync(seller, start, end, ct);
        var expenses = (await _db.Expenses.AsNoTracking().Where(e => e.SellerId == seller.Id && e.CreatedAt >= start && e.CreatedAt < end).Select(e => e.Amount).ToListAsync(ct)).Sum();

        if (orderCount == 0 && expenses == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("insights2.029", label), ct);
            return;
        }
        if (counted == 0 && orderCount > 0)
        {
            await ReplyAsync(seller,
                MessageTemplates.Get("insights2.030", label, orderCount), ct);
            return;
        }

        var gross = revenue - cost;
        var net = gross - expenses;
        var skipped = orderCount - counted;
        await ReplyAsync(seller,
            MessageTemplates.Get("insights2.031", label, Formatters.Money(revenue), Formatters.Money(cost), Formatters.Money(gross), Formatters.Money(expenses), Formatters.Money(net), (net >= 0 ? "✅ Aap faide mein hain." : "⚠️ Aap nuqsan mein hain — kharche munafe se zyada hain."), (skipped > 0 ? $"\nℹ️ {skipped} order(s) shamil nahi (cost maloom nahi), is liye asal munafa is se zyada ho sakta hai." : "")), ct);
        SuggestNext("profit", "monthly net");
    }
}
