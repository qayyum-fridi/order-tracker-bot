using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

namespace OrderTrackerBot.Application.Conversation;

// "sales by category [this week|last month|...]": sales split by the category saved on each product (Kapron, Grocery...), items without one under "Bina category".
// Item prices x quantities of active orders (cancelled / returned excluded); discount and delivery are not spread over items, so the total can differ from today's summary.
// Default window: the last 30 days. Plain SQL, no AI.
public partial class ConversationEngine
{
    private const string NoCategory = "Bina category";

    private async Task HandleCategorySalesAsync(Seller seller, string? period, CancellationToken ct)
    {
        var key = period ?? "30d";
        var (start, end) = ReportRange(seller, key);
        var label = key == "30d" ? "pichle 30 din" : PeriodLabel(seller, key).ToLowerInvariant();

        var rows = await _db.OrderItems.AsNoTracking()
            .Where(i => i.Order!.SellerId == seller.Id && i.Order.CreatedAt >= start && i.Order.CreatedAt < end
                        && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned)
            .Select(i => new { i.ProductId, i.UnitPrice, i.Quantity })
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            await ReplyAsync(seller, $"📊 Category wise sales ({label}): is dauran koi order nahi.", ct);
            return;
        }

        var ids = rows.Where(r => r.ProductId != null).Select(r => r.ProductId!.Value).Distinct().ToList();
        var categories = (await _db.Products.AsNoTracking().Where(p => p.SellerId == seller.Id && ids.Contains(p.Id)).Select(p => new { p.Id, p.Category }).ToListAsync(ct))
            .ToDictionary(p => p.Id, p => string.IsNullOrWhiteSpace(p.Category) ? NoCategory : p.Category!.Trim());

        var groups = rows
            .Select(r => (Category: r.ProductId is { } id && categories.TryGetValue(id, out var c) ? c : NoCategory, Amount: r.UnitPrice * r.Quantity, r.Quantity))
            .GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().Category, Amount: g.Sum(x => x.Amount), Units: g.Sum(x => x.Quantity)))
            .OrderBy(g => g.Name == NoCategory ? 1 : 0).ThenByDescending(g => g.Amount).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lines = groups.Select((g, i) => $"{i + 1}. {g.Name} — {Formatters.Money(g.Amount)} ({g.Units} items)");
        var hint = groups.Any(g => g.Name == NoCategory)
            ? $"\n\nℹ️ \"Bina category\" wale products ki category likhein, jaise: {Industry(seller).MainLine}, category: {Industry(seller).Category}"
            : "";
        await ReplyAsync(seller,
            $"📊 Category wise sales — {label}\n\n{string.Join("\n", lines)}\n\nTotal: {Formatters.Money(groups.Sum(g => g.Amount))}\n" +
            "Yeh products ki qeemat hai (discount aur delivery se pehle), is liye \"summary\" ke total se thora farq ho sakta hai." + hint, ct);
        SuggestNext("today's summary", "margin");
    }
}
