using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Strategic questions, part B: parcels stuck in transit, city comparisons, the share of profit couriers take, paid-then-cancelled audit, discount effects, cash needed by the 5th,
// settlement time by payment method, advance buyers' order size and the cash really lost to cancellations, returns and wastage.
public partial class ConversationEngine
{
    private async Task<bool> TryHandleStrategicInsight2Async(Seller seller, ParsedCommand cmd, string? period, string text, CancellationToken ct)
    {
        switch (cmd.Text)
        {
            case "rtotransit": await InsightReturnTransitAsync(seller, period, ct); break;
            case "citycompare": await InsightCityCompareAsync(seller, text, period, ct); break;
            case "rtoprofitshare": await InsightReturnShareOfProfitAsync(seller, (int)(cmd.Amount ?? 90), ct); break;
            case "paidcancelled": await InsightPaidThenCancelledAsync(seller, (int)(cmd.Amount ?? 90), ct); break;
            case "discountbasket": await InsightDiscountBasketAsync(seller, period, ct); break;
            case "discountreturn": await InsightDiscountReturnRateAsync(seller, period, ct); break;
            case "cashneed": await InsightCashNeededAsync(seller, ct); break;
            case "settlement": await InsightSettlementAsync(seller, period, ct); break;
            case "advancesize": await InsightAdvanceOrderSizeAsync(seller, period, ct); break;
            case "leakage": await InsightLeakageAsync(seller, (int)(cmd.Amount ?? 30), ct); break;
            default: return false;
        }
        return true;
    }

    private static bool WasDispatched(OrderStatus status, DateTime? shippedAt) =>
        shippedAt != null || status is OrderStatus.Shipped or OrderStatus.Delivered or OrderStatus.Returned;

    // ---------- couriers and cities

    /// <summary>Days from "shipped" to "returned" per courier, and parcels still shown as shipped after 10 days.</summary>
    private async Task InsightReturnTransitAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var now = DateTime.UtcNow;
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end && o.TrackingCourier != null && (o.Status == OrderStatus.Returned || o.Status == OrderStatus.Shipped))
            .Select(o => new { Courier = o.TrackingCourier!, o.Status, o.ShippedAt, o.ReturnedAt, o.UpdatedAt })
            .ToListAsync(ct);
        var groups = rows.GroupBy(r => r.Courier.Trim(), StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var back = g.Where(r => r.Status == OrderStatus.Returned && r.ShippedAt != null).Select(r => ((r.ReturnedAt ?? r.UpdatedAt) - r.ShippedAt!.Value).TotalDays).Where(d => d >= 0).ToList();
            var stuck = g.Count(r => r.Status == OrderStatus.Shipped && r.ShippedAt != null && (now - r.ShippedAt.Value).TotalDays > 10);
            return (Name: g.Key, Returned: back.Count, Avg: back.Count == 0 ? (double?)null : back.Average(), Max: back.Count == 0 ? 0 : back.Max(), Stuck: stuck);
        }).Where(g => g.Returned > 0 || g.Stuck > 0).OrderByDescending(g => g.Avg ?? 0).ToList();
        if (groups.Count == 0)
        {
            await ReplyAsync(seller, $"📦 Return ke raaste ka waqt ({label}): courier wale returned ya 10 din se zyada purane shipped parcel nahi mile. (\"mark N shipped\" aur \"mark N returned\" likhne se waqt banta hai.)", ct);
            return;
        }
        var lines = groups.Select((g, i) => $"{i + 1}. {g.Name} — " + (g.Avg is { } a ? $"return aane mein average {Num((decimal)a)} din (sab se zyada {Num((decimal)g.Max)}; {g.Returned} parcels)" : "koi returned parcel nahi") + (g.Stuck > 0 ? $" · {g.Stuck} parcel 10 din se \"shipped\" hi hain" : ""));
        await ReplyAsync(seller,
            $"📦 Return ka maal raaste mein kitne din — {label}\n\n{string.Join("\n", lines)}\n\nWaqt \"shipped\" likhne se \"returned\" likhne tak ka hai (aap jab likhte hain). Jab tak parcel \"returned\" mark nahi hota, uska stock wapas nahi chadhta.", ct);
        SuggestNext("courier returns", "return rate");
    }

    /// <summary>Delivery days and returned share for the cities named (or the busiest); the bot cannot tell a refusal from other reasons for a return.</summary>
    private async Task InsightCityCompareAsync(Seller seller, string text, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end && o.Customer != null && o.Customer.City != null)
            .Select(o => new { City = o.Customer!.City!, o.Status, o.CreatedAt, o.ShippedAt, o.DeliveredAt })
            .ToListAsync(ct);
        var dispatched = rows.Where(r => !string.IsNullOrWhiteSpace(r.City) && WasDispatched(r.Status, r.ShippedAt)).ToList();
        if (dispatched.Count == 0)
        {
            await ReplyAsync(seller, $"🚚 Shehron ka muqabla ({label}): shehar wale bheje hue orders nahi mile. Shehar likhne ke liye: \"Sara ki city Karachi\".", ct);
            return;
        }

        var cities = dispatched.Select(r => r.City.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var named = cities.Where(c => HasWord(text, c)).ToList();
        var shown = named.Count > 0 ? named : cities.OrderByDescending(c => dispatched.Count(r => r.City.Trim().Equals(c, StringComparison.OrdinalIgnoreCase))).Take(4).ToList();
        var lines = shown.Select(c =>
        {
            var g = dispatched.Where(r => r.City.Trim().Equals(c, StringComparison.OrdinalIgnoreCase)).ToList();
            var days = g.Where(r => r.DeliveredAt != null).Select(r => (r.DeliveredAt!.Value - (r.ShippedAt ?? r.CreatedAt)).TotalDays).ToList();
            var back = g.Count(r => r.Status == OrderStatus.Returned);
            return $"• {c}: {g.Count} parcels · " + (days.Count > 0 ? $"delivery average {Num((decimal)days.Average())} din" : "delivery ka waqt maloom nahi") + $" · wapas {back} ({Pct((decimal)back / g.Count * 100m)})";
        });
        await ReplyAsync(seller,
            $"🚚 Shehron ka muqabla — {label}\n\n{string.Join("\n", lines)}\n\n" +
            "Wapas = status \"returned\". Gaahak ne parcel lene se inkar kiya ya address ghalat tha — bot yeh farq nahi karta, is liye \"refusal rate\" nahi balkay wapsi ki shurat dikhai gayi hai." +
            (named.Count == 0 ? "\n(Shehar ka naam jaisa customer par likha hai waisa likhein, jaise: \"Karachi aur Islamabad ka delivery time\".)" : ""), ct);
        SuggestNext("city wise orders", "delivery time");
    }

    /// <summary>Courier cost on returned / shipped-then-cancelled parcels (written fee, else the delivery charge) as a share of the profit of the same period.</summary>
    private async Task InsightReturnShareOfProfitAsync(Seller seller, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 7, 730);
        var start = DateTime.UtcNow.AddDays(-days);
        var parcels = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && (o.Status == OrderStatus.Returned || (o.Status == OrderStatus.Cancelled && o.ShippedAt != null)))
            .Select(o => new { o.CourierFee, o.DeliveryCharge })
            .ToListAsync(ct);
        var (_, counted, revenue, cost) = await ComputeProfitAsync(seller, start, DateTime.MaxValue, ct);
        if (parcels.Count == 0)
        {
            await ReplyAsync(seller, $"✅ Pichle {days} din mein koi parcel wapas nahi aaya (ya bhej kar cancel nahi hua).", ct);
            return;
        }

        var written = parcels.Count(p => p.CourierFee != null);
        var spent = parcels.Sum(p => p.CourierFee ?? p.DeliveryCharge);
        var profit = revenue - cost;
        var share = counted > 0 && profit > 0 ? Pct(spent / profit * 100m) : null;
        await ReplyAsync(seller,
            $"📉 Wapas aaye parcels aur courier — pichle {days} din\n\n" +
            $"Wapas / bhej kar cancel hue parcels: {parcels.Count}\n" +
            $"Courier par gaya: {Formatters.Money(spent)}" + (written == parcels.Count ? " (likhi hui fee)" : written > 0 ? $" ({written} ki likhi fee, baqi {parcels.Count - written} ki delivery charge)" : " (fee likhi nahi, order par likhi delivery charge)") + "\n" +
            (share is not null ? $"Is muddat ka munafa: {Formatters.Money(profit)} → courier ka hissa {share}\n" : "Munafa nikalne ke liye products ki cost chahiye (\"Kurti - 1800, cost 1200\").\n") +
            "\nWapsi ka kiraya alag save nahi hota — sirf wo jo \"courier fee 12 250\" mein likha ho (ya delivery charge).", ct);
        SuggestNext("courier fee report", "return rate");
    }

    // ---------- audit

    /// <summary>Orders marked paid that were later cancelled or returned, and whether a matching cash-out was written in the drawer.</summary>
    private async Task InsightPaidThenCancelledAsync(Seller seller, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 7, 365);
        var start = DateTime.UtcNow.AddDays(-days);
        var orders = await _db.Orders.AsNoTracking().Include(o => o.Customer)
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && (o.Status == OrderStatus.Cancelled || o.Status == OrderStatus.Returned) && (o.PaidAt != null || o.AmountPaid > 0))
            .OrderByDescending(o => o.UpdatedAt).ToListAsync(ct);
        if (orders.Count == 0)
        {
            await ReplyAsync(seller, $"✅ Pichle {days} din mein koi paid (ya advance wala) order cancel / return nahi hua.", ct);
            return;
        }

        var outs = await _db.CashMovements.AsNoTracking().Where(m => m.SellerId == seller.Id && m.Kind == CashMovementKind.Out && m.CreatedAt >= start).ToListAsync(ct);
        var lines = orders.Take(10).Select(o =>
        {
            var received = OrderMoney.Received(o) is > 0 and var r ? r : o.AmountPaid;
            var refund = outs.FirstOrDefault(m => m.Amount >= received - 1m && m.Amount <= o.Total + 1m && m.CreatedAt >= o.UpdatedAt.AddDays(-1) && m.CreatedAt <= o.UpdatedAt.AddDays(7));
            return $"• #{o.Id} {o.Customer?.Name} — {Formatters.Status(o.Status)}, mila hua {Formatters.Money(received)} → " + (refund is not null ? $"drawer mein cash out mila ({Formatters.Money(refund.Amount)})" : "⚠️ koi cash out nahi mila");
        }).ToList();
        var open = lines.Count(l => l.Contains("⚠️"));
        await ReplyAsync(seller,
            $"🔎 Paid ke baad cancel/return — pichle {days} din ({orders.Count} orders)\n\n{string.Join("\n", lines)}\n\n" +
            (open > 0 ? $"{open} order(s) mein wapsi ka cash out nazar nahi aaya — dekh lein ke paisa customer ko lauta diya ya drawer mein hai. " : "Sab ke saath cash out likha hua hai. ") +
            "Cash out ko \"order se jorna\" bot nahi karta: raqam (mila hua ya order ka total) aur 7 din ke andar likhi cash out se milaya gaya hai.", ct);
        SuggestNext("drawer history", "cash out");
    }

    // ---------- discounts

    /// <summary>Per category: average order total with a discount against without (3+ orders each); the order's category is its biggest line.</summary>
    private async Task InsightDiscountBasketAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var orders = await ActiveOrdersIn(seller, start, end).Select(o => new { o.Id, o.Total, o.DiscountAmount }).ToListAsync(ct);
        if (orders.Count == 0)
        {
            await ReplyAsync(seller, $"🎟️ Discount aur order ka size ({label}): is dauran koi order nahi.", ct);
            return;
        }
        var ids = orders.Select(o => o.Id).ToList();
        var lines = await _db.OrderItems.AsNoTracking().Where(i => ids.Contains(i.OrderId)).Select(i => new { i.OrderId, i.ProductId, Value = i.UnitPrice * i.Quantity }).ToListAsync(ct);
        var categories = (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id).Select(p => new { p.Id, p.Category }).ToListAsync(ct)).ToDictionary(p => p.Id, p => string.IsNullOrWhiteSpace(p.Category) ? NoCategory : p.Category!.Trim());
        var categoryOf = lines.GroupBy(l => l.OrderId).ToDictionary(g => g.Key, g =>
        {
            var top = g.OrderByDescending(l => l.Value).First();
            return top.ProductId is { } id && categories.TryGetValue(id, out var c) ? c : NoCategory;
        });
        var rows = orders.Select(o => (Category: categoryOf.GetValueOrDefault(o.Id, NoCategory), o.Total, Discounted: o.DiscountAmount > 0)).ToList();
        var result = rows.GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase).Select(g =>
            {
                var withD = g.Where(r => r.Discounted).ToList();
                var without = g.Where(r => !r.Discounted).ToList();
                return (Name: g.Key, With: withD, Without: without);
            })
            .Where(x => x.With.Count >= 3 && x.Without.Count >= 3)
            .Select(x => (x.Name, WithAvg: x.With.Average(r => r.Total), WithoutAvg: x.Without.Average(r => r.Total), WithCount: x.With.Count, WithoutCount: x.Without.Count))
            .OrderByDescending(x => x.WithAvg / x.WithoutAvg).ToList();
        if (result.Count == 0)
        {
            await ReplyAsync(seller, $"🎟️ Discount aur order ka size ({label}): kisi category mein discount wale aur bina discount wale kam az kam 3-3 orders nahi — muqabla nahi ban saka.", ct);
            return;
        }
        var text = result.Take(5).Select((x, i) => $"{i + 1}. {x.Name} — discount wale order ki average {Formatters.Money(Math.Round(x.WithAvg))} ({x.WithCount}) bamuqabla {Formatters.Money(Math.Round(x.WithoutAvg))} ({x.WithoutCount}) → {(x.WithAvg >= x.WithoutAvg ? "+" : "-")}{Pct(Math.Abs(x.WithAvg - x.WithoutAvg) / x.WithoutAvg * 100m)}");
        await ReplyAsync(seller, $"🎟️ Discount ke saath bade order — {label}\n\n{string.Join("\n", text)}\n\nOrder ki category = us ki sab se bari line ki category. Yeh sirf muqabla hai: discount ki wajah se order bara hua, yeh sabit nahi.", ct);
        SuggestNext("discount report", "sales by category");
    }

    private async Task InsightDiscountReturnRateAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end)
            .Select(o => new { o.DiscountAmount, o.Status, o.ShippedAt })
            .ToListAsync(ct);
        var sent = rows.Where(r => WasDispatched(r.Status, r.ShippedAt)).ToList();
        var withD = sent.Where(r => r.DiscountAmount > 0).ToList();
        var without = sent.Where(r => r.DiscountAmount <= 0).ToList();
        if (withD.Count == 0 || without.Count == 0)
        {
            await ReplyAsync(seller, $"↩️ Discount aur return ({label}): muqabla ke liye dono qisam ke bheje gaye orders chahiye — discount wale {withD.Count}, baqi {without.Count}.", ct);
            return;
        }
        var a = withD.Count(r => r.Status == OrderStatus.Returned);
        var b = without.Count(r => r.Status == OrderStatus.Returned);
        var ra = (decimal)a / withD.Count * 100m;
        var rb = (decimal)b / without.Count * 100m;
        await ReplyAsync(seller,
            $"↩️ Discount aur return — {label}\n\nDiscount wale orders: {withD.Count} bheje, {a} wapas → {Pct(ra)}\nBina discount: {without.Count} bheje, {b} wapas → {Pct(rb)}\n" +
            (ra > rb ? "→ discount wale orders zyada wapas aaye." : ra < rb ? "→ discount wale orders kam wapas aaye." : "→ barabar.") +
            (withD.Count < 10 || without.Count < 10 ? "\n(Orders kam hain — is farq par bharosa na karein.)" : ""), ct);
        SuggestNext("return rate", "discount report");
    }

    // ---------- cash

    /// <summary>Rent, bills and supplier payments (from expenses) against the days left until the 5th of next month: the daily takings needed, against today's pace.</summary>
    private async Task InsightCashNeededAsync(Seller seller, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var (fixedMonthly, specific) = await FixedMonthlyCostsAsync(seller, ct);
        var suppliers = (await _db.Expenses.AsNoTracking().Where(e => e.SellerId == seller.Id && e.CreatedAt >= now.AddDays(-90)).ToListAsync(ct))
            .Where(e => System.Text.RegularExpressions.Regex.IsMatch(e.Category + " " + e.Note, @"supplier|maal|wholesale|purchase|khareed|kharid|مال|سپلائر", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            .Sum(e => e.Amount) / 3m;
        var rent = specific ? fixedMonthly : 0m;
        if (rent + suppliers <= 0)
        {
            await ReplyAsync(seller, "Pichle 3 mahine mein kiraya, bijli ya supplier ki payment ke kharche darj nahi. Likhein, jaise: \"expense 40000 kiraya\", \"expense 12000 bijli\", \"expense 150000 supplier\".", ct);
            return;
        }

        var today = SellerLocal(seller, now).Date;
        var target = new DateTime(today.Year, today.Month, 5).AddMonths(1);
        var daysLeft = Math.Max(1, (int)(target - today).TotalDays);
        var need = rent + suppliers;
        var perDay = need / daysLeft;
        var received = (await _db.Orders.AsNoTracking().Where(o => o.SellerId == seller.Id && o.PaidAt != null && o.PaidAt >= now.AddDays(-30) && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .Select(o => new { o.Total, o.AmountPaid, o.PaymentStatus }).ToListAsync(ct)).Sum(o => o.PaymentStatus == PaymentStatus.Paid ? o.Total : Math.Min(o.AmountPaid, o.Total)) / 30m;
        await ReplyAsync(seller,
            $"💵 Agle mahine ki 5 tareekh ({target:d MMM}) tak — {daysLeft} din baqi\n\n" +
            (rent > 0 ? $"Kiraya / bijli / bill (mahine ki average): {Formatters.Money(Math.Round(rent))}\n" : "") +
            (suppliers > 0 ? $"Supplier ki payment (mahine ki average): {Formatters.Money(Math.Round(suppliers))}\n" : "") +
            $"Kul zaroorat: {Formatters.Money(Math.Round(need))} → roz kam az kam {Formatters.Money(Math.Round(perDay))} wasooli\n" +
            $"Abhi roz ki wasooli (pichle 30 din, paid orders): {Formatters.Money(Math.Round(received))} → " + (received >= perDay ? "✅ kaafi hai" : $"⚠️ roz ~{Formatters.Money(Math.Round(perDay - received))} kam") + "\n\n" +
            "Yeh wasooli (paisa haath mein) ka hisaab hai, munafa nahi; hisaab aap ke likhe hue kharchon par hai." + (rent == 0 ? "\nKiraya/bijli ke kharche alag se likhe nahi, is liye sirf supplier ki payment ginti hai." : ""), ct);
        SuggestNext("expenses", "cash flow");
    }

    /// <summary>Days from order to the money arriving, by payment method (wallet/bank, COD cash, courier payout).</summary>
    private async Task InsightSettlementAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end && o.PaidAt != null && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .Select(o => new { o.PaymentMethod, o.CreatedAt, o.DeliveredAt, PaidAt = o.PaidAt!.Value, o.PayoutId })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            await ReplyAsync(seller, $"⏱️ Paisa kitne din mein mila ({label}): koi paid order nahi.", ct);
            return;
        }

        string Part(string name, IEnumerable<double> days)
        {
            var list = days.Where(d => d >= 0).ToList();
            return list.Count == 0 ? $"• {name}: koi order nahi" : $"• {name}: order se average {Num((decimal)list.Average())} din (sab se zyada {Num((decimal)list.Max())}) — {list.Count} orders";
        }
        var advance = rows.Where(r => r.PaymentMethod != OrderPaymentMethod.Cod).Select(r => (r.PaidAt - r.CreatedAt).TotalDays);
        var cod = rows.Where(r => r.PaymentMethod == OrderPaymentMethod.Cod && r.PayoutId == null).Select(r => (r.PaidAt - (r.DeliveredAt ?? r.CreatedAt)).TotalDays);
        var payout = rows.Where(r => r.PaymentMethod == OrderPaymentMethod.Cod && r.PayoutId != null).Select(r => (r.PaidAt - (r.DeliveredAt ?? r.CreatedAt)).TotalDays);
        await ReplyAsync(seller,
            $"⏱️ Paisa kitne din mein haath aaya — {label}\n\n{Part("Advance / wallet / bank (order se paid mark tak)", advance)}\n{Part("COD cash (delivery se paid mark tak)", cod)}\n{Part("COD courier payout ke zariye (delivery se payout tak)", payout)}\n\n" +
            "Waqt wo hai jab aap ne \"paid\" ya payout likha; bank mein asal paisa kab aaya, yeh bot nahi dekhta.", ct);
        SuggestNext("payout pending", "cod pending");
    }

    private async Task InsightAdvanceOrderSizeAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "180d");
        var rows = await ActiveOrdersIn(seller, start, end).Select(o => new { o.PaymentMethod, o.Total }).ToListAsync(ct);
        var advance = rows.Where(r => r.PaymentMethod != OrderPaymentMethod.Cod).ToList();
        var cod = rows.Where(r => r.PaymentMethod == OrderPaymentMethod.Cod).ToList();
        if (advance.Count == 0 || cod.Count == 0)
        {
            await ReplyAsync(seller, $"💳 Advance aur order ka size ({label}): dono qisam ke orders chahiye — advance/wallet {advance.Count}, COD {cod.Count}.", ct);
            return;
        }
        var a = advance.Average(r => r.Total);
        var c = cod.Average(r => r.Total);
        await ReplyAsync(seller,
            $"💳 Advance bamuqabla COD — {label}\n\nAdvance / wallet / bank: {advance.Count} orders · har order ki average {Formatters.Money(Math.Round(a))}\nCOD / cash: {cod.Count} orders · har order ki average {Formatters.Money(Math.Round(c))}\n" +
            $"→ advance wale orders {(a >= c ? "bade" : "chhote")} hain ({Pct(Math.Abs(a - c) / c * 100m)})", ct);
        SuggestNext("payment split", "average sale");
    }

    /// <summary>Cash really lost to cancellations, returns and wastage: courier cost on parcels that came back, plus the cost of goods written off; cancelled-before-shipping orders only lose the sale.</summary>
    private async Task InsightLeakageAsync(Seller seller, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 7, 365);
        var start = DateTime.UtcNow.AddDays(-days);
        var orders = await _db.Orders.AsNoTracking().Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && (o.Status == OrderStatus.Cancelled || o.Status == OrderStatus.Returned))
            .Select(o => new { o.Status, o.Total, o.DeliveryCharge, o.CourierFee, o.ShippedAt }).ToListAsync(ct);
        var losses = await _db.Losses.AsNoTracking().Where(l => l.SellerId == seller.Id && l.CreatedAt >= start).ToListAsync(ct);
        if (orders.Count == 0 && losses.Count == 0)
        {
            await ReplyAsync(seller, $"✅ Pichle {days} din mein koi cancel, return ya kharab maal nahi.", ct);
            return;
        }

        var early = orders.Where(o => o.Status == OrderStatus.Cancelled && o.ShippedAt == null).ToList();
        var parcels = orders.Where(o => o.Status == OrderStatus.Returned || (o.Status == OrderStatus.Cancelled && o.ShippedAt != null)).ToList();
        var courier = parcels.Sum(o => o.CourierFee ?? o.DeliveryCharge);
        var wasted = losses.Sum(l => l.Quantity * l.UnitCost);
        await ReplyAsync(seller,
            $"🕳️ Nuqsan ka hisaab — pichle {days} din\n\n" +
            $"Bhejne se pehle cancel: {early.Count} orders ({Formatters.Money(early.Sum(o => o.Total))} ki sale nahi hui, paisa ka nuqsan nahi)\n" +
            $"Wapas aaye / bhej kar cancel: {parcels.Count} orders ({Formatters.Money(parcels.Sum(o => o.Total))} ki sale) — courier par gaya {Formatters.Money(courier)}" + (parcels.Any(o => o.CourierFee == null) ? " (jahan fee likhi nahi wahan delivery charge)" : "") + "\n" +
            $"Kharab / gum maal: {losses.Sum(l => l.Quantity)} piece, cost {Formatters.Money(wasted)}\n\n" +
            $"Pakka naqd nuqsan: {Formatters.Money(courier + wasted)} (courier + kharab maal)\n" +
            "Wapas aaya maal agar theek ho to stock mein wapas chadh jata hai, is liye uski cost nuqsan nahi ginti.", ct);
        SuggestNext("return rate", "courier fee report");
    }
}
