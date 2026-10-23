using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// A basic cash drawer ("galla") kept through chat:
//   drawer open 5000          start the day with Rs.5,000
//   cash in 500 udhaar mila   cash put in      cash out 300 chai   cash taken out      cash sale 1200 kurti   a counter sale in cash
//   cash undo                 remove the last entry of the open drawer
//   drawer | drawer status    what should be in the drawer now
//   drawer close 18000        the cash counted: shows the difference and closes the shift      drawer history   the last shifts
// Expected cash = opening + COD orders marked paid since it opened + cash in / sales - cash out. Bank / wallet payments are not cash and never count.
public partial class ConversationEngine
{
    private const decimal MaxCashAmount = 10_000_000m;

    private sealed record CashSummary(decimal Opening, decimal CodCollected, decimal CashIn, decimal CashOut, decimal Expected);

    private Task<CashDrawerSession?> OpenDrawerAsync(Seller seller, CancellationToken ct) =>
        _db.CashDrawerSessions.Where(s => s.SellerId == seller.Id && s.ClosedAt == null).OrderByDescending(s => s.OpenedAt).FirstOrDefaultAsync(ct);

    private async Task<CashSummary> SummariseCashAsync(Seller seller, CashDrawerSession session, DateTime untilUtc, CancellationToken ct)
    {
        // COD cash comes in when the order is marked paid. Money a courier / gateway paid out later (PayoutId set) went to the bank, not the drawer. Sqlite cannot SUM decimals, so the few orders of one shift are added up in memory.
        var codOrders = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.PaymentMethod == OrderPaymentMethod.Cod && o.PaymentStatus == PaymentStatus.Paid
                        && o.PayoutId == null && o.PaidAt != null && o.PaidAt >= session.OpenedAt && o.PaidAt <= untilUtc
                        && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .ToListAsync(ct);
        var cod = codOrders.Sum(o => OrderMoney.Received(o));
        var movements = await _db.CashMovements.AsNoTracking().Where(m => m.SessionId == session.Id).ToListAsync(ct);
        var cashIn = movements.Where(m => m.Kind != CashMovementKind.Out).Sum(m => m.Amount);
        var cashOut = movements.Where(m => m.Kind == CashMovementKind.Out).Sum(m => m.Amount);
        return new CashSummary(session.OpeningAmount, cod, cashIn, cashOut, session.OpeningAmount + cod + cashIn - cashOut);
    }

    private static string CashLines(CashSummary c) =>
        $"Shuru: {Formatters.Money(c.Opening)}\n" +
        $"COD wasooli: {Formatters.Money(c.CodCollected)}\n" +
        $"Cash in / counter sale: {Formatters.Money(c.CashIn)}\n" +
        $"Cash out: {Formatters.Money(c.CashOut)}\n" +
        $"Ab hona chahiye: {Formatters.Money(c.Expected)}";

    private string LocalTime(Seller seller, DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), SellerClock.Resolve(seller.TimeZoneId)).ToString("dd MMM, hh:mm tt", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>When a drawer is open, a COD payment just collected is part of its cash: say what the drawer should hold now ("" when no drawer is open).</summary>
    private async Task<string> DrawerHintAfterCodAsync(Seller seller, CancellationToken ct)
    {
        var open = await OpenDrawerAsync(seller, ct);
        if (open is null) return "";
        await _db.SaveChangesAsync(ct); // the order just marked paid must be in the database before the drawer is added up
        var summary = await SummariseCashAsync(seller, open, _clock.GetUtcNow().UtcDateTime, ct);
        return MessageTemplates.Get("cash-drawer.019", Formatters.Money(summary.Expected));
    }

    private async Task HandleCashDrawerAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var open = await OpenDrawerAsync(seller, ct);

        switch (cmd.Text)
        {
            case "open":
                if (open is not null)
                {
                    await ReplyAsync(seller, MessageTemplates.Get("cash-drawer.001", LocalTime(seller, open.OpenedAt), Formatters.Money(open.OpeningAmount)), ct);
                    return;
                }
                if (cmd.Amount is not { } opening)
                {
                    await ReplyAsync(seller, MessageTemplates.Get("cash-drawer.002"), ct);
                    return;
                }
                if (opening < 0 || opening > MaxCashAmount)
                {
                    await ReplyAsync(seller, MessageTemplates.Get("cash-drawer.003"), ct);
                    return;
                }
                _db.CashDrawerSessions.Add(new CashDrawerSession { SellerId = seller.Id, OpenedAt = now, OpeningAmount = opening, Note = cmd.Text2 });
                await ReplyAsync(seller,
                    MessageTemplates.Get("cash-drawer.004", Formatters.Money(opening)), ct);
                return;

            case "close":
            {
                if (open is null)
                {
                    await ReplyAsync(seller, MessageTemplates.Get("cash-drawer.005"), ct);
                    return;
                }
                var summary = await SummariseCashAsync(seller, open, now, ct);
                if (cmd.Amount is not { } counted)
                {
                    await ReplyAsync(seller, MessageTemplates.Get("cash-drawer.006", LocalTime(seller, open.OpenedAt), CashLines(summary), $"{summary.Expected:0.##}"), ct);
                    return;
                }
                if (counted < 0 || counted > MaxCashAmount)
                {
                    await ReplyAsync(seller, MessageTemplates.Get("cash-drawer.007"), ct);
                    return;
                }
                open.ClosedAt = now;
                open.CountedAmount = counted;
                open.ExpectedAmount = summary.Expected;
                if (!string.IsNullOrWhiteSpace(cmd.Text2)) open.Note = cmd.Text2;
                var diff = counted - summary.Expected;
                await ReplyAsync(seller,
                    MessageTemplates.Get("cash-drawer.008", CashLines(summary), Formatters.Money(counted), (diff == 0 ? "✔️ Barabar — koi farq nahi." : diff < 0 ? $"⚠️ {Formatters.Money(-diff)} KAM hain." : $"⚠️ {Formatters.Money(diff)} ZYADA hain."), (diff == 0 ? "" : "\nKoi cash in/out likhna reh gaya ho to \"drawer history\" se dekhein; agle din se har len-den likhein.")), ct);
                return;
            }

            case "history":
            {
                var closed = await _db.CashDrawerSessions.AsNoTracking()
                    .Where(s => s.SellerId == seller.Id && s.ClosedAt != null).OrderByDescending(s => s.ClosedAt).Take(7).ToListAsync(ct);
                if (closed.Count == 0)
                {
                    await ReplyAsync(seller, MessageTemplates.Get("cash-drawer.009", (open is null ? " Shuru karne ke liye: drawer open 5000" : "")), ct);
                    return;
                }
                var lines = closed.Select(s =>
                {
                    var diff = (s.CountedAmount ?? 0) - (s.ExpectedAmount ?? 0);
                    return $"• {LocalTime(seller, s.OpenedAt)}: shuru {Formatters.Money(s.OpeningAmount)} → gine {Formatters.Money(s.CountedAmount ?? 0)} " +
                           (diff == 0 ? "✔️" : diff < 0 ? $"({Formatters.Money(-diff)} kam)" : $"({Formatters.Money(diff)} zyada)");
                });
                await ReplyAsync(seller, MessageTemplates.Get("cash-drawer.010", string.Join("\n", lines)), ct);
                return;
            }

            default: // status
            {
                if (open is null)
                {
                    var last = await _db.CashDrawerSessions.AsNoTracking()
                        .Where(s => s.SellerId == seller.Id && s.ClosedAt != null).OrderByDescending(s => s.ClosedAt).FirstOrDefaultAsync(ct);
                    await ReplyAsync(seller,
                        MessageTemplates.Get("cash-drawer.011", (last is null ? "" : $"\nPichla shift: gine {Formatters.Money(last.CountedAmount ?? 0)} ({LocalTime(seller, last.ClosedAt ?? last.OpenedAt)} par band).")), ct);
                    return;
                }
                var summary = await SummariseCashAsync(seller, open, now, ct);
                await ReplyAsync(seller,
                    MessageTemplates.Get("cash-drawer.012", LocalTime(seller, open.OpenedAt), CashLines(summary)), ct);
                return;
            }
        }
    }

    private async Task HandleCashMovementAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var open = await OpenDrawerAsync(seller, ct);
        if (open is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("cash-drawer.013"), ct);
            return;
        }
        var now = _clock.GetUtcNow().UtcDateTime;

        if (cmd.Text == "undo")
        {
            var last = await _db.CashMovements.Where(m => m.SessionId == open.Id).OrderByDescending(m => m.Id).FirstOrDefaultAsync(ct);
            if (last is null)
            {
                await ReplyAsync(seller, MessageTemplates.Get("cash-drawer.014"), ct);
                return;
            }
            _db.CashMovements.Remove(last);
            await _db.SaveChangesAsync(ct);
            var after = await SummariseCashAsync(seller, open, now, ct);
            await ReplyAsync(seller, MessageTemplates.Get("cash-drawer.015", last.Kind.ToString().ToLowerInvariant(), Formatters.Money(last.Amount), Formatters.Money(after.Expected)), ct);
            return;
        }

        var kind = cmd.Text switch { "out" => CashMovementKind.Out, "sale" => CashMovementKind.Sale, _ => CashMovementKind.In };
        if (cmd.Amount is not { } amount)
        {
            var current = await SummariseCashAsync(seller, open, now, ct);
            await ReplyAsync(seller, MessageTemplates.Get("cash-drawer.016", cmd.Text, Formatters.Money(current.Expected)), ct);
            return;
        }
        if (amount <= 0 || amount > MaxCashAmount)
        {
            await ReplyAsync(seller, MessageTemplates.Get("cash-drawer.017"), ct);
            return;
        }

        var before = await SummariseCashAsync(seller, open, now, ct);
        // A cash entry sent while the phone was offline keeps its send time, but never before the drawer opened (it would fall outside this shift).
        var movementAt = RecordTimeUtc();
        if (movementAt < open.OpenedAt) movementAt = open.OpenedAt;
        _db.CashMovements.Add(new CashMovement { SellerId = seller.Id, SessionId = open.Id, Kind = kind, Amount = amount, Note = cmd.Text2, CreatedAt = movementAt });
        await _db.SaveChangesAsync(ct);
        var summary = await SummariseCashAsync(seller, open, now, ct);
        var label = kind switch { CashMovementKind.Out => MessageTemplates.Get("cash-drawer.020"), CashMovementKind.Sale => MessageTemplates.Get("cash-drawer.021"), _ => MessageTemplates.Get("cash-drawer.022") };
        await ReplyAsync(seller,
            MessageTemplates.Get("cash-drawer.018", label, Formatters.Money(amount), (string.IsNullOrWhiteSpace(cmd.Text2) ? "" : $" ({cmd.Text2})"), Formatters.Money(summary.Expected), (kind == CashMovementKind.Out && amount > before.Expected ? $"\n⚠️ Itna cash drawer mein tha hi nahi (tha {Formatters.Money(before.Expected)}) — koi cash in/sale likhna reh gaya?" : "")), ct);
    }
}
