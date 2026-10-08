using Microsoft.EntityFrameworkCore;
using OrderTrackerBot.Application.Abstractions;
using OrderTrackerBot.Application.Formatting;
using OrderTrackerBot.Domain.Entities;
using OrderTrackerBot.Domain.Enums;

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
                await ReplyAsync(seller, $"Order #{n} nahi mila.", ct);
                return;
            }
        }
        else
        {
            order = await _db.Orders.Include(o => o.Customer).Where(o => o.SellerId == seller.Id).OrderByDescending(o => o.Id).FirstOrDefaultAsync(ct);
            if (order is null)
            {
                await ReplyAsync(seller, "Abhi koi order nahi hai. Pehle \"naya order\" likhein.", ct);
                return;
            }
        }

        var rows = StatusChoices(order);
        if (rows.Count == 0)
        {
            await ReplyAsync(seller, $"Order #{order.Id} {Formatters.Status(order.Status)} hai — iska status ab change nahi ho sakta.", ct);
            return;
        }

        var body = $"📦 Order #{order.Id} ({order.Customer?.Name}) abhi {Formatters.Status(order.Status)} hai.\nNaya status chunein 👇\n\n" +
                   "Kisi aur order ke liye: \"status 12\"";
        await _sender.SendListMessageAsync(seller.WhatsAppPhoneNumber, body, "Status chunein",
            new[] { new MenuSection("Order status", rows) }, ct);
    }
}
