using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Harder business questions, part 2: courier returns, return losses, delivery outside the city, cancels by payment method, drawer shortages, money stuck outside,
// expense share of sales, capital locked in stock, overstock, Ramadan / Eid and the category mix. Plain counting and arithmetic on saved data.
public partial class ConversationEngine
{
    private async Task<bool> TryHandleAdvancedInsight2Async(Seller seller, ParsedCommand cmd, string? period, string text, CancellationToken ct)
    {
        switch (cmd.Text)
        {
            case "courierreturns": await InsightCourierReturnsAsync(seller, period, ct); break;
            case "returnloss": await InsightReturnLossAsync(seller, period, ct); break;
            case "citydelivery": await InsightCityDeliveryAsync(seller, period, ct); break;
            case "cancelbymethod": await InsightCancelByMethodAsync(seller, period, ct); break;
            case "drawershort": await InsightDrawerShortageAsync(seller, period, ct); break;
            case "uncollected": await InsightUncollectedAsync(seller, ct); break;
            case "stuckmoney": await InsightStuckMoneyAsync(seller, ct); break;
            case "expenseratio": await InsightExpenseRatioAsync(seller, period, ct); break;
            case "lockedcapital": await InsightLockedCapitalAsync(seller, (int)(cmd.Amount ?? 90), ct); break;
            case "overstock": await InsightOverstockAsync(seller, ct); break;
            case "seasonal": await InsightSeasonAsync(seller, text, ct); break;
            case "categoryshift": await InsightCategoryShiftAsync(seller, text, ct); break;
            default: return false;
        }
        return true;
    }

    // ---------- couriers, returns, delivery

    /// <summary>Which courier's parcels come back most (shipped orders per courier, returned share).</summary>
    private async Task InsightCourierReturnsAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "90d");
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end && o.TrackingCourier != null
                        && (o.ShippedAt != null || o.Status == OrderStatus.Shipped || o.Status == OrderStatus.Delivered || o.Status == OrderStatus.Returned))
            .Select(o => new { o.TrackingCourier, o.Status, o.Total })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            await ReplyAsync(seller, $"📦 Courier ke return ({label}): courier ke saath bheje gaye orders nahi mile. Courier likhne ke liye: \"tracking 12 TCS 12345\".", ct);
            return;
        }

        var groups = rows.GroupBy(r => r.TrackingCourier!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Shipped: g.Count(), Returned: g.Count(r => r.Status == OrderStatus.Returned), Value: g.Where(r => r.Status == OrderStatus.Returned).Sum(r => r.Total)))
            .OrderByDescending(g => (decimal)g.Returned / g.Shipped).ThenByDescending(g => g.Shipped).ToList();
        var lines = groups.Select((g, i) => $"{i + 1}. {g.Name} — {g.Returned} / {g.Shipped} wapas ({Pct((decimal)g.Returned / g.Shipped * 100m)})" + (g.Returned > 0 ? $" · {Formatters.Money(g.Value)} ke orders" : "") + (g.Shipped < 5 ? " (data kam)" : ""));
        await ReplyAsync(seller, $"📦 Courier ke hisab se return — {label}\n\n{string.Join("\n", lines)}\n\nSab se zyada wapas aane wala pehle. \"data kam\" = 5 se kam orders, is par bharosa na karein.", ct);
        SuggestNext("return rate", "orders today");
    }

    /// <summary>Cancelled and returned orders: their value and the delivery charges that were on them.</summary>
    private async Task InsightReturnLossAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "30d");
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end && (o.Status == OrderStatus.Cancelled || o.Status == OrderStatus.Returned))
            .Select(o => new { o.Status, o.Total, o.DeliveryCharge, o.ShippedAt })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            await ReplyAsync(seller, $"✅ Cancel / return ({label}): koi order cancel ya return nahi hua.", ct);
            return;
        }

        var returned = rows.Where(r => r.Status == OrderStatus.Returned).ToList();
        var cancelledAfterShip = rows.Where(r => r.Status == OrderStatus.Cancelled && r.ShippedAt != null).ToList();
        var parcels = returned.Concat(cancelledAfterShip).ToList();
        await ReplyAsync(seller,
            $"↩️ Cancel aur return ka nuqsan — {label}\n\n" +
            $"Cancel: {rows.Count(r => r.Status == OrderStatus.Cancelled)} orders · Return: {returned.Count} orders\n" +
            $"In orders ki kul raqam: {Formatters.Money(rows.Sum(r => r.Total))}\n" +
            $"Wapas aaye ya bhej kar cancel hue parcels: {parcels.Count} — un par likhi delivery charges {Formatters.Money(parcels.Sum(r => r.DeliveryCharge))}\n\n" +
            "Courier ko asal mein kitna diya — yeh bot ke paas save nahi (sirf wo delivery charge jo order par likhi thi). Asal bill ke liye \"expense 300 courier\" likhte rahein.", ct);
        SuggestNext("courier returns", "expenses");
    }

    /// <summary>Delivery time (and returns) for customers inside the seller's own city against other cities.</summary>
    private async Task InsightCityDeliveryAsync(Seller seller, string? period, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(seller.City))
        {
            await ReplyAsync(seller, "Apni dukaan ka shehar save nahi, is liye \"shehar ke andar / baahar\" ka farq nahi nikal sakta. \"update business info\" se shehar likhein.", ct);
            return;
        }

        var (start, end, label) = InsightWindow(seller, period, "180d");
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end && o.Customer != null
                        && (o.ShippedAt != null || o.DeliveredAt != null || o.Status == OrderStatus.Returned))
            .Select(o => new { City = o.Customer!.City, o.CreatedAt, o.ShippedAt, o.DeliveredAt, o.Status })
            .ToListAsync(ct);
        bool Inside(string? city) => !string.IsNullOrWhiteSpace(city) && PlaceMatches(seller.City!, city);
        var known = rows.Where(r => !string.IsNullOrWhiteSpace(r.City)).ToList();
        if (known.Count == 0)
        {
            await ReplyAsync(seller, "Customers ka shehar save nahi, is liye farq nahi nikal sakta. Order ke saath shehar likhein ya \"Sara ka city Lahore\" likhein.", ct);
            return;
        }

        string Part(string name, List<(string? City, DateTime CreatedAt, DateTime? ShippedAt, DateTime? DeliveredAt, OrderStatus Status)> group)
        {
            var delivered = group.Where(g => g.DeliveredAt != null).Select(g => (g.DeliveredAt!.Value - (g.ShippedAt ?? g.CreatedAt)).TotalDays).ToList();
            var returned = group.Count(g => g.Status == OrderStatus.Returned);
            var time = delivered.Count == 0 ? "delivery ka waqt maloom nahi" : $"average {Num((decimal)delivered.Average())} din ({delivered.Count} delivered)";
            return $"• {name}: {group.Count} orders · {time} · return {returned} ({Pct(group.Count == 0 ? 0 : (decimal)returned / group.Count * 100m)})";
        }
        var all = known.Select(r => (r.City, r.CreatedAt, r.ShippedAt, r.DeliveredAt, r.Status)).ToList();
        await ReplyAsync(seller,
            $"🚚 {seller.City} ke andar aur baahar — {label}\n\n{Part(seller.City + " ke andar", all.Where(r => Inside(r.City)).ToList())}\n{Part("Baahar ke shehar", all.Where(r => !Inside(r.City)).ToList())}\n\n" +
            "Payment par asar: bot yeh alag se nahi ginta — \"cancel by payment method\" se COD aur advance ka muqabla dekhein.", ct);
        SuggestNext("delivery time", "city wise orders");
    }

    /// <summary>Cancel rate for cash-on-delivery against advance / bank / wallet orders.</summary>
    private async Task InsightCancelByMethodAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "90d");
        var rows = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end)
            .Select(o => new { o.PaymentMethod, o.Status })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            await ReplyAsync(seller, $"💳 Cancel aur payment ({label}): is dauran koi order nahi.", ct);
            return;
        }

        static string Name(OrderPaymentMethod m) => m switch { OrderPaymentMethod.Cod => "Cash on delivery (COD)", OrderPaymentMethod.Manual => "Bank / wallet (Easypaisa, JazzCash)", OrderPaymentMethod.Gateway => "Online payment", _ => "Tareeqa likha nahi" };
        var groups = rows.GroupBy(r => r.PaymentMethod)
            .Select(g => (Name: Name(g.Key), Total: g.Count(), Cancelled: g.Count(r => r.Status == OrderStatus.Cancelled), Returned: g.Count(r => r.Status == OrderStatus.Returned)))
            .OrderByDescending(g => g.Total).ToList();
        var lines = groups.Select(g => $"• {g.Name}: {g.Total} orders · cancel {g.Cancelled} ({Pct((decimal)g.Cancelled / g.Total * 100m)}) · return {g.Returned} ({Pct((decimal)g.Returned / g.Total * 100m)})");
        await ReplyAsync(seller, $"💳 Cancel aur return — payment ke tareeqe ke hisab se, {label}\n\n{string.Join("\n", lines)}", ct);
        SuggestNext("payment split", "return rate");
    }

    // ---------- cash

    /// <summary>Closed drawers: counted against expected, the total difference and the worst day.</summary>
    private async Task InsightDrawerShortageAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "7d");
        var sessions = await _db.CashDrawerSessions.AsNoTracking()
            .Where(s => s.SellerId == seller.Id && s.ClosedAt != null && s.ClosedAt >= start && s.ClosedAt < end && s.CountedAmount != null && s.ExpectedAmount != null)
            .OrderBy(s => s.ClosedAt).ToListAsync(ct);
        if (sessions.Count == 0)
        {
            await ReplyAsync(seller, $"💵 Drawer ka farq ({label}): is dauran koi drawer band nahi hua. Band karne ke liye: drawer close <gine hue paise>.", ct);
            return;
        }

        var diffs = sessions.Select(s => (Day: s.ClosedAt!.Value, Diff: s.CountedAmount!.Value - s.ExpectedAmount!.Value)).ToList();
        var worst = diffs.OrderBy(d => d.Diff).First();
        var net = diffs.Sum(d => d.Diff);
        var shortTotal = diffs.Where(d => d.Diff < 0).Sum(d => -d.Diff);
        var lines = diffs.Select(d => $"• {ShortDate(seller, d.Day)}: {(d.Diff == 0 ? "barabar" : d.Diff < 0 ? $"{Formatters.Money(-d.Diff)} kam" : $"{Formatters.Money(d.Diff)} zyada")}");
        await ReplyAsync(seller,
            $"💵 Drawer ka farq — {label} ({sessions.Count} din/shift)\n\n{string.Join("\n", lines)}\n\n" +
            $"Kul kami (sirf kam wale): {Formatters.Money(shortTotal)}\nKul farq (kami minus zyada): {(net < 0 ? "-" : "")}{Formatters.Money(Math.Abs(net))}\n" +
            (worst.Diff < 0 ? $"Sab se zyada farq: {ShortDate(seller, worst.Day)} — {Formatters.Money(-worst.Diff)} kam" : "Kisi din kami nahi aayi."), ct);
        SuggestNext("drawer history", "drawer");
    }

    /// <summary>Delivered orders whose money has not been received (COD not marked collected).</summary>
    private async Task InsightUncollectedAsync(Seller seller, CancellationToken ct)
    {
        var orders = (await _db.Orders.AsNoTracking().Include(o => o.Customer)
                .Where(o => o.SellerId == seller.Id && o.Status == OrderStatus.Delivered && o.PaymentStatus != PaymentStatus.Paid && o.PayoutSettledAt == null)
                .OrderBy(o => o.DeliveredAt ?? o.CreatedAt).ToListAsync(ct))
            .Where(o => OrderMoney.Balance(o) > 0).ToList();
        if (orders.Count == 0)
        {
            await ReplyAsync(seller, "✅ Har delivered order ka paisa mil chuka (ya courier payout mein shamil hai).", ct);
            return;
        }

        var now = DateTime.UtcNow;
        var lines = orders.Take(10).Select(o => $"• #{o.Id} {o.Customer?.Name} — {Formatters.Money(OrderMoney.Balance(o))} ({(int)(now - (o.DeliveredAt ?? o.CreatedAt)).TotalDays} din se)");
        var more = orders.Count > 10 ? $"\n…aur {orders.Count - 10} orders" : "";
        await ReplyAsync(seller,
            $"⚠️ Delivered magar paisa nahi mila — {orders.Count} orders, {Formatters.Money(orders.Sum(OrderMoney.Balance))}\n\n{string.Join("\n", lines)}{more}\n\n" +
            "Paisa milne par: \"order 12 paid\". Courier se aana ho to \"payout pending\" dekhein.", ct);
        SuggestNext("cod pending", "payout pending");
    }

    /// <summary>All money still owed on active orders, split by where the order is and how old it is.</summary>
    private async Task InsightStuckMoneyAsync(Seller seller, CancellationToken ct)
    {
        var orders = (await _db.Orders.AsNoTracking()
                .Where(o => o.SellerId == seller.Id && o.PaymentStatus != PaymentStatus.Paid && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
                .ToListAsync(ct))
            .Where(o => OrderMoney.Balance(o) > 0).ToList();
        if (orders.Count == 0)
        {
            await ReplyAsync(seller, "✅ Bazar mein aap ka koi paisa nahi phansa — sab orders ki raqam mil chuki.", ct);
            return;
        }

        var now = DateTime.UtcNow;
        decimal Sum(IEnumerable<Order> list) => list.Sum(OrderMoney.Balance);
        var delivered = orders.Where(o => o.Status == OrderStatus.Delivered).ToList();
        var shipped = orders.Where(o => o.Status == OrderStatus.Shipped).ToList();
        var pending = orders.Where(o => o.Status == OrderStatus.Pending).ToList();
        var old = orders.Where(o => (now - o.CreatedAt).TotalDays > 30).ToList();
        await ReplyAsync(seller,
            $"💰 Bazar mein phansa paisa: {Formatters.Money(Sum(orders))} ({orders.Count} orders)\n\n" +
            $"• Delivered, paisa nahi mila: {Formatters.Money(Sum(delivered))} ({delivered.Count})\n" +
            $"• Raaste mein (shipped): {Formatters.Money(Sum(shipped))} ({shipped.Count})\n" +
            $"• Abhi bheja nahi (pending): {Formatters.Money(Sum(pending))} ({pending.Count})\n" +
            $"• 30 din se purana: {Formatters.Money(Sum(old))} ({old.Count})\n\nUdhaar aur COD donon shamil hain; part payment ka baqi hi ginta hai.", ct);
        SuggestNext("unpaid", "cod pending");
    }

    private async Task InsightExpenseRatioAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = InsightWindow(seller, period, "thismonth");
        var sales = await ActiveOrdersIn(seller, start, end).Select(o => o.Total).ToListAsync(ct);
        var expenses = await _db.Expenses.AsNoTracking().Where(e => e.SellerId == seller.Id && e.CreatedAt >= start && e.CreatedAt < end).Select(e => e.Amount).ToListAsync(ct);
        if (sales.Count == 0)
        {
            await ReplyAsync(seller, $"💸 Kharche aur sale ({label}): is dauran koi sale nahi, is liye fisad nahi nikal sakta. Kharche: {Formatters.Money(expenses.Sum())}.", ct);
            return;
        }

        var ratio = expenses.Sum() / sales.Sum() * 100m;
        string trend = "";
        if (period is null or "thismonth")
        {
            var (ls, le) = SellerClock.PreviousMonthRangeUtc(seller.TimeZoneId, DateTime.UtcNow);
            var lastSales = (await ActiveOrdersIn(seller, ls, le).Select(o => o.Total).ToListAsync(ct)).Sum();
            var lastExp = (await _db.Expenses.AsNoTracking().Where(e => e.SellerId == seller.Id && e.CreatedAt >= ls && e.CreatedAt < le).Select(e => e.Amount).ToListAsync(ct)).Sum();
            if (lastSales > 0)
            {
                var lastRatio = lastExp / lastSales * 100m;
                trend = $"\nPichla mahina: {Pct(lastRatio)} ({Formatters.Money(lastExp)} / {Formatters.Money(lastSales)}) → " + (ratio > lastRatio + 0.5m ? "📈 kharche sale se tezi se barh rahe hain" : ratio < lastRatio - 0.5m ? "📉 kharche qabu mein hain" : "➖ lagbhag barabar");
            }
        }
        await ReplyAsync(seller,
            $"💸 Kharche aur sale — {label}\n\nSale: {Formatters.Money(sales.Sum())}\nKharche: {Formatters.Money(expenses.Sum())}\nKharche = sale ka {Pct(ratio)}{trend}\n\n" +
            "\"Zyada\" kitna hai, yeh aap ke karobar par hai — bot sirf fisad aur pichle mahine se muqabla deta hai.", ct);
        SuggestNext("expense report", "monthly net");
    }

    // ---------- stock capital

    /// <summary>Money tied up in stock that has not sold for N days (tracked products with a saved cost).</summary>
    private async Task InsightLockedCapitalAsync(Seller seller, int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 7, 730);
        var since = DateTime.UtcNow.AddDays(-days);
        var tracked = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive && p.StockQty > 0 && p.CreatedAt < since).ToListAsync(ct);
        if (tracked.Count == 0)
        {
            await ReplyAsync(seller, "Abhi koi aisa product nahi jis ka stock track ho aur jo itne purana ho. Stock shuru karne ke liye: \"stock Kurti 20\".", ct);
            return;
        }

        var ids = tracked.Select(p => p.Id).ToList();
        var soldIds = (await _db.OrderItems.AsNoTracking()
                .Where(i => i.ProductId != null && ids.Contains(i.ProductId.Value) && i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= since
                            && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
                .Select(i => i.ProductId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var stale = tracked.Where(p => !soldIds.Contains(p.Id)).ToList();
        if (stale.Count == 0)
        {
            await ReplyAsync(seller, $"✅ Har track hone wale product ki pichle {days} din mein kam az kam ek baar bikri hui — koi sarmaya band nahi.", ct);
            return;
        }

        var costed = stale.Where(p => p.CostPrice != null).OrderByDescending(p => p.StockQty!.Value * p.CostPrice!.Value).ToList();
        var uncosted = stale.Count - costed.Count;
        var lines = costed.Take(5).Select((p, i) => $"{i + 1}. {Formatters.ProductLabel(p)} — {p.StockQty} piece × {Formatters.Money(p.CostPrice!.Value)} = {Formatters.Money(p.StockQty!.Value * p.CostPrice.Value)}");
        await ReplyAsync(seller,
            $"🔒 Pichle {days} din se na bikne wale stock mein phansa sarmaya: {Formatters.Money(costed.Sum(p => p.StockQty!.Value * p.CostPrice!.Value))} ({costed.Count} products)\n\n{string.Join("\n", lines)}" +
            (uncosted > 0 ? $"\n\nℹ️ {uncosted} aur product(s) bhi nahi bike magar cost save nahi, wo is raqam mein nahi." : ""), ct);
        SuggestNext("overstock", "slow movers");
    }

    /// <summary>Tracked products with 90+ days of stock at the current pace (or none sold), and the discount they can take before the sale loses money.</summary>
    private async Task InsightOverstockAsync(Seller seller, CancellationToken ct)
    {
        var tracked = await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && p.IsActive && p.StockQty > 0).ToListAsync(ct);
        if (tracked.Count == 0)
        {
            await ReplyAsync(seller, "Abhi kisi product ka stock track nahi ho raha. Shuru karne ke liye: \"stock Kurti 20\".", ct);
            return;
        }

        var heavy = (await StockRowsAsync(seller, tracked, ct)).Where(r => r.DaysLeft is null || r.DaysLeft >= 90)
            .OrderByDescending(r => r.Product.StockQty * (r.Product.CostPrice ?? r.Product.Price)).Take(5).ToList();
        if (heavy.Count == 0)
        {
            await ReplyAsync(seller, "✅ Kisi track hone wale product ka stock 90 din se zyada nahi chalega — overstock nahi.", ct);
            return;
        }

        var lines = heavy.Select((r, i) =>
        {
            var p = r.Product;
            var pace = r.DaysLeft is null ? "pichle 30 din bika nahi" : $"~{Num(r.DaysLeft.Value)} din ka stock";
            var room = p.CostPrice is { } c && p.Price > 0 ? $"\n   chhoot ki had (nuqsan se pehle): {Pct((p.Price - c) / p.Price * 100m)} · munasib: ~{Pct((p.Price - c) / p.Price * 50m)}" : "\n   cost save nahi, chhoot ki had nahi nikal sakta";
            return $"{i + 1}. {Formatters.ProductLabel(p)} — {p.StockQty} piece, {pace}{room}";
        });
        await ReplyAsync(seller, $"📦 Zaroorat se zyada stock\n\n{string.Join("\n", lines)}\n\n\"Munasib\" chhoot margin ka aadha hai (guide); kitni chhoot se maal nikalega yeh bot nahi jaanta.", ct);
        SuggestNext("margin", "create discount");
    }

    // ---------- seasons

    private static (DateTime Start, DateTime End)? HijriWindow(int hijriMonth, int hijriDay, int beforeDays, int afterDays, int gregorianYear)
    {
        var calendar = new UmAlQuraCalendar();
        for (var y = calendar.GetYear(new DateTime(gregorianYear, 1, 1)) - 1; y <= calendar.GetYear(new DateTime(gregorianYear, 12, 31)) + 1; y++)
        {
            DateTime start;
            try { start = calendar.ToDateTime(y, hijriMonth, hijriDay, 0, 0, 0, 0); }
            catch (ArgumentOutOfRangeException) { continue; }
            if (start.Year != gregorianYear) continue;
            return (start.AddDays(-beforeDays), start.AddDays(afterDays + 1));
        }
        return null;
    }

    /// <summary>"Pichle saal Ramazan / Eid mein daily sale": that season's sale per day against the 30 days before it, and a stock hint for the next one.</summary>
    private async Task InsightSeasonAsync(Seller seller, string text, CancellationToken ct)
    {
        var ramadan = Regex.IsMatch(text, @"ramazan|ramadan|ramzan|رمضان", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var lastYear = Regex.IsMatch(text, @"pichle\s+sal|pichle\s+saal|last\s+year|پچھلے\s+سال", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var now = DateTime.UtcNow;
        var name = ramadan ? "Ramazan" : "Eid ul Fitr ke qareeb (10 din pehle se 2 din baad)";

        (DateTime Start, DateTime End)? Window(int year) => ramadan
            ? (HijriWindow(9, 1, 0, 29, year))
            : HijriWindow(10, 1, 10, 2, year);

        // This year's season if it has already ended (and "last year" was not said), otherwise last year's.
        var thisYear = Window(now.Year);
        var year = lastYear ? now.Year - 1 : thisYear is { } t && t.End <= now ? now.Year : now.Year - 1;
        var window = Window(year);
        if (window is not { } w)
        {
            await ReplyAsync(seller, "Maaf kijiye, is mausam ki tareekh nahi nikal saka.", ct);
            return;
        }

        var inSeason = await ActiveOrdersIn(seller, w.Start, w.End).Select(o => new { o.Id, o.Total }).ToListAsync(ct);
        var days = Math.Max(1, (int)(w.End - w.Start).TotalDays);
        if (inSeason.Count == 0)
        {
            await ReplyAsync(seller, $"📅 {name} ({w.Start:d MMM} – {w.End.AddDays(-1):d MMM yyyy}): is dauran aap ke koi order darj nahi, is liye muqabla nahi ho sakta.\n(Tareekhen taqreeban hain; Pakistan mein chand ki wajah se ek din ka farq ho sakta hai.)", ct);
            return;
        }

        var seasonDaily = inSeason.Sum(o => o.Total) / days;
        var baselineStart = w.Start.AddDays(-30);
        var baseline = (await ActiveOrdersIn(seller, baselineStart, w.Start).Select(o => o.Total).ToListAsync(ct)).Sum() / 30m;
        var uplift = baseline > 0 ? seasonDaily / baseline : (decimal?)null;
        var ids = inSeason.Select(o => o.Id).ToList();
        var top = (await _db.OrderItems.AsNoTracking().Where(i => ids.Contains(i.OrderId)).Select(i => new { i.ProductNameSnapshot, i.Quantity }).ToListAsync(ct))
            .GroupBy(i => i.ProductNameSnapshot, StringComparer.OrdinalIgnoreCase).Select(g => (Name: g.Key, Units: g.Sum(i => i.Quantity))).OrderByDescending(x => x.Units).Take(5).ToList();

        var recent = (await ActiveOrdersIn(seller, now.AddDays(-30), DateTime.MaxValue).Select(o => o.Total).ToListAsync(ct)).Sum() / 30m;
        var nextHint = uplift is { } u && recent > 0
            ? $"\n\nAgli baar ke liye andaza: aaj ki roz ki sale {Formatters.Money(Math.Round(recent))} × {Num(u)} guna = taqreeban {Formatters.Money(Math.Round(recent * u))} roz. Top products ka stock isi hisab se barhayein: " + string.Join(", ", top.Take(3).Select(t => t.Name)) + "."
            : "";
        await ReplyAsync(seller,
            $"📅 {name} — {w.Start:d MMM} se {w.End.AddDays(-1):d MMM yyyy}\n\n" +
            $"Roz ki average sale: {Formatters.Money(Math.Round(seasonDaily))} ({inSeason.Count} orders)\n" +
            (uplift is { } up ? $"Is se pehle ke 30 din: {Formatters.Money(Math.Round(baseline))} roz → {Num(up)} guna\n" : "Is se pehle ke 30 din ka data nahi.\n") +
            $"Sab se zyada bika: {string.Join(", ", top.Select(t => $"{t.Name} ({t.Units})"))}" + nextHint +
            "\n\n(Tareekhen taqreeban hain; chand ki wajah se ek din ka farq ho sakta hai. Yeh guide hai, pakki peshgoi nahi.)", ct);
        SuggestNext("stock", "trending products");
    }

    private static readonly Dictionary<string, int> MonthNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["january"] = 1, ["jan"] = 1, ["جنوری"] = 1, ["february"] = 2, ["feb"] = 2, ["فروری"] = 2, ["march"] = 3, ["mar"] = 3, ["مارچ"] = 3, ["april"] = 4, ["apr"] = 4, ["اپریل"] = 4,
        ["may"] = 5, ["مئی"] = 5, ["june"] = 6, ["jun"] = 6, ["جون"] = 6, ["july"] = 7, ["jul"] = 7, ["جولائی"] = 7, ["august"] = 8, ["aug"] = 8, ["اگست"] = 8,
        ["september"] = 9, ["sept"] = 9, ["sep"] = 9, ["ستمبر"] = 9, ["october"] = 10, ["oct"] = 10, ["اکتوبر"] = 10, ["november"] = 11, ["nov"] = 11, ["نومبر"] = 11, ["december"] = 12, ["dec"] = 12, ["دسمبر"] = 12
    };

    /// <summary>Each category's share of sales in two months ("September se October": the two months named, else last month against this month).</summary>
    private async Task InsightCategoryShiftAsync(Seller seller, string text, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var named = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('?', '.', ',', '!', '؟', '،'))
            .Where(MonthNames.ContainsKey).Select(t => MonthNames[t]).Distinct().Take(2).ToList();
        (DateTime Start, DateTime End, string Label) RangeOf(int month)
        {
            var year = month <= now.Month ? now.Year : now.Year - 1;
            var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            return (start, start.AddMonths(1), start.ToString("MMMM yyyy", CultureInfo.InvariantCulture));
        }

        (DateTime Start, DateTime End, string Label) a, b;
        if (named.Count == 2) (a, b) = (RangeOf(named[0]), RangeOf(named[1]));
        else
        {
            var (ls, le) = SellerClock.PreviousMonthRangeUtc(seller.TimeZoneId, now);
            var (cs, _) = SellerClock.CurrentMonthRangeUtc(seller.TimeZoneId, now);
            (a, b) = ((ls, le, "pichla mahina"), (cs, DateTime.MaxValue, "is mahine ab tak"));
        }

        var before = await CategoryTotalsAsync(seller, a.Start, a.End, ct);
        var after = await CategoryTotalsAsync(seller, b.Start, b.End, ct);
        if (before.Count == 0 && after.Count == 0)
        {
            await ReplyAsync(seller, $"📊 Category ka tanasub: {a.Label} aur {b.Label} dono mein koi order nahi.", ct);
            return;
        }

        var totalA = before.Values.Sum();
        var totalB = after.Values.Sum();
        var names = before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var rows = names.Select(n =>
            {
                var shareA = totalA > 0 ? before.GetValueOrDefault(n) / totalA * 100m : 0m;
                var shareB = totalB > 0 ? after.GetValueOrDefault(n) / totalB * 100m : 0m;
                return (Name: n, ShareA: shareA, ShareB: shareB);
            })
            .OrderByDescending(r => Math.Max(r.ShareA, r.ShareB)).ToList();
        var lines = rows.Take(6).Select(r => $"• {r.Name}: {Pct(r.ShareA)} → {Pct(r.ShareB)} ({(r.ShareB >= r.ShareA ? "+" : "-")}{Num(Math.Abs(r.ShareB - r.ShareA))} points)");
        await ReplyAsync(seller,
            $"📊 Category ka tanasub (sale ka hissa) — {a.Label} → {b.Label}\n\n{string.Join("\n", lines)}\n\nKul sale: {Formatters.Money(totalA)} → {Formatters.Money(totalB)}.\n" +
            "Category tab bantti hai jab product par likhi ho: \"Kurti - 1800, category: Kapron\".", ct);
        SuggestNext("sales by category", "trending products");
    }

    private async Task<Dictionary<string, decimal>> CategoryTotalsAsync(Seller seller, DateTime start, DateTime end, CancellationToken ct)
    {
        var rows = await _db.OrderItems.AsNoTracking()
            .Where(i => i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= start && i.Order.CreatedAt < end
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { i.ProductId, i.UnitPrice, i.Quantity })
            .ToListAsync(ct);
        var ids = rows.Where(r => r.ProductId != null).Select(r => r.ProductId!.Value).Distinct().ToList();
        var categories = (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && ids.Contains(p.Id)).Select(p => new { p.Id, p.Category }).ToListAsync(ct))
            .ToDictionary(p => p.Id, p => string.IsNullOrWhiteSpace(p.Category) ? NoCategory : p.Category!.Trim());
        return rows.GroupBy(r => r.ProductId is { } id && categories.TryGetValue(id, out var c) ? c : NoCategory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.UnitPrice * r.Quantity), StringComparer.OrdinalIgnoreCase);
    }
}
