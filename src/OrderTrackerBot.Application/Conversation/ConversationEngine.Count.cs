using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Application.Templates;
using OrderTrackerBot.Domain.Entities;

namespace OrderTrackerBot.Application.Conversation;

/// <summary>
/// "count customers" / "count products" / "count wages" / "count orders" / "count expenses": the number of the seller's own rows (and the total
/// money for wages and expenses). The AI router reaches it with the entity as a slot. Read only.
/// </summary>
public partial class ConversationEngine
{
    private async Task HandleCountAsync(Seller seller, string? what, CancellationToken ct)
    {
        switch (what)
        {
            case "customers":
                await ReplyAsync(seller, MessageTemplates.Get("count.customers", await _db.Customers.CountAsync(c => c.SellerId == seller.Id, ct)), ct);
                return;
            case "products":
                await ReplyAsync(seller, MessageTemplates.Get("count.products", await _db.Products.CountAsync(p => p.SellerId == seller.Id && p.IsActive, ct)), ct);
                return;
            case "wages":
                var wages = await _db.WageEntries.Where(w => w.SellerId == seller.Id).Select(w => w.Amount).ToListAsync(ct);
                await ReplyAsync(seller, MessageTemplates.Get("count.wages", wages.Count, Formatters.Money(wages.Sum())), ct);
                return;
            case "orders":
                await ReplyAsync(seller, MessageTemplates.Get("count.orders", await _db.Orders.CountAsync(o => o.SellerId == seller.Id, ct)), ct);
                return;
            case "expenses":
                var expenses = await _db.Expenses.Where(e => e.SellerId == seller.Id).Select(e => e.Amount).ToListAsync(ct);
                await ReplyAsync(seller, MessageTemplates.Get("count.expenses", expenses.Count, Formatters.Money(expenses.Sum())), ct);
                return;
            default:
                await ReplyAsync(seller, MessageTemplates.Get("count.unknown"), ct);
                return;
        }
    }
}
