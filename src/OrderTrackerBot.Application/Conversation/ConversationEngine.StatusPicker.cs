using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;
using OrderTrackerBot.Application.Templates;

namespace OrderTrackerBot.Application.Conversation;

// "status" / "status 13": show the statuses an order can move to as a pick-list; each row sends the typed command ("mark 13 delivered").
public partial class ConversationEngine
{
    /// <summary>The status changes offered for an order, as (row id = the typed command, row title). Titles stay within WhatsApp's 24-character limit.</summary>
    internal static List<MenuRow> StatusChoices(Order order)
    {
        var id = order.Id;
        var rows = new List<MenuRow>();
        if (order.Status is OrderStatus.Cancelled or OrderStatus.Returned) return rows;

        if (order.Status != OrderStatus.Shipped) rows.Add(new MenuRow($"mark {id} shipped", "🚚 Shipped"));
        if (order.Status != OrderStatus.Delivered) rows.Add(new MenuRow($"mark {id} delivered", "✅ Delivered (Complete)"));
        if (order.Status is OrderStatus.Shipped or OrderStatus.Delivered) rows.Add(new MenuRow($"mark {id} returned", "↩️ Returned (wapas)"));
        if (order.Status != OrderStatus.Pending) rows.Add(new MenuRow($"mark {id} pending", "⏳ Pending"));
        if (order.PaymentStatus != PaymentStatus.Paid) rows.Add(new MenuRow($"mark {id} paid", "💰 Paid"));
        rows.Add(new MenuRow($"cancel order {id}", "❌ Cancel order"));
        return rows;
    }

    private async Task HandleStatusPickerAsync(Seller seller, SessionContextData ctx, int? number, CancellationToken ct)
    {
        Order? order;
        if (number is { } n)
        {
            var (orderId, _) = ResolveListNumber(ctx, n);
            order = await _db.Orders.Include(o => o.Customer).FirstOrDefaultAsync(o => o.SellerId == seller.Id && o.Id == orderId, ct);
            if (order is null)
            {
                await ReplyAsync(seller, MessageTemplates.Get("commands.028", n), ct);
                return;
            }
        }
        else
        {
            order = await _db.Orders.Include(o => o.Customer).Where(o => o.SellerId == seller.Id).OrderByDescending(o => o.Id).FirstOrDefaultAsync(ct);
            if (order is null)
            {
                await ReplyAsync(seller, MessageTemplates.Get("status-picker.001"), ct);
                return;
            }
        }

        var rows = StatusChoices(order);
        if (rows.Count == 0)
        {
            await ReplyAsync(seller, MessageTemplates.Get("status-picker.002", order.Id, Formatters.Status(order.Status)), ct);
            return;
        }

        var body = MessageTemplates.Get("status-picker.003", order.Id, order.Customer?.Name, Formatters.Status(order.Status));
        await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, body, "Status chunein",
            new[] { new MenuSection("Order status", rows) }, ct);
    }
}
