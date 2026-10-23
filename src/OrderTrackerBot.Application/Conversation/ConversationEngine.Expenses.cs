using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Time;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

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
            await ReplyAsync(seller, MessageTemplates.Get("expenses.001"), ct);
            return;
        }

        var note = string.IsNullOrWhiteSpace(cmd.Text) ? null : cmd.Text.Trim();
        var category = ExpenseCategoryOf(note);

        var expense = new Expense { SellerId = seller.Id, Amount = amount, Category = category, Note = note, CreatedAt = RecordTimeUtc() };
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
            MessageTemplates.Get("expenses.002", Formatters.Money(amount), note ?? category, Formatters.Money(monthTotal)), ct);
    }

    /// <summary>The category is the first word of the note, lower-cased ("packaging 500" = packaging); "other" when there is no note.</summary>
    private static string ExpenseCategoryOf(string? note)
    {
        var category = note is null ? "other" : note.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim(',', '.', ':', ';').ToLowerInvariant();
        return category.Length == 0 ? "other" : category;
    }

    private async Task UndoExpenseAsync(Seller seller, ActionLog log, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(log.PayloadJson);
        var id = doc.RootElement.GetProperty("ExpenseId").GetInt32();
        var expense = await _db.Expenses.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == seller.Id, ct);
        if (expense is null) return;
        _db.Expenses.Remove(expense);
        await ReplyAsync(seller, MessageTemplates.Get("expenses.003", Formatters.Money(expense.Amount), expense.Note ?? expense.Category), ct);
    }

    /// <summary>
    /// "expense edit 12 800 packaging": the amount and/or the note of one expense. The note sets the category as a new expense does.
    /// The old values go into one ActionLog so "undo" puts them back.
    /// </summary>
    private async Task HandleExpenseEditAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var hasNote = !string.IsNullOrWhiteSpace(cmd.Text);
        if (cmd.Amount is null && !hasNote)
        {
            await ReplyAsync(seller, MessageTemplates.Get("expenses.011"), ct);
            return;
        }
        if (cmd.Amount is { } newAmount && (newAmount <= 0 || newAmount > MaxExpenseAmount))
        {
            await ReplyAsync(seller, MessageTemplates.Get("expenses.001"), ct);
            return;
        }

        var expense = await _db.Expenses.FirstOrDefaultAsync(x => x.Id == cmd.Number && x.SellerId == seller.Id, ct);
        if (expense is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("expenses.012", cmd.Number), ct);
            return;
        }

        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id, ActionType = ActionType.ExpenseChanged,
            PayloadJson = JsonSerializer.Serialize(new { ExpenseId = expense.Id, expense.Amount, expense.Category, expense.Note })
        });
        if (cmd.Amount is { } amount) expense.Amount = amount;
        if (hasNote)
        {
            expense.Note = cmd.Text.Trim();
            expense.Category = ExpenseCategoryOf(expense.Note);
        }
        await _db.SaveChangesAsync(ct);
        await ReplyAsync(seller, MessageTemplates.Get("expenses.013", expense.Id, Formatters.Money(expense.Amount), expense.Note ?? expense.Category), ct);
    }

    /// <summary>"expense delete 12": removes one expense. The whole row goes into the ActionLog, so "undo" writes it back with its date.</summary>
    private async Task HandleExpenseDeleteAsync(Seller seller, ParsedCommand cmd, CancellationToken ct)
    {
        var expense = await _db.Expenses.FirstOrDefaultAsync(x => x.Id == cmd.Number && x.SellerId == seller.Id, ct);
        if (expense is null)
        {
            await ReplyAsync(seller, MessageTemplates.Get("expenses.012", cmd.Number), ct);
            return;
        }

        _db.ActionLogs.Add(new ActionLog
        {
            SellerId = seller.Id, ActionType = ActionType.ExpenseChanged,
            PayloadJson = JsonSerializer.Serialize(new { ExpenseId = expense.Id, Deleted = true, expense.Amount, expense.Category, expense.Note, expense.CreatedAt })
        });
        _db.Expenses.Remove(expense);
        await _db.SaveChangesAsync(ct);
        await ReplyAsync(seller, MessageTemplates.Get("expenses.014", expense.Id, Formatters.Money(expense.Amount), expense.Note ?? expense.Category), ct);
    }

    /// <summary>Undo of an edit (puts the old amount, category and note back) or of a delete (adds the expense again with its old date).</summary>
    private async Task UndoExpenseChangeAsync(Seller seller, ActionLog log, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(log.PayloadJson);
        var root = doc.RootElement;
        var amount = root.GetProperty("Amount").GetDecimal();
        var category = root.GetProperty("Category").GetString() ?? "other";
        var note = root.GetProperty("Note").GetString();

        if (root.TryGetProperty("Deleted", out var deleted) && deleted.GetBoolean())
        {
            var restored = new Expense { SellerId = seller.Id, Amount = amount, Category = category, Note = note, CreatedAt = root.GetProperty("CreatedAt").GetDateTime() };
            _db.Expenses.Add(restored);
            await _db.SaveChangesAsync(ct);
            await ReplyAsync(seller, MessageTemplates.Get("expenses.015", Formatters.Money(amount), note ?? category), ct);
            return;
        }

        var id = root.GetProperty("ExpenseId").GetInt32();
        var expense = await _db.Expenses.FirstOrDefaultAsync(x => x.Id == id && x.SellerId == seller.Id, ct);
        if (expense is null) return;
        expense.Amount = amount;
        expense.Category = category;
        expense.Note = note;
        await ReplyAsync(seller, MessageTemplates.Get("expenses.016", expense.Id, Formatters.Money(amount), note ?? category), ct);
    }

    private async Task HandleExpenseListAsync(Seller seller, string? period, CancellationToken ct)
    {
        var (start, end, label) = ExpensePeriod(seller, period);
        var expenses = (await LoadExpensesAsync(seller, start, end, ct)).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToList();
        if (expenses.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("expenses.004", label), ct);
            return;
        }

        var tz = SellerClock.Resolve(seller.TimeZoneId);
        const int shown = 15;
        // The #number is the id that "expense edit" and "expense delete" take.
        var lines = expenses.Take(shown).Select(x =>
            $"• #{x.Id} · {TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(x.CreatedAt, DateTimeKind.Utc), tz):dd MMM} — {Formatters.Money(x.Amount)} {x.Note ?? x.Category}");
        var more = expenses.Count > shown ? $"\n…aur {expenses.Count - shown} purane" : "";
        await ReplyAsync(seller,
            MessageTemplates.Get("expenses.005", label, expenses.Count, string.Join("\n", lines), more, Formatters.Money(expenses.Sum(x => x.Amount))), ct);
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
        var lossLine = losses.Count == 0 ? "" : MessageTemplates.Get("expenses.007", Formatters.Money(losses.Sum(x => x.Quantity * x.UnitCost)), losses.Count);

        var top = expenses.GroupBy(x => x.Category).Select(g => (Category: g.Key, Total: g.Sum(x => x.Amount)))
            .OrderByDescending(g => g.Total).Take(3).ToList();
        var topLines = top.Count == 0 ? "" : MessageTemplates.Get("expenses.008", string.Join("\n", top.Select(t => $"• {t.Category}: {Formatters.Money(t.Total)}")));
        var hint = expenses.Count == 0 ? MessageTemplates.Get("expenses.009") : MessageTemplates.Get("expenses.010");

        await ReplyAsync(seller,
            MessageTemplates.Get("expenses.006", (period is null or "month" or "lastmonth" ? "Monthly Net" : "Net"), label, Formatters.Money(sales), orders.Count, Formatters.Money(spent), expenses.Count, wageLine, lossLine, (net < 0 ? "🔻" : "✅"), Formatters.Money(net), topLines, hint), ct);
    }
}
