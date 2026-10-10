using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// Expenses: "expense 500 packaging" logs a business cost (category = first word of the note), "expenses [today|last month]" lists
// them, "monthly net" = this month's sales (non-cancelled, non-returned orders, delivery included — same as today's summary)
// minus this month's expenses. Product cost of goods is the separate "profit" report. Sums are done in memory (Sqlite can't SUM decimal).
public partial class ConversationEngine
{
    private const decimal MaxExpenseAmount = 100_000_000m;

    private (DateTime Start, DateTime End, string Label) ExpensePeriod(Seller seller, string? period)
    {
        var now = DateTime.UtcNow;
        if (period is not (null or "today" or "lastmonth" or "month") && ReportPeriods.Resolve(period, seller.TimeZoneId, now) is { } resolved)
            return (resolved.StartUtc, resolved.EndUtc, PeriodLabel(seller, period).ToLowerInvariant());
        return period switch
        {
            "today" => (SellerClock.StartOfLocalDayUtc(seller.TimeZoneId, now), DateTime.MaxValue, "aaj"),
            "lastmonth" => (SellerClock.PreviousMonthRangeUtc(seller.TimeZoneId, now).StartUtc, SellerClock.PreviousMonthRangeUtc(seller.TimeZoneId, now).EndUtc, "pichla maah"),
            _ => (SellerClock.CurrentMonthRangeUtc(seller.TimeZoneId, now).StartUtc, SellerClock.CurrentMonthRangeUtc(seller.TimeZoneId, now).EndUtc, "is maah")
        };
    }

    private async Task<List<Expense>> LoadExpensesAsync(Seller seller, DateTime start, DateTime end, CancellationToken ct) =>
        await _db.Expenses.AsNoTracking().Where(x => x.SellerId == seller.Id && x.CreatedAt >= start && x.CreatedAt < end).ToListAsync(ct);

    private async Task HandleExpenseAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var amount = cmd.Amount ?? 0;
        if (amount <= 0 || amount > MaxExpenseAmount)
        {
            await ReplyAsync(seller, "Kharcha 0 se zyada hona chahiye. Jaise: \"expense 500 packaging\"", ct);
            return;
        }

        var note = string.IsNullOrWhiteSpace(cmd.Text) ? null : cmd.Text.Trim();
        var category = note is null ? "other" : note.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim(',', '.', ':', ';').ToLowerInvariant();
        if (category.Length == 0) category = "other";

        var expense = new Expense { SellerId = seller.Id, Amount = amount, Category = category, Note = note };
        _db.Expenses.Add(expense);
        await _db.SaveChangesAsync(ct);
        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id, ActionType = ActionType.ExpenseAdded,
            PayloadJson = JsonSerializer.Serialize(new { ExpenseId = expense.Id })
        });

        var (start, end, _) = ExpensePeriod(seller, "month");
        var monthTotal = (await LoadExpensesAsync(seller, start, end, ct)).Sum(x => x.Amount);
        await ReplyAsync(seller,
            $"✅ Kharcha note: {Formatters.Money(amount)} — {note ?? category}\n" +
            $"Is maah ka total kharcha: {Formatters.Money(monthTotal)}\n" +
            "Ghalti ho to \"undo\" · Hisaab: \"monthly net\"", ct);
    }

    private async Task UndoExpenseAsync(Seller seller, ActionLog log, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(log.PayloadJson);
        var id = doc.RootElement.GetProperty("ExpenseId").GetInt32();
        var expense = await _db.Expenses.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == seller.Id, ct);
        if (expense is null) return;
        _db.Expenses.Remove(expense);
        await ReplyAsync(seller, $"↩️ Reverted — kharcha {Formatters.Money(expense.Amount)} ({expense.Note ?? expense.Category}) hata diya.", ct);
    }

    private async Task HandleExpenseListAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = ExpensePeriod(seller, period);
        var expenses = (await LoadExpensesAsync(seller, start, end, ct)).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToList();
        if (expenses.Count == 0)
        {
            await ReplyAsync(seller, $"🧾 Kharcha ({label}): koi kharcha record nahi.\n\nShuru karne ke liye: \"expense 500 packaging\"", ct);
            return;
        }

        var tz = SellerClock.Resolve(seller.TimeZoneId);
        const int shown = 15;
        var lines = expenses.Take(shown).Select(x =>
            $"• {TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Utc), tz):dd MMM} — {Formatters.Money(x.Amount)} {x.Note ?? x.Category}");
        var more = expenses.Count > shown ? $"\n…aur {expenses.Count - shown} purane" : "";
        await ReplyAsync(seller,
            $"🧾 Kharcha ({label}) — {expenses.Count}\n\n{string.Join("\n", lines)}{more}\n\n" +
            $"Total: {Formatters.Money(expenses.Sum(x => x.Amount))}\nGhalti ho to \"undo\".", ct);
    }

    private async Task HandleMonthlyNetAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = ExpensePeriod(seller, period ?? "month");

        var orders = await _db.Orders.AsNoTracking()
            .Where(o => o.SellerId == seller.Id && o.CreatedAt >= start && o.CreatedAt < end
                        && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
            .ToListAsync(ct);
        var expenses = await LoadExpensesAsync(seller, start, end, ct);
        var wages = await LoadWagesAsync(seller, start, end, ct);
        var losses = await LoadLossesAsync(seller, start, end, ct);

        var sales = orders.Sum(o => o.Total);
        var spent = expenses.Sum(x => x.Amount);
        var wageTotal = wages.Sum(x => x.Amount);
        var net = sales - spent - wageTotal;
        var wageLine = wages.Count == 0 ? "" : $"Tankhwah: {Formatters.Money(wageTotal)} ({wages.Count})\n";
        var lossLine = losses.Count == 0 ? "" : $"Nuqsan (cost, net mein shamil nahi): {Formatters.Money(losses.Sum(x => x.Quantity * x.UnitCost))} ({losses.Count})\n";

        var top = expenses.GroupBy(x => x.Category).Select(g => (Category: g.Key, Total: g.Sum(x => x.Amount)))
            .OrderByDescending(g => g.Total).Take(3).ToList();
        var topLines = top.Count == 0 ? "" : "\n\nSab se zyada kharcha:\n" + string.Join("\n", top.Select(t => $"• {t.Category}: {Formatters.Money(t.Total)}"));
        var hint = expenses.Count == 0 ? "\n\nKharcha likhein, jaise: \"expense 500 packaging\"" : "\n\nProduct cost ke baad margin: \"profit month\"";

        await ReplyAsync(seller,
            $"📊 {(period is null or "month" or "lastmonth" ? "Monthly Net" : "Net")} — {label}\n\n" +
            $"Sales: {Formatters.Money(sales)} ({orders.Count} orders)\n" +
            $"Kharcha: {Formatters.Money(spent)} ({expenses.Count})\n" +
            wageLine + lossLine +
            $"{(net < 0 ? "🔻" : "✅")} Net: {Formatters.Money(net)}" + topLines + hint, ct);
    }
}
