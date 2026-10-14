using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// Courier / gateway payouts: the money a courier collected on COD orders (or a gateway took online) reaches the seller later, minus charges.
//   payout pending                      COD orders delivered but not yet paid out, by courier (what the couriers owe)
//   payout 11700 TCS orders 1 2 3 fee 300   money arrived: the orders are settled and marked paid, and the amount is checked against what they should have brought
//   payout 11700 TCS                    money arrived, no orders named (a hint says which pending orders add up to it)
//   payouts                             the last payouts      payout skip 12 13   the cash reached the seller directly (no payout expected)
//   payout undo                         remove the last payout and put its orders back as they were
// Paying others (staff, suppliers) is not here: wages / expense / cash out cover that. Courier-settled orders never count as drawer cash.
public partial class ConversationEngine
{
    private const decimal MaxPayoutAmount = 100_000_000m;
    private static readonly string NoCourier = MessageTemplates.Get("payouts.012");

    internal sealed record PayoutArgs(string Source, decimal Fee, List<int> Orders, string? Reference);

    private sealed record PayoutUndoEntry(int Id, string PaymentStatus, decimal AmountPaid, DateTime? PaidAt);

    /// <summary>What a courier / gateway should pay for this order: its total less income tax they withhold and any advance already received.</summary>
    private static decimal PayoutReceivable(Order order) =>
        Math.Max(0, order.Total - order.TaxWithheld - (order.PaymentStatus == PaymentStatus.Paid ? 0 : order.AmountPaid));

    private static decimal ParseAmountText(string text) =>
        decimal.TryParse(text.Replace(",", ""), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>"TCS orders 1 2 3 fee 300 ref ABC12" -> source TCS, orders 1 2 3, fee 300, reference ABC12 (every part optional).</summary>
    internal static PayoutArgs ParsePayoutArgs(string? text)
    {
        var rest = text ?? "";
        var fee = 0m;
        string? reference = null;

        var feeMatch = Regex.Match(rest, @"\b(?:fee|charges?|kharcha)\s*:?\s*(?:rs\.?\s*)?(?<f>\d[\d,]*(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (feeMatch.Success)
        {
            fee = ParseAmountText(feeMatch.Groups["f"].Value);
            rest = rest.Remove(feeMatch.Index, feeMatch.Length);
        }
        var refMatch = Regex.Match(rest, @"\bref(?:erence)?\s*:?\s*(?<r>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (refMatch.Success)
        {
            reference = refMatch.Groups["r"].Value;
            rest = rest.Remove(refMatch.Index, refMatch.Length);
        }

        var orders = new List<int>();
        var source = rest;
        var orderMatch = Regex.Match(rest, @"\borders?\b|#", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (orderMatch.Success)
        {
            source = rest[..orderMatch.Index];
            orders = Regex.Matches(rest[orderMatch.Index..], @"\d+").Select(n => int.Parse(n.Value)).Where(n => n > 0).Distinct().ToList();
        }
        source = Regex.Replace(source, @"\b(?:from|se|ka|ki|mila|mili|aaya|aayi|received)\b", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        source = Regex.Replace(source, @"\s+", " ").Trim(' ', ',', ':', '-');
        if (source.Length > 40) source = source[..40].Trim();
        return new PayoutArgs(source.Length == 0 ? "Courier" : source, fee, orders, reference);
    }

    private Task<List<Order>> PendingPayoutOrdersAsync(Seller seller, CancellationToken ct) =>
        _db.Orders.Include(o => o.Customer)
            .Where(o => o.SellerId == seller.Id && o.PaymentMethod == OrderPaymentMethod.Cod && o.Status == OrderStatus.Delivered
                        && o.PaymentStatus == PaymentStatus.Unpaid && o.PayoutSettledAt == null)
            .OrderBy(o => o.DeliveredAt ?? o.CreatedAt).ToListAsync(ct);

    private async Task HandlePayoutAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        switch (cmd.Text)
        {
            case "record": await RecordPayoutAsync(seller, cmd, ct); return;
            case "list": await ListPayoutsAsync(seller, ct); return;
            case "skip": await SkipPayoutAsync(seller, cmd.Text2, ct); return;
            case "undo": await UndoPayoutAsync(seller, ct); return;
            default: await ShowPendingPayoutsAsync(seller, ct); return;
        }
    }

    private async Task ShowPendingPayoutsAsync(Seller seller, CancellationToken ct)
    {
        var orders = await PendingPayoutOrdersAsync(seller, ct);
        if (orders.Count == 0)
        {
            await ReplyAsync(seller,
                MessageTemplates.Get("payouts.001"), ct);
            return;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var groups = orders.GroupBy(o => string.IsNullOrWhiteSpace(o.TrackingCourier) ? NoCourier : o.TrackingCourier!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(PayoutReceivable)).ToList();
        var lines = new List<string>();
        foreach (var g in groups)
        {
            lines.Add($"• {g.Key}: {g.Count()} orders — {Formatters.Money(g.Sum(PayoutReceivable))}");
            foreach (var o in g.Take(6))
            {
                var days = Math.Max(0, (int)(now - (o.DeliveredAt ?? o.CreatedAt)).TotalDays);
                lines.Add($"   #{o.Id} {o.Customer?.Name} — {Formatters.Money(PayoutReceivable(o))} ({days} din se)");
            }
            if (g.Count() > 6) lines.Add($"   … aur {g.Count() - 6}");
        }
        var first = groups[0];
        await ReplyAsync(seller,
            MessageTemplates.Get("payouts.002", Formatters.Money(orders.Sum(PayoutReceivable)), orders.Count, string.Join("\n", lines), (first.Key == NoCourier ? "TCS" : first.Key), string.Join(" ", first.Take(3).Select(o => o.Id))), ct);
    }

    private async Task ListPayoutsAsync(Seller seller, CancellationToken ct)
    {
        var payouts = await _db.Payouts.AsNoTracking().Where(p => p.SellerId == seller.Id).OrderByDescending(p => p.Id).Take(10).ToListAsync(ct);
        if (payouts.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("payouts.003"), ct);
            return;
        }
        var ids = payouts.Select(p => p.Id).ToList();
        var counts = (await _db.Orders.AsNoTracking().Where(o => o.PayoutId != null && ids.Contains(o.PayoutId.Value)).Select(o => o.PayoutId!.Value).ToListAsync(ct))
            .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        var lines = payouts.Select(p =>
            $"• #{p.Id} {LocalTime(seller, p.ReceivedAt)} — {p.Source}: {Formatters.Money(p.Amount)}" +
            (p.Fee > 0 ? $" (fee {Formatters.Money(p.Fee)})" : "") +
            (counts.TryGetValue(p.Id, out var n) ? $" · {n} orders" : " · orders jore nahi"));
        await ReplyAsync(seller, MessageTemplates.Get("payouts.004", string.Join("\n", lines)), ct);
    }

    private async Task RecordPayoutAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        if (cmd.Amount is not { } amount || amount <= 0 || amount > MaxPayoutAmount)
        {
            await ReplyAsync(seller, MessageTemplates.Get("payouts.005"), ct);
            return;
        }
        var args = ParsePayoutArgs(cmd.Text2);
        var now = _clock.GetUtcNow().UtcDateTime;

        var linked = new List<Order>();
        if (args.Orders.Count > 0)
        {
            linked = await _db.Orders.Include(o => o.Customer).Where(o => o.SellerId == seller.Id && args.Orders.Contains(o.Id)).ToListAsync(ct);
            var missing = args.Orders.Where(id => linked.All(o => o.Id != id)).ToList();
            if (missing.Count > 0)
            {
                await ReplyAsync(seller, MessageTemplates.Get("payouts.006", string.Join(", ", missing.Select(id => $"#{id}"))), ct);
                return;
            }
            var problems = new List<string>();
            foreach (var o in linked)
            {
                if (o.Status is OrderStatus.Cancelled or OrderStatus.Returned) problems.Add($"#{o.Id} {Formatters.Status(o.Status)} hai");
                else if (o.PaymentMethod != OrderPaymentMethod.Cod) problems.Add($"#{o.Id} COD order nahi hai");
                else if (o.PayoutSettledAt is not null) problems.Add($"#{o.Id} pehle se settle hai" + (o.PayoutId is { } pid ? $" (payout #{pid})" : ""));
            }
            if (problems.Count > 0)
            {
                await ReplyAsync(seller, MessageTemplates.Get("payouts.007", string.Join("; ", problems)), ct);
                return;
            }
        }

        var payout = new Payout { SellerId = seller.Id, Source = args.Source, Amount = amount, Fee = args.Fee, Reference = args.Reference, ReceivedAt = now };
        _db.Payouts.Add(payout);
        await _db.SaveChangesAsync(ct); // the payout needs its id before orders can point at it

        var receivable = 0m;
        if (linked.Count > 0)
        {
            payout.UndoJson = JsonSerializer.Serialize(linked.Select(o => new PayoutUndoEntry(o.Id, o.PaymentStatus.ToString(), o.AmountPaid, o.PaidAt)).ToList());
            foreach (var o in linked)
            {
                receivable += PayoutReceivable(o);
                o.PayoutId = payout.Id;
                o.PayoutSettledAt = now;
                o.PaymentStatus = PaymentStatus.Paid;
                o.PaidAt ??= now;
                o.AmountPaid = o.Total;
            }
        }

        var text = $"✅ Payout #{payout.Id} save: {Formatters.Money(amount)} — {payout.Source}" + (args.Fee > 0 ? $" (fee {Formatters.Money(args.Fee)})" : "") + ".";
        if (linked.Count > 0)
        {
            var diff = receivable - (amount + args.Fee);
            text += $"\n{linked.Count} orders ({string.Join(", ", linked.OrderBy(o => o.Id).Select(o => $"#{o.Id}"))}) settle ho gaye aur PAID mark hue.\n" +
                    $"In orders ka hisaab: {Formatters.Money(receivable)}" + (args.Fee > 0 ? $" = mila {Formatters.Money(amount)} + fee {Formatters.Money(args.Fee)}" : "") + "\n" +
                    (diff == 0 ? "✔️ Barabar — koi farq nahi."
                        : diff > 0 ? $"⚠️ {Formatters.Money(diff)} KAM aaya — {payout.Source} se pooch lein (ya fee / tax kata ho to: payout undo, phir fee likh kar dobara)."
                        : $"⚠️ {Formatters.Money(-diff)} ZYADA aaya — shayad koi aur order bhi shamil hai.");
        }
        else
        {
            var pending = await PendingPayoutOrdersAsync(seller, ct);
            var sameCourier = pending.Where(o => (o.TrackingCourier ?? "").Contains(payout.Source, StringComparison.OrdinalIgnoreCase)).ToList();
            text += sameCourier.Count > 0 && sameCourier.Sum(PayoutReceivable) == amount + args.Fee
                ? $"\n💡 Yeh {payout.Source} ke {sameCourier.Count} baqi orders ke total se milta hai. Jorne ke liye: payout undo, phir: payout {amount:0.##} {payout.Source} orders {string.Join(" ", sameCourier.Select(o => o.Id))}" + (args.Fee > 0 ? $" fee {args.Fee:0.##}" : "")
                : "\nKoi order jora nahi. Baqi orders dekhein: payout pending (phir order numbers ke saath dobara likhein).";
        }
        await ReplyAsync(seller, MessageTemplates.Get("payouts.008", text), ct);
    }

    private async Task SkipPayoutAsync(Seller seller, string? text, CancellationToken ct)
    {
        var ids = Regex.Matches(text ?? "", @"\d+").Select(n => int.Parse(n.Value)).Where(n => n > 0).Distinct().ToList();
        if (ids.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("payouts.009"), ct);
            return;
        }
        var orders = await _db.Orders.Where(o => o.SellerId == seller.Id && ids.Contains(o.Id)).ToListAsync(ct);
        var notFound = ids.Where(id => orders.All(o => o.Id != id)).ToList();
        var skipped = orders.Where(o => o.PaymentMethod == OrderPaymentMethod.Cod && o.PayoutSettledAt is null && o.Status is not (OrderStatus.Cancelled or OrderStatus.Returned)).ToList();
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var o in skipped) o.PayoutSettledAt = now;
        await ReplyAsync(seller,
            (skipped.Count > 0 ? $"✅ {string.Join(", ", skipped.Select(o => $"#{o.Id}"))} ab payout ki list se hata diye (cash seedha mila)." : "Koi order skip nahi hua.") +
            (notFound.Count > 0 ? $"\nNahi mila: {string.Join(", ", notFound.Select(id => $"#{id}"))}" : "") +
            (orders.Count - skipped.Count > 0 ? "\nBaqi orders COD nahi, cancelled/returned ya pehle se settle hain." : ""), ct);
    }

    private async Task UndoPayoutAsync(Seller seller, CancellationToken ct)
    {
        var payout = await _db.Payouts.Where(p => p.SellerId == seller.Id).OrderByDescending(p => p.Id).FirstOrDefaultAsync(ct);
        if (payout is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("payouts.010"), ct);
            return;
        }

        var orders = await _db.Orders.Where(o => o.SellerId == seller.Id && o.PayoutId == payout.Id).ToListAsync(ct);
        var before = string.IsNullOrWhiteSpace(payout.UndoJson)
            ? new List<PayoutUndoEntry>()
            : JsonSerializer.Deserialize<List<PayoutUndoEntry>>(payout.UndoJson) ?? new List<PayoutUndoEntry>();
        foreach (var o in orders)
        {
            o.PayoutId = null;
            o.PayoutSettledAt = null;
            if (before.FirstOrDefault(b => b.Id == o.Id) is { } was && Enum.TryParse<PaymentStatus>(was.PaymentStatus, out var status))
            {
                o.PaymentStatus = status;
                o.AmountPaid = was.AmountPaid;
                o.PaidAt = was.PaidAt;
            }
        }
        _db.Payouts.Remove(payout);
        await ReplyAsync(seller, MessageTemplates.Get("payouts.011", payout.Id, payout.Source, Formatters.Money(payout.Amount), (orders.Count > 0 ? $"; {orders.Count} orders wapas \"payout baqi\" mein." : ".")), ct);
    }
}
